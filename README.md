# ZCash support plugin

This plugin extends BTCPay Server to enable users to receive payments via Zcash.

## Getting Started

### Installing the Plugin

*Not yet published.*

You can test this plugin with a manual build using these steps:

### Creating and Running a Local Build

> Git clone this repository and `cd` into it

```sh
cd btcpayserver
dotnet build .
cd ..
dotnet build .

cd btcpayserver
dotnet run
```

### Creating a Production Build Locally

```sh
dotnet publish
cd BTCPayServer/bin/Release/net8.0/publish/
zip BTCPayServer.Plugins.ZCash.btcpay BTCPayServer.Plugins.ZCash.pdb BTCPayServer.Plugins.ZCash.dll BTCPayServer.Plugins.ZCash.deps.json
```

## Configuration

Configure this plugin using the following environment variables:

| Environment variable | Description |
| --- |-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
**BTCPAY_ZEC_DAEMON_URI** | **Required**. The URI of the deamon RPC interface |
**BTCPAY_ZEC_WALLET_DAEMON_URI** | **Required**.  The URI of the wallet RPC interface | http://127.0.0.1:18082 |
**BTCPAY_ZEC_WALLET_DAEMON_WALLETDIR** | **Required**. The directory of the wallet directory |

# Licence

[MIT](LICENSE.md)
