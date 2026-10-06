using System.Threading.Tasks;

using BTCPayServer.Payments;
using BTCPayServer.Plugins.ZCash.RPC;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

// using BTCPayServer.Plugins.ZCash.RPC.Models;

namespace BTCPayServer.Plugins.ZCash.Services;

public class GenerateBlocksNoAddress
{
    [JsonProperty("nblocks")]
    public int AmountOfBlocks { get; set; }
}

public class ZcashCheckoutCheatModeExtension : ICheckoutCheatModeExtension
{
    private readonly ZcashRPCProvider _rpcProvider;
    // private readonly ZkoolGraphQlClient _graphQlClient;
    private readonly ZcashLikeSpecificBtcPayNetwork _network;
    private readonly PaymentMethodId _paymentMethodId;

    public ZcashCheckoutCheatModeExtension(
        ZcashRPCProvider rpcProvider,
        // ZkoolGraphQlClient graphQlClient,
        ZcashLikeSpecificBtcPayNetwork network,
        PaymentMethodId paymentMethodId)
    {
        _rpcProvider = rpcProvider;
        // _graphQlClient = graphQlClient;
        _network = network;
        _paymentMethodId = paymentMethodId;
    }

    public bool Handle(PaymentMethodId paymentMethodId) => _paymentMethodId == paymentMethodId;
    

    public async Task<ICheckoutCheatModeExtension.PayInvoiceResult> PayInvoice(
        ICheckoutCheatModeExtension.PayInvoiceContext payInvoiceContext)
    {


        var cashcow = _rpcProvider.CashCowWalletGraphQlClients[_network.CryptoCode.ToUpperInvariant()];
        var accountId = 1;
        
        await cashcow.SendAsync(@"
mutation($idAccounts: [Int!]!) {
  synchronize(idAccounts: $idAccounts)
}", new { idAccounts = accountId });

        var payResult = await cashcow.SendAsync(
            @"mutation Pay($id: Int!, $payment: Payment!) {
                pay(idAccount: $id, payment: $payment)
            }",
            new
            {
                id = accountId,
                payment = new
                {
                    recipients = new[]
                    {
                        new
                        {
                            address = payInvoiceContext.PaymentPrompt.Destination,
                            amount = payInvoiceContext.Amount
                        }
                    }
                }
            });

        // return payResult["pay"]?.Value<long>() ?? 0;
        var txId = payResult["pay"]!.Value<string>();
        return new ICheckoutCheatModeExtension.PayInvoiceResult(txId);
    }

    public async Task<ICheckoutCheatModeExtension.MineBlockResult> MineBlock(
        ICheckoutCheatModeExtension.MineBlockContext mineBlockContext)
    {
        var daemon = _rpcProvider.CashcowDaemonRpcClients[_network.CryptoCode];
        var cashcow = _rpcProvider.CashCowWalletGraphQlClients[_network.CryptoCode.ToUpperInvariant()];


        var before = await cashcow.SendAsync("query { currentHeight }");
        var target = before["currentHeight"]!.Value<long>() + mineBlockContext.BlockCount;
        await daemon.SendRpc10CommandAsync<int[], string[]>("generate", new[] { mineBlockContext.BlockCount });
        // Zebra acknowledges mining before lightwalletd necessarily serves those blocks.
        await ZcashRPCProvider.WaitForWalletHeightAsync(cashcow, target);
        await cashcow.SendAsync(@"
mutation($idAccounts: [Int!]!) {
  synchronize(idAccounts: $idAccounts)
}", new { idAccounts = 1 });

        return new ICheckoutCheatModeExtension.MineBlockResult();
    }
}
