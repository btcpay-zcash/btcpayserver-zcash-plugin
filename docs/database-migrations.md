# Plugin database migrations

## Runtime and upgrade behavior

The plugin owns the `BTCPayServer.Plugins.ZCash` PostgreSQL schema. Its EF migration
history table is also named `BTCPayServer.Plugins.ZCash`, but lives in BTCPay's
configured search path (normally `public`). **Keep both names and all shipped
migration IDs unchanged.** Moving the history table makes an existing database
look unmigrated. Do not use `EnsureCreated`, delete history rows, or stamp existing
tables as migrated without a separately reviewed recovery procedure.

`ZcashMigrationRunner` implements BTCPay's `IMigrationExecutor`. BTCPay invokes it
in `MigrationStartupTask` before starting the web host and wallet hosted services.
This uses the database startup contract supported by both the pinned BTCPay 2.3.7
and 2.4.7, keeping the existing `BaseDbContextFactory` configuration. Current BTCPay
documentation describes `BasePluginDbContext`/`AddPluginDbContext`; those APIs are
not present in the pinned 2.3.7 source. If the minimum host version is raised and
registration is modernized, preserve the history-table name/search path, startup
error handling and tests, and do not register two migration executors.

Each attempt opens one connection and takes a stable PostgreSQL advisory lock
(`802469322`) before checking history or migrating. The session lock serializes
the entire validation-and-migration batch, including history bootstrap and gaps
between EF migration transactions. Npgsql handles some history-creation collisions
itself; the extra lock also protects our preflight checks and the full batch.
EF still controls migration transactions and its own lock. Do not wrap
`MigrateAsync` in an explicit transaction. Per-command reconnect/retries are disabled
only for this runner so they cannot lose the advisory lock and continue on a new
session. Transient failures retry the entire attempt with a fresh context and lock
after five seconds, until canceled. Normal application contexts retain BTCPay's
default retry behavior. Migration commands use BTCPay's one-day migration timeout.
Lock cleanup uses the existing connection directly; it never reconnects merely to
unlock a dead session. A connection whose cleanup fails is discarded from the pool.
This requires a direct PostgreSQL connection or a proxy with session pooling;
transaction/statement pooling proxies cannot preserve session advisory locks.

Startup checks for model changes missing a migration and for unknown or incomplete
migration history. It will not attempt a downgrade or guess how to repair schema
drift. Permanent failures are logged and surfaced as BTCPay `ConfigException`;
cancellation also aborts startup through that path. BTCPay handles these errors
without marking the plugin disabled. The server does not start accepting payments
with an incomplete schema. Correct a permanent failure and restart; a temporary
database outage is retried automatically.

## Creating a migration

From the repository root, after changing the plugin's EF model:

```sh
dotnet tool restore
dotnet ef migrations add DescribeYourChange \
  --project Plugins/ZCash/BTCPayServer.Plugins.ZCash.csproj \
  --context ZcashPluginDbContext \
  --output-dir Data/Migrations
```

Use the default Debug configuration. The design-time factory selects PostgreSQL
and the plugin schema; generating a migration does not require a running database.
Review the generated operations and SQL, especially renames, constraints, type
conversions and backfills. New migrations should follow BTCPay's forward-only
convention: move `[DbContext]` and `[Migration]` attributes from the generated
designer to the migration class, remove the new designer, and omit `Down()`.
Keep the model snapshot. Do not retroactively reformat or remove existing shipped
migrations/designers/Down methods; they are part of the upgrade history.

Every new migration ID must sort after the latest shipped migration. Resolve
parallel-branch collisions before release. A rename must use a rename operation,
not a drop followed by an add. For new required columns, plan defaults/backfill
before adding `NOT NULL`; test existing rows, not just an empty database. Review
raw SQL and nontransactional operations separately because EF cannot guarantee
their data-preservation or restart behavior.

The local `dotnet-ef` tool and Debug-only EF Design package match BTCPay's EF Core
version. Update both when updating the BTCPay submodule's EF version. EF Design
is excluded from Release builds. The Debug-only CSharp.Workspaces and
Workspaces.MSBuild references align EF Design's Roslyn dependencies with
BTCPayServer.Rating. Separate Common, CSharp, and PostgreSQL provider references
are unnecessary here.

Debug builds also copy BTCPay's assemblies so EF can inspect the plugin's types.
Release retains the plugin packaging rules that exclude those host assemblies.

## Required checks

```sh
# Reject editing/removing shipped migrations or inserting older migration IDs.
python3 scripts/check-migration-history.py HEAD

# Run all migration checks, using a disposable PostgreSQL server.
# This account must be allowed to create/drop temporary test databases.
BTCPAY_TEST_POSTGRES='Host=127.0.0.1;Port=15432;Username=btcpay;Database=postgres' \
  dotnet test tests/BTCPayServer.Plugins.ZCash.IntegrationTests/BTCPayServer.Plugins.ZCash.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~MigrationTests'
```

The tests create randomly named `zec_migration_*` databases and drop them afterward;
they do not migrate the database named in the supplied admin connection. Without
`BTCPAY_TEST_POSTGRES`, PostgreSQL tests are reported as skipped, while model and
startup error tests still run. Do not treat a skipped run as upgrade validation.

The database workflow supplies PostgreSQL and runs against the pinned BTCPay source
and 2.4.7 in Release configuration. It checks:

- Runtime model versus the committed EF snapshot.
- Fresh installation, every migration prefix upgraded to current, and repeated startup.
- Preservation of receiver addresses, invoice links and recovery checkpoints.
- Concurrent migration startup, transient connection recovery and cancellation.
- Connection loss after migration DDL, rollback/retry, and pooled lock cleanup.
- Custom search paths without changing BTCPay's own history table.
- Unknown/gapped history and schema conflicts without destructive automatic repair.

For a model change, extend data-preservation assertions to cover the changed data.
Generate a reviewable SQL script with `dotnet ef migrations script <previous-id>
<new-id> --project Plugins/ZCash/BTCPayServer.Plugins.ZCash.csproj --context
ZcashPluginDbContext`. Never edit the snapshot by hand to silence a pending-model
failure. A passing snapshot check alone does not prove a migration preserves data.

## Deployment and recovery

Back up the database before upgrades and test restoring it. Stop older plugin/server
instances before applying schema changes: migration locks serialize migrations,
but cannot make an already-running older binary compatible with a new schema.
Rollback means restoring a compatible backup and plugin version, or deploying a
reviewed forward repair; do not run old binaries against newer database history.
Missing/corrupt history or manually-created tables require inspection and an
explicit repair plan. The plugin intentionally does not drop tables or invent
history to make startup succeed.

References: [BTCPay plugin databases](https://docs.btcpayserver.org/Developers/plugins/data-migrations/),
[EF migration deployment](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying),
[EF migration review and model checks](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing),
and [PostgreSQL advisory locks](https://www.postgresql.org/docs/16/explicit-locking.html#ADVISORY-LOCKS).
