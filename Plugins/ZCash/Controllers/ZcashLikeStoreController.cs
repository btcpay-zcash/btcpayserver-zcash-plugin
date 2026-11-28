using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Events;
using Microsoft.EntityFrameworkCore;
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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ZCash.Controllers
{
    [Route("stores/{storeId}/Zcashlike")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class UIZcashLikeStoreController : Controller
    {
        private readonly ZcashLikeConfiguration _ZcashLikeConfiguration;
        private readonly StoreRepository _StoreRepository;
        private readonly ZcashRPCProvider _ZcashRpcProvider;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private IStringLocalizer StringLocalizer { get; }
        private const string LegacyNoticeDismissed = "BTCPayServer.Plugins.ZCash.LegacyWalletNoticeDismissed";
        private readonly ApplicationDbContextFactory _dbContextFactory;
        private readonly EventAggregator _events;
        private readonly ILogger<UIZcashLikeStoreController> _logger;

        public UIZcashLikeStoreController(ZcashLikeConfiguration ZcashLikeConfiguration,
            StoreRepository storeRepository, ZcashRPCProvider ZcashRpcProvider,
            PaymentMethodHandlerDictionary handlers,
            IStringLocalizer stringLocalizer, ApplicationDbContextFactory dbContextFactory, EventAggregator events,
            ILogger<UIZcashLikeStoreController> logger)
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _StoreRepository = storeRepository;
            _ZcashRpcProvider = ZcashRpcProvider;
            _handlers = handlers;
            StringLocalizer = stringLocalizer;
            _dbContextFactory = dbContextFactory;
            _events = events;
            _logger = logger;
        }

        public StoreData StoreData => HttpContext.GetStoreData();

        [HttpGet()]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethods()
        {
            var vm = GetNavVM(StoreData);
            foreach (var item in vm.Items)
                await VerifyBindingAsync(item);
            return View("/Views/ZCash/GetStoreZcashLikePaymentMethods.cshtml", vm);
        }

        [NonAction]
        public ZcashLikePaymentMethodListViewModel GetNavVM(StoreData storeData)
        {
            var excluded = storeData.GetStoreBlob().GetExcludedPaymentMethods();
            return new ZcashLikePaymentMethodListViewModel
            {
                Items = _ZcashLikeConfiguration.ZcashLikeConfigurationItems.Keys.Select(code =>
                    GetZcashLikePaymentMethodViewModel(storeData, code, excluded)).ToList()
            };
        }

        private ZcashLikePaymentMethodViewModel GetZcashLikePaymentMethodViewModel(
            StoreData storeData, string cryptoCode, IPaymentFilter excludeFilters)
        {
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var config = storeData.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers);

            _ZcashRpcProvider.Summaries.TryGetValue(cryptoCode, out var summary);

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
                Enabled = config?.AccountIndex is not null && !excludeFilters.Match(pmi),
                Summary = summary,
                CryptoCode = cryptoCode,
                AccountIndex = config?.AccountIndex,
                SettlementConfirmationThresholdChoice = settlementThresholdChoice,
                CustomSettlementConfirmationThreshold =
                    hasValidConfirmations &&
                    settlementThresholdChoice is ZcashLikeSettlementThresholdChoice.Custom
                        ? config.InvoiceSettledConfirmationThreshold
                        : null
            };
        }

        private async Task VerifyBindingAsync(ZcashLikePaymentMethodViewModel vm)
        {
            if (vm.AccountIndex is not { } id)
            {
                if (_ZcashLikeConfiguration.ZcashLikeConfigurationItems[vm.CryptoCode].HasLegacyWalletDatabase)
                {
                    await using var db = _dbContextFactory.CreateContext();
                    vm.ShowLegacyWalletNotice = !await db.Settings.AnyAsync(s => s.Id == LegacyNoticeDismissed);
                }
                return;
            }
            var config = StoreData.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(
                PaymentTypes.CHAIN.GetPaymentMethodId(vm.CryptoCode), _handlers);
            try
            {
                if (_ZcashRpcProvider.WalletBackends[vm.CryptoCode] is ZkoolGraphQlBackend wallet &&
                    await wallet.IsStoreAccountAsync(StoreData.Id, id, config.GetKeyHash()))
                    return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not verify the receiving account for store {StoreId}", StoreData.Id);
            }
            vm.AccountIndex = null;
            vm.Enabled = false;
            vm.BindingUnavailable = true;
        }

        // Core's UIStoreOnChainWalletsController.SetupWallet serves this template for
        // every other coin. Unconstrained, both endpoints match every request and
        // routing fails with AmbiguousMatchException (HTTP 500).
        [HttpGet("~/stores/{storeId}/onchain/{cryptoCode:regex(^zec$)}")]
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
                StoreData.GetStoreBlob().GetExcludedPaymentMethods());
            await VerifyBindingAsync(vm);
            return View("/Views/ZCash/GetStoreZcashLikePaymentMethod.cshtml", vm);
        }

        [HttpPost("{cryptoCode}")]
        public async Task<IActionResult> GetStoreZcashLikePaymentMethod(ZcashLikePaymentMethodViewModel viewModel, string command, string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var store = StoreData;
            var config = store.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers)
                 ?? new ZcashPaymentMethodConfig();
            if (!_ZcashRpcProvider.IsConfigured(cryptoCode))
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
                        var keyHash = ZcashPaymentMethodConfig.HashViewingKey(key);
                        // Serialize imports across requests and server instances sharing this database.
                        await using var db = _dbContextFactory.CreateContext();
                        long createdAccountIndex = 0;
                        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                        {
                            db.ChangeTracker.Clear();
                            await using var importLock = await db.Database.BeginTransactionAsync();
                            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(802469320125)");
                            var stores = await db.Stores.ToListAsync();
                            store = stores.Single(s => s.Id == StoreData.Id);
                            foreach (var existingStore in stores)
                            {
                                var existing = existingStore.GetPaymentMethodConfig<ZcashPaymentMethodConfig>(pmi, _handlers);
                                if (existingStore.Id == store.Id && existing?.AccountIndex != null)
                                    throw new InvalidOperationException("This store already has an account configured.");
                                if (string.Equals(existing?.GetKeyHash(), keyHash, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("This viewing key is already assigned to a store. Use a different viewing key.");
                            }
                            var newAccount = await _ZcashRpcProvider.WalletBackends[cryptoCode]
                                .CreateAccountAsync(new WalletAccountCreationRequest
                                {
                                    Key = key,
                                    BirthHeight = viewModel.BirthHeight,
                                    Label = ZkoolGraphQlBackend.StoreAccountLabel(store.Id)
                                });
                            config.AccountIndex = newAccount.AccountIndex;
                            config.ViewingKeyHash = keyHash;
                            config.ViewingKey = null;
                            config.BirthHeight = viewModel.BirthHeight;
                            store.SetPaymentMethodConfig(_handlers[pmi], config);
                            if (_ZcashLikeConfiguration.ZcashLikeConfigurationItems[cryptoCode].HasLegacyWalletDatabase &&
                                !await db.Settings.AnyAsync(s => s.Id == LegacyNoticeDismissed))
                                db.Settings.Add(new SettingData { Id = LegacyNoticeDismissed, Value = "true" });
                            await db.SaveChangesAsync();
                            await importLock.CommitAsync();
                            createdAccountIndex = newAccount.AccountIndex;
                        });
                        _events.Publish(new StoreEvent.Updated(StoreData));
                        TempData.SetStatusMessageModel(new StatusMessageModel
                        {
                            Severity = StatusMessageModel.StatusSeverity.Success,
                            Message = StringLocalizer["Account #{0} created for this store.",
                                createdAccountIndex].Value
                        });
                        return RedirectToAction(nameof(GetStoreZcashLikePaymentMethod),
                            new { storeId = StoreData.Id, cryptoCode });
                    }
                    catch (InvalidOperationException ex)
                    {
                        ModelState.AddModelError(nameof(viewModel.WalletPassword), ex.Message);
                    }
                    catch (ZkoolGraphQlClient.GraphQlApiException ex)
                    {
                        _logger.LogError(ex, "GraphQL wallet account creation failed for store {StoreId}", store.Id);
                        ModelState.AddModelError(nameof(viewModel.WalletPassword),
                            StringLocalizer["The wallet could not import this viewing key. Check its network and whether it is already assigned."]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Wallet account creation failed for store {StoreId}", store.Id);
                        ModelState.AddModelError(nameof(viewModel.WalletPassword), StringLocalizer["Could not create a new account."]);
                    }
                }

            }

            if (viewModel.Enabled && config.AccountIndex is not null)
            {
                var binding = GetZcashLikePaymentMethodViewModel(store, cryptoCode, store.GetStoreBlob().GetExcludedPaymentMethods());
                await VerifyBindingAsync(binding);
                if (binding.BindingUnavailable)
                    ModelState.AddModelError(string.Empty, "The wallet could not verify this store's receiving account.");
            }
            if (config.AccountIndex is null && viewModel.Enabled)
            {
                ModelState.AddModelError(nameof(viewModel.AccountIndex), 
                    "An account must be created before enabling this payment method.");
            }

            if (!ModelState.IsValid)
            {

                var vm = GetZcashLikePaymentMethodViewModel(StoreData, cryptoCode,
                    StoreData.GetStoreBlob().GetExcludedPaymentMethods());

                vm.Enabled = viewModel.Enabled;
                await VerifyBindingAsync(vm);
                vm.SettlementConfirmationThresholdChoice = viewModel.SettlementConfirmationThresholdChoice;
                vm.CustomSettlementConfirmationThreshold = viewModel.CustomSettlementConfirmationThreshold;
                return View("/Views/ZCash/GetStoreZcashLikePaymentMethod.cshtml", vm);
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

        public class ZcashLikePaymentMethodListViewModel
        {
            public IEnumerable<ZcashLikePaymentMethodViewModel> Items { get; set; }
        }

        public class ZcashLikePaymentMethodViewModel : IValidatableObject
        {
            public ZcashRPCProvider.ZcashLikeSummary Summary { get; set; }
            public string CryptoCode { get; set; }
            [BindNever]
            public long? AccountIndex { get; set; }
            public bool Enabled { get; set; }
            [BindNever]
            public bool BindingUnavailable { get; set; }
            [BindNever]
            public bool ShowLegacyWalletNotice { get; set; }

            [Range(0, int.MaxValue)]
            [Display(Name = "Birth Height")]
            public long? BirthHeight { get; set; }
            [Display(Name = "Unified full viewing key")]
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
