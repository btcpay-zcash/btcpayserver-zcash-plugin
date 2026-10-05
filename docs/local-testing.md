# Local testing without Docker

Requirements: .NET SDK 10, Git, curl, and PostgreSQL 16. On macOS:

```sh
brew install postgresql@16
git submodule update --init --recursive
./scripts/run-local.sh
```

Open http://127.0.0.1:14142 and register the first administrator. Create a store,
open its Zcash wallet settings, create a mock account (any test label/key), select
that account, and enable ZEC. Use a fixed rate rule such as `ZEC_USD = 30` for
repeatable USD invoices without an external exchange. Create an invoice, open
checkout, and use the cheatmode payment/mining controls. The plugin's existing
poller checks for transactions and blocks every ten seconds.

The launcher builds and loads the plugin via `DEBUG_PLUGINS`, selects regtest and
ZEC only, and bypasses BTCPay's Bitcoin launch profile. No Bitcoin node,
NBXplorer, Zebra, lightwalletd, or real zkool instance is required.

The mock listens on loopback port 18080 and implements the subset of GraphQL
operations used for account creation, address allocation, wallet status, balances,
transactions and cheatmode payment. `/rpc` implements `generate`. It starts with a
funded cashcow account (ID 1), so BTCPay does not need to mine real coinbase funds.
Unsupported operations return errors rather than silently succeeding. WebSocket
subscriptions stay idle; payment detection uses the existing polling integration.

Mock addresses (`uregtest-mock-…`) and transactions are synthetic. This validates
application plumbing, not Zcash address validity, cryptography, fees, spending
rules, chain synchronization, or real zkool schema compatibility. Do not send real
funds. The mock wallet is in memory and resets each run; BTCPay's database persists.
After restarting the mock, create fresh mock accounts/wallet settings and invoices.

PostgreSQL uses `dev/data/postgres`, loopback port 15432, role `btcpay`, and database
`btcpay_zcash_local`. This isolated local cluster uses trust authentication and is
only for development. `Ctrl-C` stops BTCPay and the mock; stop PostgreSQL separately:

```sh
./scripts/local-postgres.sh stop
```

Set `PG_BIN` to a directory containing PostgreSQL tools if they are not installed
through Homebrew. Set `BTCPAY_POSTGRES` before launch to use another development
database (the launcher still starts the local cluster).

## Integration tests

```sh
dotnet test tests/BTCPayServer.Plugins.ZCash.IntegrationTests/BTCPayServer.Plugins.ZCash.IntegrationTests.csproj
```

These initial xUnit HTTP integration tests need neither Docker nor PostgreSQL nor
a separately running server. Each test starts an isolated mock on an ephemeral
loopback port and uses the real plugin GraphQL/RPC clients, wallet backend, summary
provider, polling and checkout cheatmode extension. They verify payment amount
conversion, unconfirmed transaction detection, mining, confirmation counts and
address filtering, plus explicit rejection of unsupported operations/invalid mining.
The separate Playwright test below covers browser UI, database address persistence
and full invoice settlement through BTCPay's listener.

