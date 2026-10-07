using System.Reflection;
using BTCPayServer.Plugins.ZCash.Services;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashWalletShutdownTests
{
    private static ZcashWalletEventHostedService CreateService(Task loop)
    {
        var service = new ZcashWalletEventHostedService(null!, null!, null!, null!);
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ZcashWalletEventHostedService).GetField("_cts", fields)!
            .SetValue(service, new CancellationTokenSource());
        var loops = (List<Task>)typeof(ZcashWalletEventHostedService).GetField("_loops", fields)!
            .GetValue(service)!;
        loops.Add(loop);
        return service;
    }

    [Fact]
    public async Task CanceledWalletLoopDoesNotEscapeShutdown()
    {
        var service = CreateService(Task.FromCanceled(new CancellationToken(true)));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExpiredHostDeadlineDoesNotEscapeShutdown()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(pending.Task);
        await service.StopAsync(new CancellationToken(true));
        pending.SetResult();
    }

    [Fact]
    public async Task UnexpectedWalletFaultStillEscapesShutdown()
    {
        var failure = new InvalidOperationException("wallet fault");
        var service = CreateService(Task.FromException(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StopAsync(CancellationToken.None)));
    }
}
