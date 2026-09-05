# SqlDataPack.Aspire.Hosting

Adds `Import SqlDataPack` and `Reset Database` commands to a local Aspire SQL Server database, so you can load real data into your dev container from the dashboard instead of restoring a backup by hand.

The actual work is done by [SqlDataPack](https://zachtbeer.github.io/sqldatapack/). It exports a SQL Server database into a single SQLite file: pick the tables you want, filter the rows with a WHERE clause, bring the schema along as a dacpac if you need it. Instead of a whole database backup, or a seed script that drifts away from production, you get one file holding the slice of data you actually develop against. Its docs cover producing one, which you'll need before any of this is useful.

That's what this package is for: a tighter inner dev loop. Pull a fresh slice, hit Import in the dashboard, keep working. Hit Reset when you've made a mess of it.

Local development only. There's no way to point it at an arbitrary connection string, an external SQL Server, or Azure SQL. That's on purpose.

## Install

```bash
dotnet add package SqlDataPack.Aspire.Hosting
```

Needs .NET 10 and Aspire 13.5.3 or later. It attaches to a database resource created by `Aspire.Hosting.SqlServer`, so your AppHost needs that package too.

## Use

```csharp
var sql = builder.AddSqlServer("sql");

sql.AddDatabase("catalog").WithSqlDataPack();
```

Run the AppHost, open the `catalog` resource in the dashboard, and pick `Import SqlDataPack`.

The commands only show up in run mode. In publish mode `WithSqlDataPack` does nothing, so you can leave the call in and it won't follow you into a deployment.

## Options

```csharp
.WithSqlDataPack(o => {
    o.PackSource = SqlDataPackSource.Path;        // skip the upload field
    o.Visibility = ResourceCommandVisibility.UI;  // hide from MCP and other API clients
    o.AllowIncompatiblePlatform = false;          // refuse a pack from a different SQL Server
});
```

`PackSource` defaults to `UploadOrPath`. `Visibility` defaults to dashboard plus API clients, so an MCP client can import a pack by path with nobody sitting at the dashboard. Hiding a command from a client only hides it, though. It won't stop something that already knows the command name.

Combining `PackSource.Upload` with `Api` visibility throws at startup. An API client has no way to hand us an uploaded file, so there would be nothing for it to call.

### Changing the data on the way in

Two optional hooks rewrite data as part of the import. `BeforeImport` runs against the pack file, `AfterImport` runs against the target database once the import has succeeded.

```csharp
.WithSqlDataPack(o => {
    // SQLite, against the pack file. Runs before the database is touched at all.
    o.BeforeImport = async (ctx, ct) => {
        var customer = ctx.SqliteTableFor("dbo", "Customer");

        await using var cmd = ctx.Connection.CreateCommand();
        cmd.CommandText = $"UPDATE \"{customer}\" SET Email = 'dev@example.test', Phone = NULL;";
        await cmd.ExecuteNonQueryAsync(ct);
    };

    // SQL Server, against the target database. Only runs if the import succeeded.
    o.AfterImport = async (ctx, ct) => {
        ctx.Logger.LogInformation("Imported {Rows:N0} rows into {Db}.", ctx.Result.RowCount, ctx.DatabaseName);

        await using var cmd = ctx.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO dbo.FeatureFlag(Name, Enabled) VALUES ('NewCheckout', 1);";
        await cmd.ExecuteNonQueryAsync(ct);
    };
});
```

A pack does not name its tables after the SQL Server ones, so `ctx.SqliteTableFor("dbo", "Customer")` gives you the name to put in the SQL. `ctx.Manifest` has the rest of it: tables, columns, what the export left out, warnings.

Both connections are opened and closed for you, and each hook you register shows up as its own step in the dashboard's progress. `BeforeImport` runs ahead of the reset, so a hook that throws leaves the target database exactly as you left it.

If a `BeforeImport` hook deletes or inserts rows, the pack stops matching the row counts recorded at export. The import still goes ahead and logs a per-table drift warning.

### Packs from a different SQL Server

You usually export from one server and import into another. Azure SQL out, local SQL Server 2022 container in. DacFx normally refuses to do that. You may see messages like this:

```
A project which specifies SQL Server 2025 or Azure SQL Database Managed Instance as the target platform cannot be published to SQL Server 2022.
```

In SqlDataPack.Aspire.Hosting, we've defaulted `AllowIncompatiblePlatform` to true. If your source database is newer than your target _AND_ actually uses the features that come with those newer SQL Server versions, imports will fail in weird ways. You can set `AllowIncompatiblePlatform` = false to help prevent that (see above for an example).

## Other things you might need to be aware of

- The upload field is capped by Aspire's server-side upload limit, 100 MB by default. Use the path field for anything larger. We can't raise that limit from here.
- The path field reads any file the AppHost process can read. That's the point on your own machine. Think twice if the AppHost is reachable by anything else.
- `BeforeImport` edits the pack file in place. If you used the path field, that's your own `dev-slice.sqlite` being rewritten. Write hooks that survive a second run: `SET Email = 'dev@example.test'` is fine, `SET Email = Email || '.test'` grows a longer suffix every import.
- Reset does not re-run a `WithCreationScript` configured on the database. You get a plain empty database back.
- V1 does not merge or remove existing data. Import into a blank database, or use reset.
- If the reset batch fails partway through, the database can be left in `SINGLE_USER` mode. Re-running `Reset Database` recovers it.
