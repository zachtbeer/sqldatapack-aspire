# SqlDataPack Aspire Hosting V1 design

Date: 2026-08-25
Status: approved, ready for implementation planning

## What this is

A development-only Aspire hosting integration that adds SQL Data Pack resource commands to
local Aspire SQL Server databases. The whole point is a short inner loop: run the AppHost,
open the database resource, pick `Import SQL Data Pack`, choose a pack, import it.

Nothing is configured in AppHost code beyond turning the commands on. No pack paths in
config, no automatic import at startup.

The commands are also callable by MCP clients. That is a first-class scenario, not an
accident, and it drove several decisions below.

## Package

- Repository `sqldatapack-aspire`, package `SqlDataPack.Aspire.Hosting`.
- Targets `net10.0` only.
- References `Aspire.Hosting` 13.5.3 and `SqlDataPack` 1.1.0.
- Versioned independently from the core `SqlDataPack` package.

## Verified API facts

Everything below was checked against the real assemblies, not assumed. Recording it here
because several of these overturn what the original brief expected.

**Aspire.Hosting 13.5.3**

- `IInteractionService`, `InteractionInput`, `InputType.File`, `FileFilter`, `MaxFileSize`
  and `InputsDialogInteractionOptions.ValidationCallback` are all stable API.
- Only `PromptProgressAsync`, `ProgressInteractionOptions` and `ProgressContext` carry
  `[Experimental("ASPIREINTERACTION001")]`.
- `ProgressContext` exposes a `CancellationToken` and nothing else. There is no way to
  change the message of a running progress dialog, so phase text means one
  `PromptProgressAsync` call per phase.
- `PromptProgressAsync` with a `Work` callback runs the work and closes the dialog when the
  callback finishes. It never waits on a human, so it is safe for agent-invoked commands.
- `InteractionFile.FilePath` is documented as "the full path to the uploaded file on disk".
  Aspire stages the upload for us.
- `InteractionFile` has **no public constructor** (only `internal .ctor(string, string, string)`)
  and `InteractionInput.Files` has a **non-public setter**. Only Aspire can populate a File
  input, and only from a real file picker. A File input is therefore structurally UI-only and
  cannot be supplied by an MCP or CLI client.
- File uploads are capped server-side at 100 MB by default. `MaxFileSize` can only lower that
  value, never raise it.
- `ResourceCommandVisibility` is a `[Flags]` enum: `None = 0`, `UI = 1`, `Api = 2`.
  `CommandOptions.Visibility` already defaults to `UI | Api`.
- Visibility "controls discovery and display, not authorization". Removing `Api` stops an
  agent discovering a command, not invoking it by name.
- `CommandOptions.Arguments` renders a dialog in the dashboard and maps named payloads for
  Dashboard and MCP clients by `Name`. `ValidateArguments` runs before the command callback,
  blocks execution on error, and the dashboard shows each error next to its input.
- `ExecuteCommandContext` carries `ServiceProvider`, `ResourceName`, `CancellationToken`,
  `Logger` and `Arguments`. It carries **no caller identity**, so code cannot tell an agent
  invocation from a human one.
- `SqlServerDatabaseResource` exposes `DatabaseName` and `Parent`, and implements
  `IResourceWithConnectionString`. The parent `SqlServerServerResource` resolves the
  server-level connection string.

**SqlDataPack 1.1.0**

- `SqlDataPackImporter.ImportAsync(string sqliteFilePath, string connectionString, ImportOptions?, CancellationToken)`
  takes a **file path**, not a stream, and opens it as SQLite read-only.
- `SqlDataPackReader.ReadManifestAsync(string sqliteFilePath, CancellationToken)` is the cheap
  pre-flight validator.
- `SqlDataPackManifest.ContainsDacpac` says whether the pack carries a schema.
- "Import schema" maps to `ImportOptions.SchemaDeploymentMode = SchemaDeploymentMode.DeployDacpac`,
  which throws when the pack has no dacpac.
- `ImportOptions.Logger` accepts an `ILogger`, which `ExecuteCommandContext.Logger` provides.
- Packs are plain SQLite files. The core repo writes `.sqlite` (389 references) and never
  writes `.sqldatapack` (0 references).

## Registration API

```csharp
var db = sql.AddDatabase("catalog")
            .WithSqlDataPack();
```

