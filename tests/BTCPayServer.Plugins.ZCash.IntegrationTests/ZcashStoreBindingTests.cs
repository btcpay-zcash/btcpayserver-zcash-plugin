using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.ZCash.Payments;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashStoreBindingTests
{
    private static async Task<PaymentMethodConfigValidationContext> Validate(JObject? previous, JObject update)
    {
        var handler = new ZcashLikePaymentMethodHandler(new ZcashLikeSpecificBtcPayNetwork { CryptoCode = "ZEC" }, null!);
        // BTCPay 2.4 adds a StoreData argument. The validator uses the same
        // model state and config properties on both supported host versions.
        var constructor = Assert.Single(typeof(PaymentMethodConfigValidationContext).GetConstructors());
        object?[] arguments = [null, new ModelStateDictionary(), update, null, previous];
        var context = (PaymentMethodConfigValidationContext)constructor.Invoke(
            constructor.GetParameters().Length == 5 ? arguments : [.. arguments, new BTCPayServer.Data.StoreData()]);
        await handler.ValidatePaymentMethodConfig(context);
        return context;
    }

    [Theory]
    [InlineData("accountIndex")]
    [InlineData("viewingKeyHash")]
    [InlineData("viewingKey")]
    [InlineData("birthHeight")]
    public async Task ApiRejectsClientAssignedBindings(string property)
    {
        var update = new JObject { [property] = property is "accountIndex" or "birthHeight" ? new JValue(6) : new JValue("new value") };
        Assert.False((await Validate(null, update)).ModelState.IsValid);
        Assert.False((await Validate(new JObject { ["accountIndex"] = 5 }, update)).ModelState.IsValid);
    }

    [Fact]
    public async Task ApiPreservesBindingWhenUpdatingConfirmations()
    {
        var previous = new JObject { ["accountIndex"] = 5, ["viewingKeyHash"] = "existing" };
        var context = await Validate(previous, new JObject { ["invoiceSettledConfirmationThreshold"] = 2 });
        Assert.True(context.ModelState.IsValid);
        Assert.Equal(5, context.Config["accountIndex"]!.Value<int>());
        Assert.Equal("existing", context.Config["viewingKeyHash"]!.Value<string>());
        Assert.Equal(2, context.Config["invoiceSettledConfirmationThreshold"]!.Value<int>());
    }
}
