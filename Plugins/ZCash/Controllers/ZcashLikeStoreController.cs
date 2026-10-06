using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.ZCash.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.Payments;
using BTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.ZCash.Controllers
{
    [Route("stores/{storeId}/Zcashlike")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class UIZcashLikeStoreController : Controller
    {
        private readonly ZcashLikeConfiguration _ZcashLikeConfiguration;
        private readonly StoreRepository _StoreRepository;
        private readonly ZcashRPCProvider _ZcashRpcProvider;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private IStringLocalizer StringLocalizer { get; }
        private readonly ZcashPluginDbContextFactory _dbContextFactory;

        public UIZcashLikeStoreController(ZcashLikeConfiguration ZcashLikeConfiguration,
            StoreRepository storeRepository, ZcashRPCProvider ZcashRpcProvider,
            PaymentMethodHandlerDictionary handlers,
            IStringLocalizer stringLocalizer, ZcashPluginDbContextFactory dbContextFactory)
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _StoreRepository = storeRepository;
            _ZcashRpcProvider = ZcashRpcProvider;
            _handlers = handlers;
            StringLocalizer = stringLocalizer;
            _dbContextFactory = dbContextFactory;
        }

        public StoreData StoreData => HttpContext.GetStoreData();

        [HttpGet()]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethods()
        {
            return View("/Views/Zcash/GetStoreZcashLikePaymentMethods.cshtml", await GetVM(StoreData));
        }
        [NonAction]
        public async Task<ZcashLikePaymentMethodListViewModel> GetVM(StoreData storeData)
        {
            var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();

            var accountsList = _ZcashLikeConfiguration.ZcashLikeConfigurationItems.ToDictionary(pair => pair.Key,
                pair => GetAccounts(pair.Key));

            await Task.WhenAll(accountsList.Values);
            return new ZcashLikePaymentMethodListViewModel()
            {
                Items = _ZcashLikeConfiguration.ZcashLikeConfigurationItems.Select(pair =>
                    GetZcashLikePaymentMethodViewModel(StoreData, pair.Key, excludeFilters, accountsList[pair.Key].Result))
            };
        }

        [NonAction]
        public ZcashLikePaymentMethodListViewModel GetNavVM(StoreData storeData)
        {
            var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();

            return new ZcashLikePaymentMethodListViewModel
            {
                Items = _ZcashLikeConfiguration.ZcashLikeConfigurationItems.Select(pair =>
                {
                    var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(pair.Key);
                    return new ZcashLikePaymentMethodViewModel
                    {
                        CryptoCode = pair.Key,
                        Enabled = !excludeFilters.Match(paymentMethodId)
                    };
                })
            };
        }

        private Task<IReadOnlyList<WalletAccount>> GetAccounts(string cryptoCode)
        {
            try
            {
                if (_ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary) && summary.WalletAvailable)
                {
                    return _ZcashRpcProvider.WalletBackends[cryptoCode].GetAccountsAsync();
                }
            }
            catch { }
            return Task.FromResult<IReadOnlyList<WalletAccount>>(null);
        }

        private ZcashLikePaymentMethodViewModel GetZcashLikePaymentMethodViewModel(
            StoreData storeData, string cryptoCode, IPaymentFilter excludeFilters, IReadOnlyList<WalletAccount> accounts = null)
        {
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var config = storeData.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers);

            _ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary);
            _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                .TryGetValue(cryptoCode, out var configurationItem);

            var settlementThresholdChoice = ZcashLikeSettlementThresholdChoice.StoreSpeedPolicy;
            bool hasValidConfirmations = false;
            if (config?.InvoiceSettledConfirmationThreshold is { } confirmations)
            {
                hasValidConfirmations = true;
                settlementThresholdChoice = confirmations switch
                {
                    0 => ZcashLikeSettlementThresholdChoice.ZeroConfirmation,
                    1 => ZcashLikeSettlementThresholdChoice.AtLeastOne,
                    6 => ZcashLikeSettlementThresholdChoice.AtLeastSix,
                    _ => ZcashLikeSettlementThresholdChoice.Custom
                };
            }

            return new ZcashLikePaymentMethodViewModel
            {
                UsesWalletFile = configurationItem?.WalletBackend == WalletBackend.Walletd,
                WalletFileFound = configurationItem?.WalletBackend != WalletBackend.Walletd || System.IO.File.Exists(configurationItem?.ConfigFile),
                Enabled = config?.AccountIndex is not null && !excludeFilters.Match(pmi),
                Summary = summary,
                CryptoCode = cryptoCode,
                AccountIndex = config?.AccountIndex,
                Accounts = accounts?.Select(account => new SelectListItem(
                    string.IsNullOrEmpty(account.Label)
                        ? $"Account #{account.AccountIndex}"
                        : $"{account.Label} (#{account.AccountIndex})",
                    account.AccountIndex.ToString(CultureInfo.InvariantCulture))),
                SettlementConfirmationThresholdChoice = settlementThresholdChoice,
                CustomSettlementConfirmationThreshold =
                    hasValidConfirmations &&
                    settlementThresholdChoice is ZcashLikeSettlementThresholdChoice.Custom
                        ? config.InvoiceSettledConfirmationThreshold
                        : null
            };
        }

        [HttpGet("~/stores/{storeId}/onchain/{cryptoCode}")]
        public IActionResult OnchainRedirect(string storeId, string cryptoCode)
        {
            return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod), new { storeId, cryptoCode });
        }

        [HttpGet("{cryptoCode}")]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethod(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_ZcashLikeConfiguration.ZcashLikeConfigurationItems.ContainsKey(cryptoCode))
            {
                return NotFound();
            }

            var vm = GetZcashLikePaymentMethodViewModel(StoreData, cryptoCode,
                StoreData.GetStoreBlob().GetExcludedPaymentMethods(),
                await GetAccounts(cryptoCode));
            return View("/Views/Zcash/GetStoreZcashLikePaymentMethod.cshtml", vm);
        }

        [DisableRequestSizeLimit]
        [HttpPost("{cryptoCode}")]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethod(ZcashLikePaymentMethodViewModel viewModel, string command, string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var store = StoreData;
            var config = store.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers)
                 ?? new ZcashPaymentMethodConfig();
            if (!_ZcashLikeConfiguration.ZcashLikeConfigurationItems.TryGetValue(cryptoCode,
                out var configurationItem))
            {
                return NotFound();
            }

            if (command == "add-account")
            {
                if (config.AccountIndex is not null)
                {
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Error,
                        Message = StringLocalizer["This store already has an account configured."].Value
                    });
                    return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod),
                        new { storeId = store.Id, cryptoCode });
                }

                if (string.IsNullOrWhiteSpace(viewModel.WalletPassword))
                    ModelState.AddModelError(nameof(viewModel.WalletPassword),
                        StringLocalizer["A viewing key is required."]);

                if (ModelState.IsValid)
                {
                    try
                    {
                        var key = viewModel.WalletPassword.Trim();
                        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                        // Serialize imports across requests and server instances sharing this database.
                        await using var db = _dbContextFactory.CreateContext();
                        await using var importLock = await db.Database.BeginTransactionAsync();
                        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(802469320125)");
                        var stores = await _StoreRepository.GetStores();
                        foreach (var existingStore in stores)
                        {
                            var existing = existingStore.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers);
                            if (existingStore.Id == store.Id && existing?.AccountIndex != null)
                                throw new InvalidOperationException("This store already has an account configured.");
                            var existingHash = existing?.ViewingKeyHash;
                            if (existingHash == null && !string.IsNullOrWhiteSpace(existing?.ViewingKey))
                                existingHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(existing.ViewingKey.Trim())));
                            if (existingHash == keyHash)
                                throw new InvalidOperationException("This viewing key is already assigned to a store. Use a different viewing key.");
                        }
                        var newAccount = await _ZcashRpcProvider.WalletBackends[cryptoCode]
                            .CreateAccountAsync(new WalletAccountCreationRequest
                            {
                                Key = key,
                                BirthHeight = viewModel.BirthHeight,
                                Label = $"store:{StoreData.Id}"
                            });
                        // Backend retries recover the account by key and store label if this save fails.
                        config.AccountIndex = newAccount.AccountIndex;
                        config.ViewingKeyHash = keyHash;
                        config.ViewingKey = null;
                        config.BirthHeight = viewModel.BirthHeight;
                        store.SetPaymentMethodConfig(_handlers[pmi], config);
                        await _StoreRepository.UpdateStore(store);
                        await importLock.CommitAsync();
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Success,
                            Message = StringLocalizer["Account #{0} created for this store.",
                                newAccount.AccountIndex].Value
                        });
                        return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod),
                            new { storeId = StoreData.Id, cryptoCode });
                    }
                    catch (InvalidOperationException ex)
                    {
                        ModelState.AddModelError(nameof(viewModel.WalletPassword), ex.Message);
                    }
                    catch (Exception)
                    {
                        ModelState.AddModelError(nameof(viewModel.WalletPassword), StringLocalizer["Could not create a new account."]);
                    }
                }

            }

            if (config.AccountIndex is null && viewModel.Enabled)
            {
                ModelState.AddModelError(nameof(viewModel.AccountIndex), 
                    "An account must be created before enabling this payment method.");
            }

            if (!ModelState.IsValid)
            {

                var vm = GetZcashLikePaymentMethodViewModel(StoreData, cryptoCode,
                    StoreData.GetStoreBlob().GetExcludedPaymentMethods(),
                    await GetAccounts(cryptoCode));

                vm.Enabled = viewModel.Enabled;
                // vm.NewAccountLabel = viewModel.NewAccountLabel;
                vm.AccountIndex = config.AccountIndex;
                vm.SettlementConfirmationThresholdChoice = viewModel.SettlementConfirmationThresholdChoice;
                vm.CustomSettlementConfirmationThreshold = viewModel.CustomSettlementConfirmationThreshold;
                return View("/Views/Zcash/GetStoreZcashLikePaymentMethod.cshtml", vm);
            }

            var storeData = StoreData;
            var blob = storeData.GetStoreBlob();
            storeData.SetPaymentMethodConfig(_handlers[PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode)], new ZcashPaymentMethodConfig()
            {
                AccountIndex = config.AccountIndex,
                ViewingKeyHash = config.ViewingKeyHash,
                ViewingKey = config.ViewingKey, // Preserve legacy keys until an explicit successful import.
                BirthHeight = config.BirthHeight,
                InvoiceSettledConfirmationThreshold = viewModel.SettlementConfirmationThresholdChoice switch
                {
                    ZcashLikeSettlementThresholdChoice.ZeroConfirmation => 0,
                    ZcashLikeSettlementThresholdChoice.AtLeastOne => 1,
                    ZcashLikeSettlementThresholdChoice.AtLeastSix => 6,
                    ZcashLikeSettlementThresholdChoice.Custom when viewModel.CustomSettlementConfirmationThreshold is { } custom => custom,
                    _ => null
                }
            });

            if (configurationItem.WalletBackend == WalletBackend.Walletd)
            {
                var fileConfig = configurationItem.ConfigFile;

                JsonObject jsonObj;
                if (System.IO.File.Exists(fileConfig))
                {
                    using (var fs = new FileStream(fileConfig, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                    using (var reader = new StreamReader(fs))
                    {
                        var jsonText = await reader.ReadToEndAsync();
                        jsonObj = JsonNode.Parse(jsonText)?.AsObject() ?? new JsonObject();
                    }
                }
                else
                {
                    jsonObj = new JsonObject();
                }

                long? confirmations = viewModel.SettlementConfirmationThresholdChoice switch
                {
                    ZcashLikeSettlementThresholdChoice.ZeroConfirmation => 0,
                    ZcashLikeSettlementThresholdChoice.AtLeastOne => 1,
                    ZcashLikeSettlementThresholdChoice.AtLeastSix => 6,
                    ZcashLikeSettlementThresholdChoice.Custom when viewModel.CustomSettlementConfirmationThreshold is { } custom => custom,
                    _ => null
                };

                try
                {
                    if (confirmations.HasValue)
                    {
                        jsonObj["confirmations"] = confirmations.Value;
                        string jsonOutput = JsonSerializer.Serialize(jsonObj, new JsonSerializerOptions { WriteIndented = true });

                        using (var fs = new FileStream(fileConfig, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                        using (var writer = new StreamWriter(fs))
                        {
                            await writer.WriteAsync(jsonOutput);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Invalid settlement confirmation threshold.");
                    }

                    Exec($"chmod 666 {fileConfig}");
                }
                catch
                {
                    ModelState.AddModelError(nameof(viewModel.AccountIndex), StringLocalizer["Could not write wallet file."]);
                }
            }

            blob.SetExcluded(pmi, !viewModel.Enabled);
            storeData.SetStoreBlob(blob);
            await _StoreRepository.UpdateStore(storeData);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Info,
                Message = StringLocalizer[$"{cryptoCode} settings updated successfully"].Value
            });
            return RedirectToAction(nameof(GetStoreZcashLikePaymentMethods), new { storeId = StoreData.Id });
        }

        private void Exec(string cmd)
        {

            var escapedArgs = cmd.Replace("\"", "\\\"", StringComparison.InvariantCulture);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{escapedArgs}\""
                }
            };

#pragma warning disable CA1416 // Validate platform compatibility
            process.Start();
#pragma warning restore CA1416 // Validate platform compatibility
            process.WaitForExit();
        }

        public class ZcashLikePaymentMethodListViewModel
        {
            public IEnumerable<ZcashLikePaymentMethodViewModel> Items { get; set; }
        }

        public class ZcashLikePaymentMethodViewModel : IValidatableObject
        {
            public ZcashRPCProvider.ZcashLikeSummary Summary { get; set; }
            public string CryptoCode { get; set; }
            // public string NewAccountLabel { get; set; }
            [BindNever]
            public long? AccountIndex { get; set; }
            public bool Enabled { get; set; }

            public IEnumerable<SelectListItem> Accounts { get; set; }
            public bool UsesWalletFile { get; set; }
            public bool WalletFileFound { get; set; }
            [Range(0, int.MaxValue)]
            [Display(Name = "Birth Height")]
            public long? BirthHeight { get; set; }
            [Display(Name = "Wallet Viewing Key")]
            public string WalletPassword { get; set; }
            [Display(Name = "Consider the invoice settled (confirmed) when the payment transaction …")]
            public ZcashLikeSettlementThresholdChoice SettlementConfirmationThresholdChoice { get; set; }
            [Display(Name = "Required Confirmations"), Range(0, 100)]
            public long? CustomSettlementConfirmationThreshold { get; set; }

            public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
            {
                if (SettlementConfirmationThresholdChoice is ZcashLikeSettlementThresholdChoice.Custom
                    && CustomSettlementConfirmationThreshold is null)
                {
                    yield return new ValidationResult(
                        "You must specify the number of required confirmations when using a custom threshold.",
                        new[] { nameof(CustomSettlementConfirmationThreshold) });
                }
            }
        }


        public enum ZcashLikeSettlementThresholdChoice
        {
            [Display(Name = "Store Speed Policy", Description = "Use the store's speed policy")]
            StoreSpeedPolicy,
            [Display(Name = "Zero Confirmation", Description = "Is unconfirmed")]
            ZeroConfirmation,
            [Display(Name = "At Least One", Description = "Has at least 1 confirmation")]
            AtLeastOne,
            [Display(Name = "At Least Six", Description = "Has at least 6 confirmations")]
            AtLeastSix,
            [Display(Name = "Custom", Description = "Custom")]
            Custom
        }
    }
}
