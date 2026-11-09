#!/usr/bin/env bash
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PG_BIN="${PG_BIN:-$(brew --prefix postgresql@16)/bin}"
PG_DATA="$REPO_ROOT/dev/data/postgres"
mkdir -p "$REPO_ROOT/dev/data"
case "${1:-start}" in
  start)
    if [ ! -f "$PG_DATA/PG_VERSION" ]; then
      "$PG_BIN/initdb" -D "$PG_DATA" -U btcpay -A trust --locale=C -E UTF8
    fi
    if ! "$PG_BIN/pg_ctl" -D "$PG_DATA" status >/dev/null 2>&1; then
      "$PG_BIN/pg_ctl" -D "$PG_DATA" -l "$REPO_ROOT/dev/data/postgres.log" -o "-h 127.0.0.1 -p 15432 -k $REPO_ROOT/dev/data" -w start
    fi
    DATABASE_EXISTS="$("$PG_BIN/psql" -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 15432 -U btcpay -d postgres -tAc "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname='btcpay_zcash_local')")"
    if [ "$DATABASE_EXISTS" = "f" ]; then
      "$PG_BIN/createdb" -h 127.0.0.1 -p 15432 -U btcpay btcpay_zcash_local
    fi
    ;;
  stop) "$PG_BIN/pg_ctl" -D "$PG_DATA" -m fast -w stop ;;
  *) echo "Usage: $0 [start|stop]" >&2; exit 1 ;;
esac
