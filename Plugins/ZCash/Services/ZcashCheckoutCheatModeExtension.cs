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
    private readonly ZkoolGraphQlClient _graphQlClient;
    private readonly ZcashLikeSpecificBtcPayNetwork _network;
    private readonly PaymentMethodId _paymentMethodId;

    public ZcashCheckoutCheatModeExtension(
        ZcashRPCProvider rpcProvider,
        ZkoolGraphQlClient graphQlClient,
        ZcashLikeSpecificBtcPayNetwork network,
        PaymentMethodId paymentMethodId)
    {
        _rpcProvider = rpcProvider;
        _graphQlClient = graphQlClient;
        _network = network;
        _paymentMethodId = paymentMethodId;
    }

    public bool Handle(PaymentMethodId paymentMethodId) => _paymentMethodId == paymentMethodId;
    
    private async Task CreateTestWallet(string label, string key, string passphrase, long birthHeight, bool useInternalAddresses)
    {
        var data = await _graphQlClient.SendAsync(@"
mutation($newAccount: NewAccount!) {
  createAccount(newAccount: $newAccount)
}", new
        {
            newAccount = new
            {
                name = label,
                key = key,
                passphrase = passphrase,
                aindex = 0,
                birth = birthHeight,
                useInternal = useInternalAddresses
            }
        });


        var createdId = data["createAccount"]!.Value<long>();
    }

    public async Task<ICheckoutCheatModeExtension.PayInvoiceResult> PayInvoice(
        ICheckoutCheatModeExtension.PayInvoiceContext payInvoiceContext)
    {
        var amount = payInvoiceContext.Amount;
        for (int i = 0; i < _network.Divisibility; i++)
        {
            amount *= 10;
        }

        // var cashcow = _rpcProvider.CashCowWalletGqlClients[_network.CryptoCode];
        // var accountId = _rpcProvider.CashCowAccountIds[_network.CryptoCode];
        var accountId = 0;

        var payResult = await _graphQlClient.SendAsync(
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
                            amount = (long)amount
                        }
                    },
                    recipientPaysFee = true
                }
            });

        // return payResult["pay"]?.Value<long>() ?? 0;
        var txId = payResult["pay"]!.Value<string>();
        return new ICheckoutCheatModeExtension.PayInvoiceResult(txId);
    }

    public async Task<ICheckoutCheatModeExtension.MineBlockResult> MineBlock(
        ICheckoutCheatModeExtension.MineBlockContext mineBlockContext)
    {
        var daemon = _rpcProvider.DaemonRpcClients[_network.CryptoCode];

        await daemon.SendCommandAsync<GenerateBlocksNoAddress, JsonRpcClient.NoRequestModel>(
            "generate",
            new GenerateBlocksNoAddress { AmountOfBlocks = mineBlockContext.BlockCount });

        return new ICheckoutCheatModeExtension.MineBlockResult();
    }
}
