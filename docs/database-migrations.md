# Plugin database migrations

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
Review and commit the migration, its designer file, and the model snapshot together.
BTCPay applies plugin migrations at startup through `ZcashMigrationRunner`.

The local `dotnet-ef` tool and Debug-only EF Design package match BTCPay's EF Core
version. Update both when updating the BTCPay submodule's EF version. EF Design
is excluded from Release builds. The Debug-only CSharp.Workspaces and
Workspaces.MSBuild references align EF Design's Roslyn dependencies with
BTCPayServer.Rating. Separate Common, CSharp, and PostgreSQL provider references
are unnecessary here.

Debug builds also copy BTCPay's assemblies so EF can inspect the plugin's types.
Release retains the plugin packaging rules that exclude those host assemblies.