The fixture/project structure references the
[Monero plugin integration-test branch](https://github.com/btcpay-monero/btcpayserver-monero-plugin/tree/integration-test).
That branch uses BTCPay's `UnitTestBase` and Playwright with Docker-backed services.
Here the first stage substitutes isolated mock HTTP services, allowing its testing
pattern and our existing cheatmode to be used on a macOS VM without Docker. The browser suite below launches the full server against these mocks. Real regtest
coverage can replace mock endpoints with zkool/Zebra instances.

## Playwright checkout coverage

```sh
./scripts/test-playwright.sh
```

The runner starts the local PostgreSQL cluster and installs Playwright's matching
Chromium browser (cached after the first run). The browser test starts a separate
BTCPay process and mock wallet on private loopback ports, creates a temporary
`zec_ui_*` database, and drops that database and stops both servers on completion.
It leaves existing local BTCPay stores and wallets untouched.

The flow registers an administrator, creates a store, creates its mock wallet
account, enables ZEC with a two-confirmation settlement threshold, creates a
0.1 ZEC invoice, and pays it through checkout cheatmode. It verifies the merchant
status is Processing before mining, remains Processing after one block, and
becomes Settled after the second block, then checks the customer checkout.
Using a ZEC-denominated invoice avoids external exchange-rate dependencies.

The browser test is opt-in: normal `dotnet test` runs the HTTP tests and reports
the browser test as skipped. Set `BTCPAY_RUN_PLAYWRIGHT=1` to enable it directly.
Set `BTCPAY_TEST_POSTGRES` to an administrative development PostgreSQL connection
string if using a different cluster; its user needs CREATE DATABASE privileges.
To use an already installed Chrome instead of downloading Chromium:

```sh
BTCPAY_PLAYWRIGHT_INSTALL=0 BTCPAY_PLAYWRIGHT_CHANNEL=chrome ./scripts/test-playwright.sh
```

Server logs and isolated server configuration are saved under
`TestResults/playwright/<run-id>/`. A failed browser flow also saves `failure.png`
and a Playwright `trace.zip`. These artifacts are ignored by Git. This test still
uses synthetic Zcash data; it does not replace real regtest chain coverage.

## Real local regtest (no Docker)

Install Rust/Cargo, Go, Python 3, Git and clang/libclang (`brew install go llvm`
plus Xcode command-line tools on macOS; `apt install clang libclang-dev` on Linux).
The wallet source defaults to the sibling `../zkool2` checkout; set `ZKOOL_SOURCE`
to use another checkout. From the sibling `zcash-regtest` repository:

```sh
./scripts/install-tools.sh
./scripts/regtest.sh start
# After “Regtest ready”, in another terminal, from the plugin repository:
./scripts/test-playwright.sh
```

The installer pins Zebra v6.2.1 with `internal-miner` and the lightwalletd revision
from the reference action, and builds zkool_graphql from local source with its
workspace patches. Binaries live in ignored `zcash-regtest/tools/bin` and can be reused;
the launcher also accepts binaries already on PATH. Go is needed for lightwalletd;
Cargo alone does not build the full stack. The wallet may download Sapling proving
parameters on first startup.

Each launch creates fresh state under `zcash-regtest/data/local/run-*`, binds Zebra RPC
18232, Zebra P2P 18233, lightwalletd 8137 and main GraphQL 18081 and cashcow GraphQL 18082 on loopback, and keeps
logs there. It refuses occupied ports and stops only its own processes on Ctrl-C.
It mines 300 blocks with NU6.3 activating at 250 (matching local zkool2).
BTCPay's existing `CreateTestWalletAsync`/`MakeCashCowFat` path creates cashcow
account 1 and the miner, mines mature coinbase, shields it and synchronizes the
cashcow. The browser fixture supplies the matching public test miner seed.
Checkout cheatmode handles invoice payments and further mining.

The Playwright test defaults to `BTCPAY_TEST_WALLET=auto`: it uses the reachable
regtest GraphQL server, otherwise starts its isolated mock. A reachable unhealthy
stack fails rather than silently falling back. Use `BTCPAY_TEST_WALLET=regtest`
to require the real stack, or `BTCPAY_TEST_WALLET=mock` to force the mock.
`BTCPAY_TEST_GRAPHQL`, `BTCPAY_TEST_CASHCOW_GRAPHQL` and `BTCPAY_TEST_ZEBRA_RPC` override the test endpoints.
The real browser flow imports a generated receiving viewing key and exercises
the same payment and two-confirmation settlement assertions. Run browser tests
serially against this shared chain; each run creates another receiving account.
The HTTP tests retain their isolated mock, including synthetic-key assertions.

Start BTCPay with `scripts/run-local.sh` once to fund the cashcow, then run
`zcash-regtest/scripts/prepare-test-wallet.py`. This creates a separate receiver account in
the cashcow instance and saves its seed and UFVK in ignored
`zcash-regtest/data/local/current.env` (mode 0600). The browser runner generates a fresh receiver per real run and loads this
file; each generated receiver also has a saved `receiver-<id>.env` file. In the local UI, paste `BTCPAY_TEST_VIEWING_KEY` into “Wallet Viewing Key”;
the main wallet imports a watch-only account. The spending seed stays in cashcow.
Use `zcash-regtest/scripts/regtest.sh start|stop|status` to manage the local stack.
Starting resumes its last state; `start --state-dir <existing-run-directory>` selects
another saved run after stopping.
