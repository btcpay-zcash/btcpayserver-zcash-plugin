using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.ZCash.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZcashWalletEventHostedService : IHostedService
    {
        private readonly ZcashLikeConfiguration _zcashLikeConfiguration;
        private readonly ZcashRPCProvider _zcashRpcProvider;
        private readonly EventAggregator _eventAggregator;
        private readonly Logs _logs;
        private CancellationTokenSource _cts;

        public ZcashWalletEventHostedService(ZcashLikeConfiguration zcashLikeConfiguration,
            ZcashRPCProvider zcashRpcProvider,
            EventAggregator eventAggregator,
            Logs logs)
        {
            _zcashLikeConfiguration = zcashLikeConfiguration;
            _zcashRpcProvider = zcashRpcProvider;
            _eventAggregator = eventAggregator;
            _logs = logs;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            foreach (var item in _zcashLikeConfiguration.ZcashLikeConfigurationItems)
            {
                _ = StartLoop(item.Key, _cts.Token);
            }

            return Task.CompletedTask;
        }

        private async Task StartLoop(string cryptoCode, CancellationToken cancellationToken)
        {
            if (_zcashRpcProvider.WalletBackends.TryGetValue(cryptoCode, out var backend)
                && backend is ZkoolGraphQlBackend zkoolBackend)
            {
                _ = zkoolBackend.StartSubscriptionLoopAsync(_eventAggregator, _logs.PayServer, cancellationToken);
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (_zcashRpcProvider.WalletBackends.TryGetValue(cryptoCode, out var walletBackend))
                        {
                            var events = await walletBackend.PollEventsAsync(cancellationToken);
                            foreach (var evt in events)
                            {
                                _eventAggregator.Publish(evt);
                            }
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logs.PayServer.LogError(ex, $"Unhandled exception in wallet event poller ({cryptoCode})");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
