#!/usr/bin/env bash
set -euo pipefail
if [ "${BTCPAY_TEST_WALLET:-auto}" != mock ] && curl --max-time 3 -fsS -H 'Content-Type: application/json' -d '{"query":"query { currentHeight }"}' http://127.0.0.1:18081/graphql >/dev/null; then
  export BTCPAY_ZEC_WALLET_GRAPHQL_URI=http://127.0.0.1:18081/graphql
  export BTCPAY_ZEC_WALLET_CASHCOW_URI=http://127.0.0.1:18082/graphql
  export BTCPAY_ZEC_CASHCOW_DAEMON_URI=http://127.0.0.1:18232
  export BTCPAY_ZEC_CASHCOW_MINER_SEED="burger voice warrior danger satoshi you solid atom elite alcohol category layer able debate culture talk tissue language hip surge fiction paddle stove voyage"
elif [ "${BTCPAY_TEST_WALLET:-auto}" = regtest ]; then
  echo "Start zcash-regtest/scripts/run-local.py first" >&2
  exit 1
fi
source "$(dirname "$0")/local-env.sh"
cd "$REPO_ROOT"
# A second launch must not connect to an older mock or leave an older plugin serving the browser.
for LOCAL_PORT in 14142 18080; do
  if (echo >"/dev/tcp/127.0.0.1/$LOCAL_PORT") >/dev/null 2>&1; then
    echo "Port $LOCAL_PORT is already in use. Stop the previous local server (Ctrl-C in its terminal) before restarting." >&2
    exit 1
  fi
done
./scripts/local-postgres.sh start
dotnet build -m:1 Plugins/ZCash/BTCPayServer.Plugins.ZCash.csproj
MOCK_PID=""
SERVER_PID=""
cleanup() {
  if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi
  if [ -n "$SERVER_PID" ]; then kill "$SERVER_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM
if [ "$BTCPAY_ZEC_WALLET_GRAPHQL_URI" = http://127.0.0.1:18080/graphql ]; then
  dotnet build -m:1 dev/MockZkool/MockZkool.csproj
  dotnet run --no-build --no-launch-profile --project dev/MockZkool -- --urls http://127.0.0.1:18080 &
  MOCK_PID=$!
  for attempt in $(seq 1 30); do
    if ! kill -0 "$MOCK_PID" 2>/dev/null; then echo "Mock server failed to start" >&2; exit 1; fi
    if curl -fsS http://127.0.0.1:18080/health >/dev/null; then break; fi
    if [ "$attempt" = 30 ]; then echo "Mock server readiness timed out" >&2; exit 1; fi
    sleep 1
  done
fi
# Run from BTCPay's content root, and bypass its Docker-dependent launch profiles.
cd btcpayserver/BTCPayServer
dotnet run --no-build --no-launch-profile -- --disable-registration false &
SERVER_PID=$!
wait "$SERVER_PID"
