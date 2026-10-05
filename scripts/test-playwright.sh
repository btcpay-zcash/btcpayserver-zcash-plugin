#!/usr/bin/env bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"
# A new receiver per real run avoids crediting deposits from earlier test invoices.
if [ "${BTCPAY_TEST_WALLET:-auto}" != mock ] && curl --max-time 3 -fsS -H 'Content-Type: application/json' -d '{"query":"query { currentHeight }"}' http://127.0.0.1:18082/graphql >/dev/null; then
  "$REPO_ROOT/../zcash-regtest/scripts/prepare-test-wallet.py" --new
fi
if [ -f "$REPO_ROOT/../zcash-regtest/data/local/current.env" ]; then
  source "$REPO_ROOT/../zcash-regtest/data/local/current.env"
fi
./scripts/local-postgres.sh start
export BTCPAY_RUN_PLAYWRIGHT=1
# Download Playwright's matching Chromium once; later runs reuse its browser cache.
export BTCPAY_PLAYWRIGHT_INSTALL="${BTCPAY_PLAYWRIGHT_INSTALL:-1}"
dotnet test tests/BTCPayServer.Plugins.ZCash.IntegrationTests/BTCPayServer.Plugins.ZCash.IntegrationTests.csproj -m:1 --filter Category=Playwright "$@"
