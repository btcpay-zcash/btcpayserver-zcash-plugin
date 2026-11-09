#!/usr/bin/env bash
# Source this file from the launch/test scripts.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ASPNETCORE_ENVIRONMENT=Development
export BTCPAY_NETWORK=regtest
export BTCPAY_CHAINS=zec
export BTCPAY_CHEATMODE=true
export BTCPAY_POSTGRES="${BTCPAY_POSTGRES:-Host=127.0.0.1;Port=15432;Username=btcpay;Database=btcpay_zcash_local}"
export BTCPAY_PLUGINDIR="$REPO_ROOT/dev/data/plugins"
export BTCPAY_DATADIR="$REPO_ROOT/dev/data/btcpay"
export BTCPAY_BIND=127.0.0.1
export BTCPAY_PORT=14142
export BTCPAY_ZEC_WALLET_BACKEND_TYPE=ZkoolGraphQL
export BTCPAY_ZEC_WALLET_GRAPHQL_URI="${BTCPAY_ZEC_WALLET_GRAPHQL_URI:-http://127.0.0.1:18080/graphql}"
export BTCPAY_ZEC_WALLET_CASHCOW_URI="${BTCPAY_ZEC_WALLET_CASHCOW_URI:-http://127.0.0.1:18080/graphql}"
export BTCPAY_ZEC_CASHCOW_DAEMON_URI="${BTCPAY_ZEC_CASHCOW_DAEMON_URI:-http://127.0.0.1:18080/rpc}"
export DEBUG_PLUGINS="$REPO_ROOT/Plugins/ZCash/bin/Debug/net10.0/BTCPayServer.Plugins.ZCash.dll"
export BTCPAY_DEBUG_PLUGINS="$DEBUG_PLUGINS"
