# ZCash support plugin

This plugin extends BTCPayServer to enable users to receive payments via Zcash.

> [!WARNING]
> This plugin shares a single Zcash wallet across all the stores in the BTCPayServer instance. Only use this plugin if you are not sharing your instance.

## Getting Started

### Installing the Plugin

[docs/installation.md](./docs/installation.md)

You can create a local test build of the plugin manually using these steps:

### Cloning the Project

```sh
git clone --recurse-submodules https://github.com/btcpay-zcash/btcpayserver-zcash-plugin
```

### Creating and Running a Local Build

> `cd` into the repository

```sh
cd btcpayserver
dotnet build .
cd ..
dotnet build .

cd btcpayserver/BTCPayServer
dotnet run
```

### Creating a Production Build Locally

```sh
cd btcpayserver
dotnet build .
cd ..
dotnet publish
cd bin/Release/net8.0/publish/
zip BTCPayServer.Plugins.ZCash.btcpay BTCPayServer.Plugins.ZCash.pdb BTCPayServer.Plugins.ZCash.dll BTCPayServer.Plugins.ZCash.deps.json
```

## Contribution

You will need to create this file:

**`btcpayserver/BTCPayServer/appsettings.dev.json`**

```json
{
  "DEBUG_PLUGINS": "/<absolute-path-to-repo>/btcpayserver-zcash-plugin/Plugins/ZCash/bin/Debug/net8.0/BTCPayServer.Plugins.ZCash.dll",
  "ZEC_DAEMON_URI": "http://127.0.0.1:8001",
  "ZEC_WALLET_DAEMON_URI": "http://127.0.0.1:8001",
  "ZEC_WALLET_DAEMON_WALLETDIR": "/<absolute-path-to-repo>/btcpayserver-zcash-plugin/dev/wallet_datadir",
  "CHAINS": "zec"
}

```

## Configuration

Configure this plugin using the following environment variables:

| Environment variable | Description |
| --- |-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
**BTCPAY_ZEC_DAEMON_URI** | **Required**. The URI of the deamon RPC interface |
**BTCPAY_ZEC_WALLET_DAEMON_URI** | **Required**.  The URI of the wallet RPC interface | http://127.0.0.1:18082 |
**BTCPAY_ZEC_WALLET_DAEMON_WALLETDIR** | **Required**. The directory of the wallet directory |

## For Maintainers

If you are a developer maintaining this plugin, in order to maintain this plugin, you need to clone this repository with `--recurse-submodules`:

```sh
git clone --recurse-submodules https://github.com/btcpay-zcash/btcpayserver-zcash-plugin
```

# Licence

[MIT](LICENSE.md)
