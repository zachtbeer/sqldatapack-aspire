# SqlDataPack.Aspire.Hosting

Adds `Import SqlDataPack` and `Reset Database` commands to a local Aspire SQL Server database.

For SqlDataPack itself, what a pack file is and how to produce one, see the
[SqlDataPack documentation](https://zachtbeer.github.io/sqldatapack/).

This is a **local development integration**. It is not for production, and it deliberately refuses
arbitrary connection strings, external SQL Server instances, and Azure SQL.

## Install

```bash
dotnet add package SqlDataPack.Aspire.Hosting
```

Needs .NET 10 and Aspire 13.5.3 or later. It attaches to a database resource created by
`Aspire.Hosting.SqlServer`, so your AppHost needs that package too.

## Use

```csharp
var sql = builder.AddSqlServer("sql");

sql.AddDatabase("catalog")
   .WithSqlDataPack();
```

Run the AppHost, open the `catalog` resource in the dashboard, and pick `Import SqlDataPack`.

The commands only register in run mode. In publish mode `WithSqlDataPack` does nothing.

## Options

```csharp
.WithSqlDataPack(o => {
    o.PackSource = SqlDataPackSource.Path;        // skip the upload field
    o.Visibility = ResourceCommandVisibility.UI;  // hide from MCP and other API clients
});
```

`PackSource` defaults to `UploadOrPath`. `Visibility` defaults to dashboard plus API clients, so
an MCP client can import a pack by path without anyone at the dashboard.

`PackSource.Upload` combined with `Api` visibility throws, because an API client cannot supply an
uploaded file.

`importSchema` defaults to checked (schema and data) when the dashboard renders the command.
Whether an MCP client sees that default depends on how the client builds its payload; if it omits
`importSchema` entirely, set it explicitly rather than relying on the declared default. Getting
this wrong is fail-safe: `reset` and `confirmDestroy` still default to false either way, so the
worst case is a data-only import, not a dropped database.

## Limits

- The upload field is capped by Aspire's server-side upload limit, 100 MB by default. Use the path
  field for anything larger. That limit cannot be raised from here.
- The path field reads any file the AppHost process can read. That is the point on your own
  machine. Think twice if the AppHost is reachable by something else.
- Visibility controls discovery, not authorization.
- Reset does not re-run a `WithCreationScript` configured on the database. You get a plain empty
  database back.
- V1 does not merge or remove existing data. Import into a blank database, or use reset.
- If the reset batch fails partway through, the database can be left in `SINGLE_USER` mode.
  Re-running `Reset Database` recovers it.