```csharp
public static IResourceBuilder<SqlServerDatabaseResource> WithSqlDataPack(
    this IResourceBuilder<SqlServerDatabaseResource> builder,
    Action<SqlDataPackCommandOptions>? configure = null);
```

```csharp
public sealed class SqlDataPackCommandOptions {
    public SqlDataPackSource PackSource { get; set; } = SqlDataPackSource.UploadOrPath;
    public ResourceCommandVisibility Visibility { get; set; }
        = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;
}

[Flags]
public enum SqlDataPackSource {
    Upload = 1,
    Path = 2,
    UploadOrPath = Upload | Path
}
```

Only `IResourceBuilder<SqlServerDatabaseResource>` is supported. No generic
`IResourceWithConnectionString` overload, no Azure SQL, no external servers.

`WithSqlDataPack` returns immediately without registering anything when
`builder.ApplicationBuilder.ExecutionContext.IsRunMode` is false.

An options object rather than boolean parameters, because these settings will keep
accumulating and a growing parameter list breaks callers every time.

### Registration-time validation

`PackSource = Upload` combined with a `Visibility` that includes `Api` throws at
registration. An agent would discover the import command and have no way to supply a pack,
because File inputs cannot be filled by a non-UI client. The message says to add `Path` or
drop `Api`.

This is deliberately blunt. `Reset Database` needs no pack and would work fine over `Api` in
that combination, but one `Visibility` setting covers both commands, and failing loudly beats
silently downgrading the import command to UI-only behind the developer's back. Splitting
visibility per command is post-V1 if anyone asks for it.

## Command availability

Both commands share an `UpdateState` callback reading `UpdateCommandStateContext.ResourceSnapshot`:

- `State` must report the resource running.
- `HealthStatus` must be `Healthy`, or `null`.

`null` counts as enabled because a resource with no health check registered reports no health
status, and treating that as unhealthy would disable the commands permanently. Anything else,
including a starting, stopped or unhealthy resource, returns `Disabled`, so the commands
cannot be invoked and then fail later.

## Command: Import SQL Data Pack

Display name `Import SQL Data Pack`.

Built declaratively with `CommandOptions.Arguments` rather than by calling
`PromptInputsAsync` by hand. The dashboard renders the dialog from the declared inputs,
validation errors appear next to the input that caused them, and the same argument contract
is what an MCP client fills in. All stable API.

### Arguments

| Name | Type | Default | Notes |
|---|---|---|---|
| `packFile` | File | none | Filter `.sqlite,.sqldatapack,.db`. Declared only when `PackSource` includes `Upload`. UI only, 100 MB cap |
| `packPath` | Text | none | Absolute path on the AppHost machine. Declared only when `PackSource` includes `Path`. The only route an agent can use, and the route for packs over 100 MB |
| `importSchema` | Boolean | true | Checked means schema and data. Unchecked means data only |
| `reset` | Boolean | false | Drops and recreates the database before importing |
| `confirmDestroy` | Boolean | false | Only consulted when `reset` is true |

The dialog message carries the V1 caveat as markdown:

> V1 does not merge or remove existing data. Without reset, schema + data import expects a
> blank database. Data-only import expects the required schema to already exist and its
> target tables to be empty.

Neither `packFile` nor `packPath` is marked `Required`, because exactly one of them is
wanted and the validation callback enforces that.

### Validation

Runs in `ValidateArguments`, before the command callback, so nothing destructive has
happened yet. Every failure attaches to the input that caused it.

| Condition | Error attaches to |
|---|---|
| Neither `packFile` nor `packPath` supplied | `packPath` when declared, else `packFile` |
| Both supplied | `packPath` |
| `packPath` does not exist on disk | `packPath` |
| Pack is not readable as a SQL Data Pack (`ReadManifestAsync` throws) | whichever input supplied it |
| `importSchema` true and `manifest.ContainsDacpac` false | `importSchema` |
| `reset` true and `importSchema` false | `reset` |
| `reset` true and `confirmDestroy` false | `confirmDestroy` |

The no-dacpac message names the fix: export with `SchemaCaptureMode.Dacpac`, or uncheck to
import data only.

Reset with `importSchema` false is refused because reset produces a completely empty
database, leaving a data-only import with no schema to load into.

Validating the pack here also satisfies the ordering the brief requires: the pack is proven
readable before any destructive reset can run.

### Execution

