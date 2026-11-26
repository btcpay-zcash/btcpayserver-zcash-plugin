# Zcash support plugin

This plugin extends BTCPayServer to enable users to receive payments via Zcash.

Each hosted store receives through its own watch-only zkool account. A user with
permission to modify the store can import its unified full viewing key under
**Wallets → ZEC**. Use a distinct Zcash account for each store. Spending keys stay
in the merchant’s wallet. The legacy walletd backend is disabled.

## Getting Started

### Installing the Plugin

[docs/installation.md](./docs/installation.md)

## Local development and integration tests

For Docker-free local testing with mock zkool/mining endpoints and the existing
checkout cheatmode, see [docs/local-testing.md](docs/local-testing.md).

```sh
./scripts/run-local.sh
./scripts/test-playwright.sh
dotnet test tests/BTCPayServer.Plugins.ZCash.IntegrationTests/BTCPayServer.Plugins.ZCash.IntegrationTests.csproj
```

## Full Node

Use the [Docker wallet fragment and local lightwalletd options](docs/installation.md#optional-local-zebra-and-lightwalletd)
to connect zkool to your own full node.

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
publish_dir="$(mktemp -d)"
dotnet publish Plugins/ZCash/BTCPayServer.Plugins.ZCash.csproj -c Release -o "$publish_dir"
dotnet run --project btcpayserver/BTCPayServer.PluginPacker -- \
  "$publish_dir" BTCPayServer.Plugins.ZCash "$PWD/Plugins/packed"
```

To exercise the archive through the Release host’s plugin installer, set
`BTCPAY_TEST_PLUGIN_PACKAGE` to its absolute path and run
`./scripts/test-playwright.sh -c Release`.

## Contribution

You will need to create this file:

**`btcpayserver/BTCPayServer/appsettings.dev.json`**

```json
{
  "DEBUG_PLUGINS": "/<absolute-path-to-repo>/btcpayserver-zcash-plugin/Plugins/ZCash/bin/Debug/net10.0/BTCPayServer.Plugins.ZCash.dll",
  "ZEC_WALLET_BACKEND_TYPE": "ZkoolGraphQL",
  "ZEC_WALLET_GRAPHQL_URI": "http://127.0.0.1:18081/graphql",
  "CHAINS": "zec"
}

```

## Configuration

Set `BTCPAY_ZEC_WALLET_GRAPHQL_URI` to the host’s private zkool GraphQL endpoint.
The plugin uses stock zkool GraphQL (`accounts`, `createAccount`, and
`unifiedFullViewingKey`; tested with 6.32.0). Give this BTCPay deployment a
dedicated receiving wallet database and restrict its endpoint to trusted host
services. For JWT-authenticated zkool, supply its admin bearer token via
`BTCPAY_ZEC_WALLET_GRAPHQL_TOKEN`. Use a protected connection and rotate the
token before it expires. Only BTCPay should create or edit accounts in it. The host can view
incoming payments for its stores. Keep spending wallets on a separate endpoint.

## Upgrading to 2.0

Back up the BTCPay PostgreSQL database and the matching wallet data together.
Test the upgrade on a restored copy before switching a host that receives ZEC.

- **Existing zkool stores.** Keep the same wallet database. The plugin verifies
  the stored wallet ID, store label, and original viewing key or fingerprint.
  Existing verified imports retain their receiver addresses. A legacy single-address
  account without an exportable full viewing key stays in the wallet for historical
  payments. It cannot be bound to a store and does not block other stores. Stock zkool can
  export a viewing key from a spending account too. An old assignment with
  only a numeric ID and label cannot establish watch-only provenance. Those
  settings stay intact and ZEC checkout stays unavailable for that store.
  For new invoices, use a new store with its own receiving key or cut over the
  receiving database as described below. Preserve the previous wallet for
  payments to existing addresses.
- **Legacy walletd stores.** Finish or expire open ZEC invoices before switching.
  Configure the new host wallet with `BTCPAY_ZEC_WALLET_BACKEND_TYPE=ZkoolGraphQL`
  and `BTCPAY_ZEC_WALLET_GRAPHQL_URI`. Merchants start with a fresh receiving
  account. Issue a new invoice for any unpaid order that should be paid to the
  new wallet. Payments sent to an old address still reach the previous wallet
  and require separate reconciliation.
  Keep the old wallet and its backup to check for late payments. To reuse the
  same BTCPay store, remove its old ZEC payment-method configuration using
  Greenfield `DELETE /api/v1/stores/{storeId}/payment-methods/ZEC-CHAIN` after
  cutover, then import the new key under **Wallets → ZEC**. This clears the
  store setting and preserves invoice history.
- **Plugin database.** Startup applies all outstanding migrations in order.
  Intermediate plugin installs are unnecessary for this schema. Roll back by
  restoring the matching database, wallet, and plugin together.

See [database migration details](docs/database-migrations.md).

## For Maintainers

If you are a developer maintaining this plugin, in order to maintain this plugin, you need to clone this repository with `--recurse-submodules`:

```sh
git clone --recurse-submodules https://github.com/btcpay-zcash/btcpayserver-zcash-plugin
```

# Licence

[MIT](LICENSE.md)
