using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
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

        public UIZcashLikeStoreController(ZcashLikeConfiguration ZcashLikeConfiguration,
            StoreRepository storeRepository, ZcashRPCProvider ZcashRpcProvider,
            PaymentMethodHandlerDictionary handlers,
            IStringLocalizer stringLocalizer)
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _StoreRepository = storeRepository;
            _ZcashRpcProvider = ZcashRpcProvider;
            _handlers = handlers;
            StringLocalizer = stringLocalizer;
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
                    GetZcashLikePaymentMethodViewModel(StoreData, pair.Key, excludeFilters,
                        accountsList[pair.Key].Result))
            };
        }

        private Task<GetAccountsResponse> GetAccounts(string cryptoCode)
        {
            try
            {
                if (_ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary) && summary.WalletAvailable)
                {

                    return _ZcashRpcProvider.WalletRpcClients[cryptoCode].SendCommandAsync<GetAccountsRequest, GetAccountsResponse>("get_accounts", new GetAccountsRequest());
                }
            }
            catch { }
            return Task.FromResult<GetAccountsResponse>(null);
        }

        private ZcashLikePaymentMethodViewModel GetZcashLikePaymentMethodViewModel(
            StoreData storeData, string cryptoCode,
            IPaymentFilter excludeFilters, GetAccountsResponse accountsResponse)
        {
            var Zcash = storeData.GetPaymentMethodConfigs(_handlers)
                .Where(s => s.Value is ZcashPaymentPromptDetails)
                .Select(s => (PaymentMethodId: s.Key, Details: (ZcashPaymentPromptDetails)s.Value));
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var settings = Zcash.Where(method => method.PaymentMethodId == pmi).Select(m => m.Details).SingleOrDefault();
            _ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary);
            _ZcashLikeConfiguration.ZcashLikeConfigurationItems.TryGetValue(cryptoCode,
                out var configurationItem);
            var fileAddress = Path.Combine(configurationItem.WalletDirectory, "wallet");
            var accounts = accountsResponse?.SubaddressAccounts?.Select(account =>
                new SelectListItem(
                    $"{account.AccountIndex} - {(string.IsNullOrEmpty(account.Label) ? "No label" : account.Label)}",
                    account.AccountIndex.ToString(CultureInfo.InvariantCulture)));

            var configFile = configurationItem.ConfigFile;

            JsonObject json;
            if (System.IO.File.Exists(configFile))
            {
                using (var fs = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                using (var reader = new StreamReader(fs))
                {
                    var jsonText = reader.ReadToEnd();
                    json = JsonNode.Parse(jsonText)?.AsObject() ?? new JsonObject();
                }
            }
            else
            {
                json = new JsonObject();
            }

            var settlementThresholdChoice = ZcashLikeSettlementThresholdChoice.StoreSpeedPolicy;
            long confirmations = 0;
            bool hasValidConfirmations = false;
            if (json?["confirmations"] is JsonValue valueNode &&
                long.TryParse(valueNode.ToString(), out confirmations))
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

            return new ZcashLikePaymentMethodViewModel()
            {
                WalletFileFound = System.IO.File.Exists(configFile),
                Enabled =
                    // settings != null &&
                    !excludeFilters.Match(PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode)),
                Summary = summary,
                CryptoCode = cryptoCode,
                AccountIndex = settings?.AccountIndex ?? accountsResponse?.SubaddressAccounts?.FirstOrDefault()?.AccountIndex ?? 0,
                Accounts = accounts == null ? null : new SelectList(accounts, nameof(SelectListItem.Value),
                    nameof(SelectListItem.Text)),
                SettlementConfirmationThresholdChoice = settlementThresholdChoice,
                CustomSettlementConfirmationThreshold =
                    // settings != null &&
                    hasValidConfirmations &&
                    settlementThresholdChoice is ZcashLikeSettlementThresholdChoice.Custom
                        ? confirmations
                        : null
            };
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
                StoreData.GetStoreBlob().GetExcludedPaymentMethods(), await GetAccounts(cryptoCode));
            return View("/Views/Zcash/GetStoreZcashLikePaymentMethod.cshtml", vm);
        }

        [DisableRequestSizeLimit]
        [HttpPost("{cryptoCode}")]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethod(ZcashLikePaymentMethodViewModel viewModel, string command, string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            if (!_ZcashLikeConfiguration.ZcashLikeConfigurationItems.TryGetValue(cryptoCode,
                out var configurationItem))
            {
                return NotFound();
            }

            if (command == "add-account")
            {
                try
                {
                    var newAccount = await _ZcashRpcProvider.WalletRpcClients[cryptoCode].SendCommandAsync<CreateAccountRequest, CreateAccountResponse>("create_account", new CreateAccountRequest()
                    {
                        Label = viewModel.NewAccountLabel
                    });
                    viewModel.AccountIndex = newAccount.AccountIndex;
                }
                catch (Exception)
                {
                    ModelState.AddModelError(nameof(viewModel.AccountIndex), StringLocalizer["Could not create a new account."]);
                }

            }
            else if (command == "upload-wallet")
            {
                var valid = true;
                if (viewModel.BirthHeight == null)
                {
                    ModelState.AddModelError(nameof(viewModel.BirthHeight), StringLocalizer["Please enter a viewing key"]);
                    valid = false;
                }
                if (viewModel.WalletPassword == null)
                {
                    ModelState.AddModelError(nameof(viewModel.WalletPassword), StringLocalizer["Please enter a viewing key"]);
                    valid = false;
                }
                if (configurationItem.WalletDirectory == null)
                {
                    ModelState.AddModelError(nameof(viewModel.WalletPassword), StringLocalizer["This installation doesn't support wallet import (BTCPAY_ZEC_WALLET_DAEMON_WALLETDIR is not set)"]);
                    valid = false;
                }

                if (valid)
                {
                    if (_ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary))
                    {
                        if (summary.WalletAvailable)
                        {
                            TempData.SetStatusMessageModel(new StatusMessageModel
                            {
                                Severity = StatusMessageModel.StatusSeverity.Error,
                                Message = StringLocalizer["There is already an active wallet configured for {0}. Replacing it would break any existing invoices!", cryptoCode].Value
                            });
                            return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod),
                                new { cryptoCode });
                        }
                    }

                    var configFile = configurationItem.ConfigFile;

                    JsonObject json;
                    if (System.IO.File.Exists(configFile))
                    {
                        using (var fs = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                        using (var reader = new StreamReader(fs))
                        {
                            var jsonText = await reader.ReadToEndAsync();
                            json = JsonNode.Parse(jsonText)?.AsObject() ?? new JsonObject();
                        }
                    }
                    else
                    {
                        json = new JsonObject();
                    }

                    try
                    {
                        json["vk"] = viewModel.WalletPassword;
                        json["birth_height"] = viewModel.BirthHeight;

                        string jsonOutput = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });

                        using (var fs = new FileStream(configFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                        using (var writer = new StreamWriter(fs))
                        {
                            await writer.WriteAsync(jsonOutput);
                        }

                        Exec($"chmod 666 {configFile}");
                    }
                    catch
                    {
                        ModelState.AddModelError(nameof(viewModel.AccountIndex), StringLocalizer["Could not write wallet file."]);
                    }

                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Info,
                        Message = StringLocalizer["Wallet config uploaded. Please restart the wallet daemon."].Value
                    });
                    return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod), new { cryptoCode });
                }
            }

            if (!ModelState.IsValid)
            {

                var vm = GetZcashLikePaymentMethodViewModel(StoreData, cryptoCode,
                    StoreData.GetStoreBlob().GetExcludedPaymentMethods(), await GetAccounts(cryptoCode));

                vm.Enabled = viewModel.Enabled;
                vm.NewAccountLabel = viewModel.NewAccountLabel;
                vm.AccountIndex = viewModel.AccountIndex;
                vm.SettlementConfirmationThresholdChoice = viewModel.SettlementConfirmationThresholdChoice;
                vm.CustomSettlementConfirmationThreshold = viewModel.CustomSettlementConfirmationThreshold;
                return View("/Views/Zcash/GetStoreZcashLikePaymentMethod.cshtml", vm);
            }

            var storeData = StoreData;
            var blob = storeData.GetStoreBlob();
            storeData.SetPaymentMethodConfig(_handlers[PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode)], new ZcashPaymentPromptDetails()
            {
                AccountIndex = viewModel.AccountIndex
            });

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
                    // Handle null case if needed, or throw
                    throw new InvalidOperationException("Invalid settlement confirmation threshold.");
                }

                Exec($"chmod 666 {fileConfig}");
            }
            catch
            {
                ModelState.AddModelError(nameof(viewModel.AccountIndex), StringLocalizer["Could not write wallet file."]);
            }

            blob.SetExcluded(PaymentTypes.CHAIN.GetPaymentMethodId(viewModel.CryptoCode), !viewModel.Enabled);
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
            public string NewAccountLabel { get; set; }
            public long AccountIndex { get; set; }
            public bool Enabled { get; set; }

            public IEnumerable<SelectListItem> Accounts { get; set; }
            public bool WalletFileFound { get; set; }
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