1. `Validating SQL Data Pack` phase. Re-read the manifest from the resolved path. Cheap, and
   it covers the file disappearing between validation and execution.
2. `Resetting database` phase, only when `reset` is true. Uses the shared resetter.
3. One import phase wrapping `SqlDataPackImporter.ImportAsync`. That single call deploys the
   dacpac and loads the rows, so it cannot be split into two dialogs. The phase message is
   `Applying schema and importing data` when `importSchema` is true and `Importing data` when
   it is false. The Console tab carries the real per-table detail.
4. Return `ExecuteCommandResult { Success = true, Message = "SQL Data Pack imported successfully." }`.

The pack path is `InteractionFile.FilePath` for an upload, or `packPath` verbatim. Aspire has
already staged the upload on disk, so there is no temp copy and no manual streaming.

`ImportOptions.Logger` is set to `ExecuteCommandContext.Logger`, which puts SqlDataPack's
table-level and row-level progress in the resource Console tab.

Cancellation flows from `ExecuteCommandContext.CancellationToken` and from the progress
dialog's cancel button into `ImportAsync`.

Failures return `Success = false` with a short `ErrorMessage` naming the useful reason. Full
exception detail goes to the command logger, not the notification.

## Command: Reset Database

Display name `Reset Database`. Drops and recreates the database empty. Does not import
anything afterwards.

One argument, `confirmDestroy` (Boolean, default false), validated as must-be-true with the
message naming the database:

> Reset database "{database}"? All data and schema in this local development database will
> be deleted.

Returns `Database reset successfully.`

## Destructive confirmation

Confirmation is an argument on the command contract, not an interactive prompt.

`PromptConfirmationAsync` and `CommandOptions.ConfirmationMessage` are both dashboard
prompts, and `ExecuteCommandContext` carries no caller identity, so the code cannot tell an
agent invocation from a human one. An agent-triggered reset would post a prompt to a
dashboard nobody may be watching and hang until cancelled.

A `confirmDestroy` argument behaves identically in both surfaces: a checkbox in the dashboard
dialog, an explicit field an agent must set. No hang, no caller sniffing, and no database is
destroyed by omission.

Neither `PromptConfirmationAsync` nor `ConfirmationMessage` appears in the implementation.

## Reset implementation

`SqlServerDatabaseResetter` is shared by both commands and holds no Aspire types.

It connects using the **parent server's** connection string, not the database's, so it is not
holding a handle on the database it is about to drop. Then, against the target named by
`SqlServerDatabaseResource.DatabaseName` (not the resource `Name`, which can differ):

1. `ALTER DATABASE [x] SET SINGLE_USER WITH ROLLBACK IMMEDIATE` to evict connections held by
   the application under test.
2. `DROP DATABASE [x]`.
3. `CREATE DATABASE [x]`.

Identifiers are quoted. This is enough for a normal Aspire development environment and is not
meant to be robust against a hostile or busy server.

## Progress and logging

One internal seam with a single method, roughly:

```csharp
internal interface IProgressScope {
    Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken ct);
}
```

`AspireProgressScope` implements it and is the only file that suppresses
`ASPIREINTERACTION001`. It checks `IInteractionService.IsAvailable` first and falls back to
running the work with logging only when interactions are unavailable. Tests substitute a
fake, so no test depends on the experimental API.

Phase text comes from one `PromptProgressAsync` call per phase, because `ProgressContext`
cannot update a running dialog. Phases may visibly flicker as one modal closes and the next
opens. That is accepted for V1.

Every phase is also written to the command logger, so the resource Console tab shows the
phases interleaved with SqlDataPack's own table progress.

## Repository structure

```text
src/
  SqlDataPack.Aspire.Hosting/
    SqlDataPackBuilderExtensions.cs    WithSqlDataPack(), run-mode gate, registration checks
    SqlDataPackCommandOptions.cs       options object and SqlDataPackSource
    SqlDataPackCommands.cs             inputs, validation, execute callbacks
    SqlDataPackImportOperation.cs      manifest read and import, no Aspire types
    SqlServerDatabaseResetter.cs       drop and recreate, no Aspire types
    AspireProgressScope.cs             experimental progress API, isolated

samples/
  SqlDataPack.Aspire.Sample/           AppHost exercising the acceptance criteria by hand

tests/
  SqlDataPack.Aspire.Hosting.Tests/               fast, no Docker
  SqlDataPack.Aspire.Hosting.IntegrationTests/    needs Docker
```

