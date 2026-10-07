using System;
using System.Threading;
using System.Collections.Generic;
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
        private readonly List<Task> _loops = new();

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
                _loops.Add(StartLoop(item.Key, _cts.Token));
            }

            return Task.CompletedTask;
        }

        private async Task StartLoop(string cryptoCode, CancellationToken cancellationToken)
        {
            if (_zcashRpcProvider.WalletBackends.TryGetValue(cryptoCode, out var backend)
                && backend is ZkoolGraphQlBackend zkoolBackend)
            {
                _loops.Add(zkoolBackend.StartSubscriptionLoopAsync(_eventAggregator, _logs.PayServer, cancellationToken));
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

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _cts?.Cancel();
                await Task.WhenAll(_loops).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                || _cts?.IsCancellationRequested == true)
            {
                // Cancellation is expected when stopping the wallet loops or
                // when the host's graceful shutdown deadline expires.
            }
            finally
            {
                _cts?.Dispose();
            }
        }
    }
}