The brief suggested naming the import file `SqlDataPackImporter.cs`. That collides with
`SqlDataPack.SqlDataPackImporter`, the type this code calls, so ours is
`SqlDataPackImportOperation`.

The split between `SqlDataPackCommands` and the two operation classes is the only abstraction
worth having: Aspire command and dialog plumbing on one side, the actual import and reset
work on the other. No further layering.

## Repository conventions

Mirrors the core `sqldatapack` repository so moving between them feels the same:

- `Directory.Build.props` and `Directory.Packages.props` with central package management.
- Committed `packages.lock.json`, CI restoring with `--locked-mode`.
- `build/cleanup.sh` running `jb cleanupcode` as the formatter, with CI running the same
  script then `git diff --exit-code`. Not `dotnet format`.
- `SqlDataPack.Aspire.slnx`.
- A CI workflow running the unit suite, then the Docker-backed integration suite.
- NuGet packaging metadata describing the package as a local development Aspire integration.
- `README.md` and `AGENTS.md`.

## Testing

**`SqlDataPack.Aspire.Hosting.Tests`**, fast, no Docker:

- `WithSqlDataPack` registers both commands in run mode and registers nothing in publish mode.
- `PackSource` controls which of `packFile` and `packPath` is declared.
- `Visibility` reaches the command annotation, and the default includes `Api`.
- `PackSource = Upload` with `Api` visibility throws at registration.
- The full validation matrix from the table above, including a real fixture pack with a
  dacpac, one without, and a SQLite file that is not a pack at all.
- `UpdateState` returns Enabled only for a running, non-unhealthy snapshot.
- The progress seam is driven through a fake, asserting phase order and cancellation.

**`SqlDataPack.Aspire.Hosting.IntegrationTests`**, needs Docker:

- Reset drops and recreates, leaving an empty database.
- Reset evicts an open connection rather than failing.
- Import schema and data into a blank database.
- Import data only into an existing compatible empty schema.
- Reset then import from one pack.
- Import by `packPath` and import by staged upload path reach the same result.

The dashboard UI itself is not testable from either suite. The sample AppHost covers the
end-to-end acceptance criteria by hand.

## Safety boundary

Not supported in V1, and refused rather than approximated:

arbitrary connection strings, external SQL Server instances, Azure SQL, production
deployment, automatic startup imports, configured data-pack paths in AppHost code, database
merge behaviour, deleting rows while preserving schema, foreign-key dependency ordering,
export operations.

The package describes itself as a local development Aspire integration.

## Stated limitations

- The 100 MB cap applies to the `packFile` upload only. `packPath` has no cap. `MaxFileSize`
  cannot raise the server limit, which is why the path input exists rather than a larger cap.
- `packPath` reads any path the AppHost process can read. On a developer's own machine in run
  mode that is the intent. If the AppHost is reachable by a remote agent, it is a file-read
  primitive. Documented rather than restricted, because restricting it breaks the legitimate
  use.
- Visibility is discovery, not authorization. Turning `Api` off reduces what an agent finds,
  not what it can invoke by name.
- Reset does not re-run a `WithCreationScript` configured on the database. A developer who
  configured one gets a plain empty database back.
- Progress phases may flicker between modals.
- The `Validating SQL Data Pack` phase is partly cosmetic, since real validation already ran
  before execution.
- Three phases, not the four the brief listed. `Applying schema` and `Importing data` cannot
  be separated because one `ImportAsync` call does both. The Console tab shows the real
  boundary between them.

## Acceptance criteria

A developer can:

1. Add `.WithSqlDataPack()` to an Aspire SQL Server database.
2. Start the AppHost.
3. Open the database resource in the dashboard.
4. Choose `Import SQL Data Pack`.
5. Upload a pack, or give a path for one over 100 MB.
6. Import schema and data into a blank database.
7. Import data only into an existing compatible empty schema.
8. Select reset, confirm, and replace the database from a pack.
9. Run `Reset Database` on its own.
10. See phase progress, table-level detail in the Console tab, and a success or failure result.

An MCP client can:

11. Discover both commands on the database resource.
12. Import a pack by `packPath` without any human at the dashboard.
13. Reset, or import with reset, only by setting `confirmDestroy` explicitly.

Anything beyond these is post-V1.
