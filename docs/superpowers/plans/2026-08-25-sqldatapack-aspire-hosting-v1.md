# SqlDataPack Aspire Hosting V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `SqlDataPack.Aspire.Hosting`, a development-only Aspire integration that adds `Import SQL Data Pack` and `Reset Database` commands to a local Aspire SQL Server database resource, callable from the dashboard and from MCP clients.

**Architecture:** One extension method registers two declarative Aspire resource commands. Aspire renders the dialog from `CommandOptions.Arguments` and runs `ValidateArguments` before the command body, so all validation happens before anything destructive. The Aspire plumbing lives in `SqlDataPackCommands`; the real work lives in two classes that hold no Aspire types. The experimental progress API is confined to one file behind an interface.

**Tech Stack:** .NET 10, Aspire.Hosting 13.5.3, SqlDataPack 1.1.0, xunit 2.9.3, Shouldly 4.3.0, Testcontainers.MsSql 4.14.0.

**Spec:** `docs/superpowers/specs/2026-08-25-sqldatapack-aspire-hosting-design.md`

## Global Constraints

- Package ID `SqlDataPack.Aspire.Hosting`, namespace `SqlDataPack.Aspire.Hosting`.
- Target `net10.0` only. No multi-targeting.
- `Aspire.Hosting` version `13.5.3`. `SqlDataPack` version `1.1.0`.
- Central package management. Every version lives in `Directory.Packages.props`. No `Version=` on a `PackageReference`.
- `RestorePackagesWithLockFile` true. Commit every `packages.lock.json`. CI restores with `--locked-mode`.
- Only `IResourceBuilder<SqlServerDatabaseResource>` is supported. No generic `IResourceWithConnectionString` overload, no Azure SQL, no external servers.
- Commands register only when `builder.ApplicationBuilder.ExecutionContext.IsRunMode` is true.
- File filter is exactly `.sqlite,.sqldatapack,.db`.
- Command names: `sqldatapack-import` and `sqldatapack-reset`. Display names `Import SQL Data Pack` and `Reset Database`.
- Success messages, verbatim: `SQL Data Pack imported successfully.` and `Database reset successfully.`
- `ASPIREINTERACTION001` may be suppressed in exactly one file, `AspireProgressScope.cs`. Nowhere else.
- Never call `GetBoolean` or `GetString` on an `InteractionInputCollection` directly. Both throw `InvalidOperationException` when the input has no value, which is the normal state of an unchecked checkbox. Use the helpers from Task 3.
- No em dashes or en dashes in any source comment, doc, or user-facing string.

---

## File Structure

```text
global.json
Directory.Build.props
Directory.Packages.props
SqlDataPack.Aspire.slnx
.gitignore
.editorconfig
build/cleanup.sh
.github/workflows/ci.yml
README.md
AGENTS.md

src/SqlDataPack.Aspire.Hosting/
  SqlDataPack.Aspire.Hosting.csproj
  SqlDataPackCommandOptions.cs       public options object and SqlDataPackSource enum
  PackInspection.cs                  IPackInspector, PackInfo, PackUnreadableException, SqlDataPackInspector
  CommandArguments.cs                safe readers over InteractionInputCollection, input names
  ImportValidation.cs                the pure validation matrix
  ResourceAvailability.cs            pure resource-usable decision
  ProgressScope.cs                   IProgressScope and a logging-only fallback
  AspireProgressScope.cs             the only file suppressing ASPIREINTERACTION001
  SqlServerDatabaseResetter.cs       drop and recreate, no Aspire types
  SqlDataPackImportOperation.cs      manifest read and import, no Aspire types
  SqlDataPackCommands.cs             argument declarations, validation adapter, execute callbacks
  SqlDataPackBuilderExtensions.cs    WithSqlDataPack(), run-mode gate, registration checks

samples/SqlDataPack.Aspire.Sample/
  SqlDataPack.Aspire.Sample.csproj
  AppHost.cs

tests/SqlDataPack.Aspire.Hosting.Tests/            fast, no Docker
tests/SqlDataPack.Aspire.Hosting.IntegrationTests/ needs Docker
```

Why the spec's five source files became eleven: the spec's `SqlDataPackCommands.cs` would have held the argument declarations, the validation matrix, the safe argument readers, and the availability rule all at once, and two of those cannot be unit tested through Aspire types (see Task 5). Splitting the pure decisions out is what makes the validation matrix testable without Docker or a dashboard. The two operation classes and the progress seam are unchanged from the spec.

---

### Task 1: Repository scaffolding and the options type

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `SqlDataPack.Aspire.slnx`
- Create: `src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj`
- Create: `src/SqlDataPack.Aspire.Hosting/SqlDataPackCommandOptions.cs`
- Create: `tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPackCommandOptionsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `SqlDataPackCommandOptions` with `PackSource` (`SqlDataPackSource`) and `Visibility` (`ResourceCommandVisibility`); `[Flags] enum SqlDataPackSource { Upload = 1, Path = 2, UploadOrPath = 3 }`.

- [ ] **Step 1: Create `global.json`**

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestMinor"
  }
}
```

- [ ] **Step 2: Create `Directory.Build.props`**

```xml
<Project>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <!--
    No project holds a <Version>. An ordinary build produces 1.0.0. Release versions are passed
    into build and pack by the release workflow, matching how the core sqldatapack repo works.
  -->

</Project>
```

- [ ] **Step 3: Create `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup Label="Library">
    <PackageVersion Include="Aspire.Hosting" Version="13.5.3" />
    <PackageVersion Include="SqlDataPack" Version="1.1.0" />
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="7.0.2" />
  </ItemGroup>
  <ItemGroup Label="Sample">
    <PackageVersion Include="Aspire.Hosting.AppHost" Version="13.5.3" />
    <PackageVersion Include="Aspire.Hosting.SqlServer" Version="13.5.3" />
  </ItemGroup>
  <ItemGroup Label="Testing">
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.14.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create `.gitignore`**

```gitignore
bin/
obj/
.vs/
*.user
artifacts/
TestResults/

# Scratch workspace for plan execution, not part of the project
.superpowers/
```

- [ ] **Step 5: Create the library project**

`src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>SqlDataPack.Aspire.Hosting</PackageId>
    <Description>Local development Aspire integration that adds SQL Data Pack import and database reset commands to an Aspire SQL Server database resource. Not for production use.</Description>
    <PackageTags>aspire;sqlserver;sqldatapack;development</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting" />
    <PackageReference Include="SqlDataPack" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="SqlDataPack.Aspire.Hosting.Tests" />
    <InternalsVisibleTo Include="SqlDataPack.Aspire.Hosting.IntegrationTests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Create the unit test project**

`tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 7: Create the solution**

Run:

```bash
dotnet new sln -n SqlDataPack.Aspire --format slnx
dotnet sln SqlDataPack.Aspire.slnx add src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj
dotnet sln SqlDataPack.Aspire.slnx add tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj
```

If `--format slnx` is rejected by this SDK, create a plain `.sln` instead and rename the references in later tasks accordingly. Do not block on it.

- [ ] **Step 8: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPackCommandOptionsTests.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class SqlDataPackCommandOptionsTests {
    [Fact]
    public void Defaults_AcceptBothPackSourcesAndBothSurfaces() {
        var options = new SqlDataPackCommandOptions();

        options.PackSource.ShouldBe(SqlDataPackSource.UploadOrPath);
        options.Visibility.ShouldBe(ResourceCommandVisibility.UI | ResourceCommandVisibility.Api);
    }

    [Fact]
    public void UploadOrPath_IsUploadAndPathCombined() {
        SqlDataPackSource.UploadOrPath.ShouldBe(SqlDataPackSource.Upload | SqlDataPackSource.Path);
    }

    [Theory]
    [InlineData(SqlDataPackSource.Upload, true, false)]
    [InlineData(SqlDataPackSource.Path, false, true)]
    [InlineData(SqlDataPackSource.UploadOrPath, true, true)]
    public void PackSource_FlagsReadIndependently(SqlDataPackSource source, bool upload, bool path) {
        source.HasFlag(SqlDataPackSource.Upload).ShouldBe(upload);
        source.HasFlag(SqlDataPackSource.Path).ShouldBe(path);
    }
}
```

- [ ] **Step 9: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`
Expected: FAIL to compile, `SqlDataPackCommandOptions` and `SqlDataPackSource` do not exist.

- [ ] **Step 10: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/SqlDataPackCommandOptions.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Where the import command is allowed to get a SQL Data Pack from.
/// </summary>
[Flags]
public enum SqlDataPackSource {
    /// <summary>A file uploaded through the dashboard. Dashboard only, and capped at the server upload limit.</summary>
    Upload = 1,

    /// <summary>A path on the machine running the AppHost. The only source a non-UI client can supply.</summary>
    Path = 2,

    /// <summary>Both. This is the default.</summary>
    UploadOrPath = Upload | Path
}

/// <summary>
/// Settings for the SQL Data Pack resource commands.
/// </summary>
public sealed class SqlDataPackCommandOptions {
    /// <summary>Where a pack may come from. Defaults to <see cref="SqlDataPackSource.UploadOrPath"/>.</summary>
    public SqlDataPackSource PackSource { get; set; } = SqlDataPackSource.UploadOrPath;

    /// <summary>
    /// Which clients discover the commands. Defaults to both the dashboard and API clients such as MCP.
    /// Visibility controls discovery and display, not authorization.
    /// </summary>
    public ResourceCommandVisibility Visibility { get; set; }
        = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;
}
```

- [ ] **Step 11: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`
Expected: PASS, 5 tests.

- [ ] **Step 12: Commit**

```bash
dotnet restore SqlDataPack.Aspire.slnx
git add -A
git commit -m "Add repository scaffolding and SQL Data Pack command options"
```

---

### Task 2: Pack inspection seam

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/PackInspection.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/PackInspectionTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `internal sealed record PackInfo(bool ContainsDacpac)`
  - `internal interface IPackInspector { Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken); }`
  - `internal sealed class PackUnreadableException : Exception` with `PackUnreadableException(string message, Exception? innerException = null)`
  - `internal sealed class SqlDataPackInspector : IPackInspector`

This seam exists so the whole validation matrix in Task 4 is testable without a real pack file or a SQL Server. `SqlDataPackReader` is a sealed class with no interface, so it cannot be faked directly.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/PackInspectionTests.cs`:

```csharp
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class PackInspectionTests {
    [Fact]
    public async Task InspectAsync_MissingFile_ThrowsPackUnreadable() {
        var inspector = new SqlDataPackInspector();
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sqlite");

        var exception = await Should.ThrowAsync<PackUnreadableException>(
            () => inspector.InspectAsync(path, CancellationToken.None));

        exception.Message.ShouldContain("does not exist");
    }

    [Fact]
    public async Task InspectAsync_FileThatIsNotAPack_ThrowsPackUnreadable() {
        var inspector = new SqlDataPackInspector();
        var path = Path.Combine(Path.GetTempPath(), $"junk-{Guid.NewGuid():N}.sqlite");
        await File.WriteAllTextAsync(path, "this is not a SQLite database at all");

        try {
            var exception = await Should.ThrowAsync<PackUnreadableException>(
                () => inspector.InspectAsync(path, CancellationToken.None));

            exception.Message.ShouldContain("not a readable SQL Data Pack");
        }
        finally {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter PackInspectionTests`
Expected: FAIL to compile, `SqlDataPackInspector` and `PackUnreadableException` do not exist.

- [ ] **Step 3: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/PackInspection.cs`:

```csharp
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>What the commands need to know about a pack before touching a database.</summary>
internal sealed record PackInfo(bool ContainsDacpac);

/// <summary>Thrown when a path is not a SQL Data Pack we can read.</summary>
internal sealed class PackUnreadableException : Exception {
    public PackUnreadableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>Reads just enough of a pack to validate it. Exists so tests can fake it.</summary>
internal interface IPackInspector {
    Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken);
}

/// <summary>The real inspector, backed by SqlDataPackReader.</summary>
internal sealed class SqlDataPackInspector : IPackInspector {
    public async Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken) {
        if (!File.Exists(path)) {
            throw new PackUnreadableException($"The file '{path}' does not exist.");
        }

        try {
            var manifest = await new SqlDataPackReader().ReadManifestAsync(path, cancellationToken);
            return new PackInfo(manifest.ContainsDacpac);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new PackUnreadableException($"The file '{path}' is not a readable SQL Data Pack.", ex);
        }
    }
}
```

Namespaces in SqlDataPack 1.1.0, verified against the assembly. Get these wrong and nothing compiles:

- `SqlDataPack` (root): `SqlDataPackExporter`, `SqlDataPackImporter`.
- `SqlDataPack.Models`: `SqlDataPackReader`, `SqlDataPackManifest`, `SqlDataPackResult`, `ExportOptions`, `ImportOptions`, `SchemaCaptureMode`, `SchemaDeploymentMode`.

The root-namespace types resolve unqualified from inside `SqlDataPack.Aspire.Hosting` because `SqlDataPack` is an enclosing namespace. Everything in `SqlDataPack.Models` needs `using SqlDataPack.Models;`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter PackInspectionTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add pack inspection seam over SqlDataPackReader"
```

---

### Task 3: Safe argument readers

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/CommandArguments.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/CommandArgumentsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal static class CommandArguments` with constants `PackFile`, `PackPath`, `ImportSchema`, `Reset`, `ConfirmDestroy`, and methods `bool ReadBoolean(InteractionInputCollection, string)`, `string? ReadText(InteractionInputCollection, string)`, `string? ReadUploadedFilePath(InteractionInputCollection, string)`.

This exists because `InteractionInputCollection.GetBoolean` and `GetString` throw `InvalidOperationException` when an input carries no value, which is the normal state of an unchecked checkbox and of an untouched optional field. Verified against Aspire 13.5.3. Every read in this codebase goes through here.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/CommandArgumentsTests.cs`:

```csharp
using Aspire.Hosting;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class CommandArgumentsTests {
    private static InteractionInputCollection Collection(params InteractionInput[] inputs) => new(inputs);

    private static InteractionInput Boolean(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Boolean, Value = value };

    private static InteractionInput Text(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Text, Value = value };

    [Fact]
    public void ReadBoolean_UnsetInput_ReturnsFalseInsteadOfThrowing() {
        var args = Collection(Boolean(CommandArguments.Reset, null));

        CommandArguments.ReadBoolean(args, CommandArguments.Reset).ShouldBeFalse();
    }

    [Fact]
    public void ReadBoolean_MissingInput_ReturnsFalse() {
        var args = Collection(Boolean(CommandArguments.Reset, "true"));

        CommandArguments.ReadBoolean(args, CommandArguments.ConfirmDestroy).ShouldBeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void ReadBoolean_ParsesValue(string value, bool expected) {
        var args = Collection(Boolean(CommandArguments.ImportSchema, value));

        CommandArguments.ReadBoolean(args, CommandArguments.ImportSchema).ShouldBe(expected);
    }

    [Fact]
    public void ReadText_MissingInput_ReturnsNull() {
        var args = Collection(Text(CommandArguments.PackPath, "c:/packs/a.sqlite"));

        CommandArguments.ReadText(args, "nope").ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadText_BlankValue_ReturnsNull(string? value) {
        var args = Collection(Text(CommandArguments.PackPath, value));

        CommandArguments.ReadText(args, CommandArguments.PackPath).ShouldBeNull();
    }

    [Fact]
    public void ReadText_TrimsSurroundingWhitespace() {
        var args = Collection(Text(CommandArguments.PackPath, "  c:/packs/a.sqlite  "));

        CommandArguments.ReadText(args, CommandArguments.PackPath).ShouldBe("c:/packs/a.sqlite");
    }

    [Fact]
    public void ReadUploadedFilePath_NoFileSelected_ReturnsNull() {
        var args = Collection(new InteractionInput {
            Name = CommandArguments.PackFile, Label = "pack", InputType = InputType.File
        });

        CommandArguments.ReadUploadedFilePath(args, CommandArguments.PackFile).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter CommandArgumentsTests`
Expected: FAIL to compile, `CommandArguments` does not exist.

- [ ] **Step 3: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/CommandArguments.cs`:

```csharp
using Aspire.Hosting;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Argument names and safe readers. InteractionInputCollection.GetBoolean and GetString throw when
/// an input has no value, which is what an unchecked box and an untouched field both look like.
/// </summary>
internal static class CommandArguments {
    public const string PackFile = "packFile";
    public const string PackPath = "packPath";
    public const string ImportSchema = "importSchema";
    public const string Reset = "reset";
    public const string ConfirmDestroy = "confirmDestroy";

    public static bool ReadBoolean(InteractionInputCollection arguments, string name) =>
        arguments.TryGetByName(name, out var input)
        && bool.TryParse(input.Value, out var value)
        && value;

    public static string? ReadText(InteractionInputCollection arguments, string name) {
        if (!arguments.TryGetByName(name, out var input)) {
            return null;
        }

        var value = input.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string? ReadUploadedFilePath(InteractionInputCollection arguments, string name) {
        if (!arguments.TryGetByName(name, out var input)) {
            return null;
        }

        var file = input.Files is { Count: > 0 } files ? files[0] : null;
        return string.IsNullOrWhiteSpace(file?.FilePath) ? null : file.FilePath;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter CommandArgumentsTests`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add safe readers for Aspire command arguments"
```

---

### Task 4: The import validation matrix

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/ImportValidation.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/ImportValidationTests.cs`

**Interfaces:**
- Consumes: `IPackInspector`, `PackInfo`, `PackUnreadableException` (Task 2); `CommandArguments` (Task 3).
- Produces:
  - `internal sealed record ValidationFailure(string InputName, string Message)`
  - `internal sealed record ImportRequest(string PackPath, bool ImportSchema, bool Reset)`
  - `internal sealed record ImportValidationResult(ImportRequest? Request, IReadOnlyList<ValidationFailure> Failures)`
  - `internal static class ImportValidation` with
    `Task<ImportValidationResult> ValidateAsync(InteractionInputCollection arguments, SqlDataPackSource packSource, IPackInspector inspector, CancellationToken cancellationToken)`
    and `IReadOnlyList<ValidationFailure> ValidateReset(InteractionInputCollection arguments)`

This is the heart of the feature. Every rule from the spec's validation table lives here and nowhere else.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/ImportValidationTests.cs`:

```csharp
using Aspire.Hosting;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ImportValidationTests {
    private const string ExistingPack = "pack-that-exists.sqlite";

    private sealed class FakeInspector : IPackInspector {
        private readonly PackInfo? _info;
        private readonly Exception? _throws;

        private FakeInspector(PackInfo? info, Exception? throws) {
            _info = info;
            _throws = throws;
        }

        public static FakeInspector WithDacpac() => new(new PackInfo(ContainsDacpac: true), null);
        public static FakeInspector WithoutDacpac() => new(new PackInfo(ContainsDacpac: false), null);
        public static FakeInspector Unreadable() => new(null, new PackUnreadableException("not a readable SQL Data Pack"));

        public int Calls { get; private set; }

        public Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken) {
            Calls++;
            return _throws is not null ? Task.FromException<PackInfo>(_throws) : Task.FromResult(_info!);
        }
    }

    private static InteractionInput Boolean(string name, bool? value) => new() {
        Name = name, Label = name, InputType = InputType.Boolean,
        Value = value is null ? null : value.Value ? "true" : "false"
    };

    private static InteractionInput Text(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Text, Value = value };

    private static InteractionInputCollection Args(
        string? packPath = null, bool? importSchema = true, bool? reset = false, bool? confirmDestroy = false) =>
        new([
            Text(CommandArguments.PackPath, packPath),
            Boolean(CommandArguments.ImportSchema, importSchema),
            Boolean(CommandArguments.Reset, reset),
            Boolean(CommandArguments.ConfirmDestroy, confirmDestroy)
        ]);

    private static Task<ImportValidationResult> Validate(
        InteractionInputCollection args, IPackInspector inspector, SqlDataPackSource source = SqlDataPackSource.Path) =>
        ImportValidation.ValidateAsync(args, source, inspector, CancellationToken.None);

    [Fact]
    public async Task ValidPathPackWithDacpac_Succeeds() {
        var result = await Validate(Args(packPath: ExistingPack), FakeInspector.WithDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request.ShouldNotBeNull();
        result.Request!.PackPath.ShouldBe(ExistingPack);
        result.Request.ImportSchema.ShouldBeTrue();
        result.Request.Reset.ShouldBeFalse();
    }

    [Fact]
    public async Task NoPackSupplied_FailsOnPackPath() {
        var result = await Validate(Args(packPath: null), FakeInspector.WithDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackPath);
    }

    [Fact]
    public async Task NoPackSuppliedAndOnlyUploadDeclared_FailsOnPackFile() {
        var args = new InteractionInputCollection([
            new InteractionInput { Name = CommandArguments.PackFile, Label = "pack", InputType = InputType.File },
            Boolean(CommandArguments.ImportSchema, true),
            Boolean(CommandArguments.Reset, false),
            Boolean(CommandArguments.ConfirmDestroy, false)
        ]);

        var result = await Validate(args, FakeInspector.WithDacpac(), SqlDataPackSource.Upload);

        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackFile);
    }

    [Fact]
    public async Task UnreadablePack_FailsOnTheInputThatSuppliedIt() {
        var inspector = FakeInspector.Unreadable();

        var result = await Validate(Args(packPath: ExistingPack), inspector);

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackPath);
        result.Failures[0].Message.ShouldContain("not a readable SQL Data Pack");
    }

    [Fact]
    public async Task ImportSchemaWithoutDacpac_FailsOnImportSchema() {
        var result = await Validate(Args(packPath: ExistingPack, importSchema: true), FakeInspector.WithoutDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.ImportSchema);
        result.Failures[0].Message.ShouldContain("SchemaCaptureMode.Dacpac");
    }

    [Fact]
    public async Task DataOnlyImportFromPackWithoutDacpac_Succeeds() {
        var result = await Validate(Args(packPath: ExistingPack, importSchema: false), FakeInspector.WithoutDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request!.ImportSchema.ShouldBeFalse();
    }

    [Fact]
    public async Task ResetWithDataOnly_FailsOnReset() {
        var result = await Validate(
            Args(packPath: ExistingPack, importSchema: false, reset: true, confirmDestroy: true),
            FakeInspector.WithoutDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldContain(f => f.InputName == CommandArguments.Reset);
    }

    [Fact]
    public async Task ResetWithoutConfirmation_FailsOnConfirmDestroy() {
        var result = await Validate(
            Args(packPath: ExistingPack, reset: true, confirmDestroy: false),
            FakeInspector.WithDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldContain(f => f.InputName == CommandArguments.ConfirmDestroy);
    }

    [Fact]
    public async Task ResetWithConfirmation_Succeeds() {
        var result = await Validate(
            Args(packPath: ExistingPack, reset: true, confirmDestroy: true),
            FakeInspector.WithDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request!.Reset.ShouldBeTrue();
    }

    [Fact]
    public async Task UnreadablePack_DoesNotAlsoReportSchemaProblem() {
        var result = await Validate(Args(packPath: ExistingPack), FakeInspector.Unreadable());

        result.Failures.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MissingPack_NeverInspects() {
        var inspector = FakeInspector.WithDacpac();

        await Validate(Args(packPath: null), inspector);

        inspector.Calls.ShouldBe(0);
    }

    [Fact]
    public void ValidateReset_WithoutConfirmation_Fails() {
        var args = new InteractionInputCollection([Boolean(CommandArguments.ConfirmDestroy, false)]);

        var failures = ImportValidation.ValidateReset(args);

        failures.ShouldHaveSingleItem();
        failures[0].InputName.ShouldBe(CommandArguments.ConfirmDestroy);
    }

    [Fact]
    public void ValidateReset_WithConfirmation_Passes() {
        var args = new InteractionInputCollection([Boolean(CommandArguments.ConfirmDestroy, true)]);

        ImportValidation.ValidateReset(args).ShouldBeEmpty();
    }
}
```

Note: the "both supplied" rule from the spec cannot be exercised here, because a File input's `Files` collection has a non-public setter and cannot be populated by a test. It is implemented in Step 3 and covered by the integration suite in Task 11.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ImportValidationTests`
Expected: FAIL to compile, `ImportValidation` does not exist.

- [ ] **Step 3: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/ImportValidation.cs`:

```csharp
using Aspire.Hosting;

namespace SqlDataPack.Aspire.Hosting;

internal sealed record ValidationFailure(string InputName, string Message);

internal sealed record ImportRequest(string PackPath, bool ImportSchema, bool Reset);

internal sealed record ImportValidationResult(ImportRequest? Request, IReadOnlyList<ValidationFailure> Failures);

/// <summary>
/// Every rule from the design's validation table. Runs before the command body, so nothing
/// destructive has happened when a failure is returned.
/// </summary>
internal static class ImportValidation {
    public static async Task<ImportValidationResult> ValidateAsync(
        InteractionInputCollection arguments,
        SqlDataPackSource packSource,
        IPackInspector inspector,
        CancellationToken cancellationToken) {

        var failures = new List<ValidationFailure>();

        var uploadDeclared = packSource.HasFlag(SqlDataPackSource.Upload);
        var pathDeclared = packSource.HasFlag(SqlDataPackSource.Path);

        var uploadedPath = uploadDeclared ? CommandArguments.ReadUploadedFilePath(arguments, CommandArguments.PackFile) : null;
        var typedPath = pathDeclared ? CommandArguments.ReadText(arguments, CommandArguments.PackPath) : null;

        // Which input a "you must supply a pack" error belongs to.
        var missingPackInput = pathDeclared ? CommandArguments.PackPath : CommandArguments.PackFile;

        string? packPath = null;
        string packInput = missingPackInput;

        if (uploadedPath is not null && typedPath is not null) {
            failures.Add(new ValidationFailure(
                CommandArguments.PackPath,
                "Supply either an uploaded file or a path, not both."));
        }
        else if (uploadedPath is null && typedPath is null) {
            failures.Add(new ValidationFailure(
                missingPackInput,
                pathDeclared && uploadDeclared
                    ? "Upload a SQL Data Pack, or give the path to one on the AppHost machine."
                    : uploadDeclared
                        ? "Upload a SQL Data Pack."
                        : "Give the path to a SQL Data Pack on the AppHost machine."));
        }
        else if (uploadedPath is not null) {
            packPath = uploadedPath;
            packInput = CommandArguments.PackFile;
        }
        else {
            packPath = typedPath;
            packInput = CommandArguments.PackPath;

            if (!File.Exists(packPath)) {
                failures.Add(new ValidationFailure(
                    CommandArguments.PackPath,
                    $"No file exists at '{packPath}' on the machine running the AppHost."));
                packPath = null;
            }
        }

        var importSchema = CommandArguments.ReadBoolean(arguments, CommandArguments.ImportSchema);
        var reset = CommandArguments.ReadBoolean(arguments, CommandArguments.Reset);
        var confirmDestroy = CommandArguments.ReadBoolean(arguments, CommandArguments.ConfirmDestroy);

        if (reset && !importSchema) {
            failures.Add(new ValidationFailure(
                CommandArguments.Reset,
                "Reset creates a completely empty database, so a data-only import would have no schema to import into. Turn on Import schema, or turn off Reset."));
        }

        if (reset && !confirmDestroy) {
            failures.Add(new ValidationFailure(
                CommandArguments.ConfirmDestroy,
                "Confirm that the database may be dropped and recreated before importing."));
        }

        if (packPath is not null) {
            PackInfo? info = null;
            try {
                info = await inspector.InspectAsync(packPath, cancellationToken);
            }
            catch (PackUnreadableException ex) {
                failures.Add(new ValidationFailure(packInput, ex.Message));
            }

            if (info is not null && importSchema && !info.ContainsDacpac) {
                failures.Add(new ValidationFailure(
                    CommandArguments.ImportSchema,
                    "This pack carries no schema. Export it with SchemaCaptureMode.Dacpac, or turn off Import schema to import data only."));
            }
        }

        return failures.Count > 0
            ? new ImportValidationResult(null, failures)
            : new ImportValidationResult(new ImportRequest(packPath!, importSchema, reset), failures);
    }

    public static IReadOnlyList<ValidationFailure> ValidateReset(InteractionInputCollection arguments) =>
        CommandArguments.ReadBoolean(arguments, CommandArguments.ConfirmDestroy)
            ? []
            : [new ValidationFailure(
                CommandArguments.ConfirmDestroy,
                "Confirm that the database may be dropped and recreated.")];
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ImportValidationTests`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add import and reset validation rules"
```

---

### Task 5: Resource availability rule

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/ResourceAvailability.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/ResourceAvailabilityTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal static class ResourceAvailability` with `ResourceCommandState StateFor(string? stateText, HealthStatus? health)`.

Why this is a separate pure function rather than a lambda reading the snapshot: `CustomResourceSnapshot.HealthStatus` has a private setter and `HealthReports` an internal one, verified against Aspire 13.5.3. A test outside Aspire's own assembly cannot build an unhealthy snapshot. Taking the two values as parameters makes every branch testable and leaves the untestable part a single property read in Task 9.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/ResourceAvailabilityTests.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ResourceAvailabilityTests {
    [Fact]
    public void RunningAndHealthy_IsEnabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Enabled);

    [Fact]
    public void RunningWithNoHealthChecksRegistered_IsEnabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, null)
            .ShouldBe(ResourceCommandState.Enabled);

    [Fact]
    public void RunningButUnhealthy_IsDisabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Unhealthy)
            .ShouldBe(ResourceCommandState.Disabled);

    [Fact]
    public void RunningButDegraded_IsDisabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Degraded)
            .ShouldBe(ResourceCommandState.Disabled);

    [Theory]
    [InlineData(KnownResourceStates.Starting)]
    [InlineData(KnownResourceStates.Exited)]
    [InlineData(KnownResourceStates.FailedToStart)]
    [InlineData(KnownResourceStates.Finished)]
    [InlineData(KnownResourceStates.Waiting)]
    public void NotRunning_IsDisabled(string state) =>
        ResourceAvailability.StateFor(state, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Disabled);

    [Fact]
    public void UnknownState_IsDisabled() =>
        ResourceAvailability.StateFor(null, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Disabled);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ResourceAvailabilityTests`
Expected: FAIL to compile, `ResourceAvailability` does not exist.

If any `KnownResourceStates` constant in the `[Theory]` does not exist in Aspire 13.5.3, drop that one `InlineData` line rather than inventing a name.

- [ ] **Step 3: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/ResourceAvailability.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Whether the commands can run against a resource in a given state. Takes the two values rather
/// than a snapshot because a snapshot's health cannot be constructed outside Aspire's assembly.
/// </summary>
internal static class ResourceAvailability {
    public static ResourceCommandState StateFor(string? stateText, HealthStatus? health) {
        if (!string.Equals(stateText, KnownResourceStates.Running, StringComparison.Ordinal)) {
            return ResourceCommandState.Disabled;
        }

        // null means no health check is registered for this resource. Treating that as unhealthy
        // would disable the commands permanently.
        return health is null or HealthStatus.Healthy
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ResourceAvailabilityTests`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add resource availability rule for command state"
```

---

### Task 6: Progress seam

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/ProgressScope.cs`
- Create: `src/SqlDataPack.Aspire.Hosting/AspireProgressScope.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/ProgressScopeTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `internal interface IProgressScope { Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken); }`
  - `internal sealed class LoggingProgressScope : IProgressScope` with constructor `LoggingProgressScope(ILogger logger)`
  - `internal sealed class AspireProgressScope : IProgressScope` with constructor `AspireProgressScope(IInteractionService interactions, ILogger logger)`

`AspireProgressScope.cs` is the only file permitted to suppress `ASPIREINTERACTION001`.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/ProgressScopeTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ProgressScopeTests {
    [Fact]
    public async Task LoggingScope_RunsTheWork() {
        var scope = new LoggingProgressScope(NullLogger.Instance);
        var ran = false;

        await scope.RunAsync("Importing data", _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task LoggingScope_PropagatesFailure() {
        var scope = new LoggingProgressScope(NullLogger.Instance);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => scope.RunAsync("Importing data", _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        exception.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task LoggingScope_PassesCancellationThrough() {
        var scope = new LoggingProgressScope(NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        CancellationToken seen = default;
        await scope.RunAsync("Importing data", ct => { seen = ct; return Task.CompletedTask; }, cts.Token);

        seen.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task PhasesRunInOrder() {
        var scope = new LoggingProgressScope(NullLogger.Instance);
        var phases = new List<string>();

        foreach (var phase in new[] { "Validating SQL Data Pack", "Resetting database", "Importing data" }) {
            await scope.RunAsync(phase, _ => { phases.Add(phase); return Task.CompletedTask; }, CancellationToken.None);
        }

        phases.ShouldBe(["Validating SQL Data Pack", "Resetting database", "Importing data"]);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ProgressScopeTests`
Expected: FAIL to compile, `LoggingProgressScope` does not exist.

- [ ] **Step 3: Write the seam and the fallback**

`src/SqlDataPack.Aspire.Hosting/ProgressScope.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Runs one phase of work while showing the user something. Kept behind an interface because the
/// Aspire implementation depends on an experimental API.
/// </summary>
internal interface IProgressScope {
    Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>Used when interactions are unavailable. Logs the phase and runs the work.</summary>
internal sealed class LoggingProgressScope(ILogger logger) : IProgressScope {
    public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
        logger.LogInformation("{Phase}", message);
        await work(cancellationToken);
    }
}
```

- [ ] **Step 4: Write the Aspire implementation**

`src/SqlDataPack.Aspire.Hosting/AspireProgressScope.cs`:

```csharp
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Shows an Aspire progress dialog for one phase. This is the only file that touches the
/// experimental progress API. Replace this class when Aspire stabilises it.
/// </summary>
#pragma warning disable ASPIREINTERACTION001
internal sealed class AspireProgressScope(IInteractionService interactions, ILogger logger) : IProgressScope {
    public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
        logger.LogInformation("{Phase}", message);

        if (!interactions.IsAvailable) {
            await work(cancellationToken);
            return;
        }

        Exception? failure = null;

        await interactions.PromptProgressAsync(message, new ProgressInteractionOptions {
            Work = async context => {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, context.CancellationToken);
                try {
                    await work(linked.Token);
                }
                catch (Exception ex) {
                    // Let the dialog close cleanly, then rethrow on the calling thread so the
                    // command result reports the real reason.
                    failure = ex;
                }
            }
        }, cancellationToken);

        if (failure is not null) {
            throw failure;
        }
    }
}
#pragma warning restore ASPIREINTERACTION001
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter ProgressScopeTests`
Expected: PASS, 4 tests.

- [ ] **Step 6: Verify the suppression is confined to one file**

Run: `grep -rn "ASPIREINTERACTION001" src/`
Expected: matches only in `src/SqlDataPack.Aspire.Hosting/AspireProgressScope.cs`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add progress seam isolating the experimental Aspire progress API"
```

---

### Task 7: Database resetter

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/SqlServerDatabaseResetter.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/SqlServerDatabaseResetterTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal static class SqlServerDatabaseResetter` with
  `string BuildResetScript(string databaseName)` and
  `Task ResetAsync(string serverConnectionString, string databaseName, CancellationToken cancellationToken)`.

The script builder is separated so identifier quoting is unit tested without a server. Real behaviour is covered in Task 10.

- [ ] **Step 1: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/SqlServerDatabaseResetterTests.cs`:

```csharp
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class SqlServerDatabaseResetterTests {
    [Fact]
    public void BuildResetScript_DropsThenCreates() {
        var script = SqlServerDatabaseResetter.BuildResetScript("catalog");

        script.ShouldContain("ALTER DATABASE [catalog] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        script.ShouldContain("DROP DATABASE [catalog]");
        script.ShouldContain("CREATE DATABASE [catalog]");
        script.IndexOf("DROP DATABASE", StringComparison.Ordinal)
            .ShouldBeLessThan(script.IndexOf("CREATE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildResetScript_EscapesClosingBracketsInTheName() {
        var script = SqlServerDatabaseResetter.BuildResetScript("we][ird");

        script.ShouldContain("[we]][ird]");
    }

    [Fact]
    public void BuildResetScript_ToleratesAMissingDatabase() {
        var script = SqlServerDatabaseResetter.BuildResetScript("catalog");

        script.ShouldContain("DB_ID");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildResetScript_RejectsABlankName(string? name) {
        Should.Throw<ArgumentException>(() => SqlServerDatabaseResetter.BuildResetScript(name!));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter SqlServerDatabaseResetterTests`
Expected: FAIL to compile, `SqlServerDatabaseResetter` does not exist.

- [ ] **Step 3: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/SqlServerDatabaseResetter.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Drops and recreates a local development database. Connects with the server connection string,
/// never the target database's own, so it is not holding a handle on what it drops.
/// </summary>
internal static class SqlServerDatabaseResetter {
    public static string BuildResetScript(string databaseName) {
        if (string.IsNullOrWhiteSpace(databaseName)) {
            throw new ArgumentException("A database name is required.", nameof(databaseName));
        }

        var quoted = $"[{databaseName.Replace("]", "]]", StringComparison.Ordinal)}]";
        var literal = databaseName.Replace("'", "''", StringComparison.Ordinal);

        return $"""
            IF DB_ID(N'{literal}') IS NOT NULL
            BEGIN
                ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quoted};
            END;
            CREATE DATABASE {quoted};
            """;
    }

    public static async Task ResetAsync(
        string serverConnectionString, string databaseName, CancellationToken cancellationToken) {

        // Point at master explicitly: the resource connection string names the database we are
        // about to drop, and you cannot drop the database your own session is using.
        var builder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildResetScript(databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter SqlServerDatabaseResetterTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add SQL Server database resetter"
```

---

### Task 8: Import operation

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/SqlDataPackImportOperation.cs`
- Test: covered by the integration suite in Task 11. No unit test: every branch requires a real SQL Server.

**Interfaces:**
- Consumes: `ImportRequest` (Task 4).
- Produces: `internal static class SqlDataPackImportOperation` with
  `Task<SqlDataPackResult> ImportAsync(ImportRequest request, string databaseConnectionString, ILogger logger, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the implementation**

`src/SqlDataPack.Aspire.Hosting/SqlDataPackImportOperation.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Runs the actual import. Holds no Aspire types so it can be driven straight from a test.
/// </summary>
internal static class SqlDataPackImportOperation {
    public static Task<SqlDataPackResult> ImportAsync(
        ImportRequest request,
        string databaseConnectionString,
        ILogger logger,
        CancellationToken cancellationToken) {

        var options = ImportOptions.Default;
        options.Logger = logger;
        options.SchemaDeploymentMode = request.ImportSchema
            ? SchemaDeploymentMode.DeployDacpac
            : SchemaDeploymentMode.None;

        return new SqlDataPackImporter()
            .ImportAsync(request.PackPath, databaseConnectionString, options, cancellationToken);
    }
}
```

Verified signature: `SqlDataPackImporter.ImportAsync(string sqliteFilePath, string sqlServerConnectionString, ImportOptions? options = null, CancellationToken cancellationToken = default)`. Pack path first, connection string second. `ImportOptions`, `SchemaDeploymentMode` and `SqlDataPackResult` are all in `SqlDataPack.Models`; `SqlDataPackImporter` is in the root `SqlDataPack` namespace.

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Add SQL Data Pack import operation"
```

---

### Task 9: Command wiring and registration

**Files:**
- Create: `src/SqlDataPack.Aspire.Hosting/SqlDataPackCommands.cs`
- Create: `src/SqlDataPack.Aspire.Hosting/SqlDataPackBuilderExtensions.cs`
- Test: `tests/SqlDataPack.Aspire.Hosting.Tests/WithSqlDataPackTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 through 8.
- Produces: `public static IResourceBuilder<SqlServerDatabaseResource> WithSqlDataPack(this IResourceBuilder<SqlServerDatabaseResource> builder, Action<SqlDataPackCommandOptions>? configure = null)`.

Add `Aspire.Hosting.SqlServer` to the library project and to the test project before starting: `SqlServerDatabaseResource` lives there, not in `Aspire.Hosting`.

- [ ] **Step 1: Add the SqlServer package reference**

In `src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj`, add to the existing `ItemGroup`:

```xml
    <PackageReference Include="Aspire.Hosting.SqlServer" />
```

In `tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`, add to the package `ItemGroup`:

```xml
    <PackageReference Include="Aspire.Hosting.SqlServer" />
```

- [ ] **Step 2: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.Tests/WithSqlDataPackTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using SqlDataPack.Aspire.Hosting;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class WithSqlDataPackTests {
    private static IDistributedApplicationBuilder RunModeBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });

    private static IDistributedApplicationBuilder PublishModeBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions {
            Args = ["--operation", "publish"], DisableDashboard = true
        });

    private static IReadOnlyList<ResourceCommandAnnotation> CommandsOn(IResourceBuilder<SqlServerDatabaseResource> db) =>
        db.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

    [Fact]
    public void RunMode_RegistersBothCommands() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var names = CommandsOn(db).Select(c => c.Name).ToList();

        names.ShouldContain("sqldatapack-import");
        names.ShouldContain("sqldatapack-reset");
    }

    [Fact]
    public void RunMode_UsesTheSpecifiedDisplayNames() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var commands = CommandsOn(db);

        commands.Single(c => c.Name == "sqldatapack-import").DisplayName.ShouldBe("Import SQL Data Pack");
        commands.Single(c => c.Name == "sqldatapack-reset").DisplayName.ShouldBe("Reset Database");
    }

    [Fact]
    public void PublishMode_RegistersNothing() {
        var db = PublishModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        CommandsOn(db).ShouldBeEmpty();
    }

    [Fact]
    public void DefaultVisibility_IncludesApiSoMcpClientsCanDiscoverIt() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        foreach (var command in CommandsOn(db)) {
            command.Visibility.HasFlag(ResourceCommandVisibility.Api).ShouldBeTrue();
            command.Visibility.HasFlag(ResourceCommandVisibility.UI).ShouldBeTrue();
        }
    }

    [Fact]
    public void Visibility_IsConfigurable() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => o.Visibility = ResourceCommandVisibility.Api);

        CommandsOn(db).ShouldAllBe(c => c.Visibility == ResourceCommandVisibility.Api);
    }

    [Fact]
    public void DefaultPackSource_DeclaresBothInputs() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldContain(CommandArguments.PackFile);
        names.ShouldContain(CommandArguments.PackPath);
    }

    [Fact]
    public void PathOnly_OmitsTheUploadInput() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => o.PackSource = SqlDataPackSource.Path);

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldNotContain(CommandArguments.PackFile);
        names.ShouldContain(CommandArguments.PackPath);
    }

    [Fact]
    public void UploadOnly_OmitsThePathInput() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => {
                o.PackSource = SqlDataPackSource.Upload;
                o.Visibility = ResourceCommandVisibility.UI;
            });

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldContain(CommandArguments.PackFile);
        names.ShouldNotContain(CommandArguments.PackPath);
    }

    [Fact]
    public void UploadOnlyWithApiVisibility_ThrowsBecauseAgentsCannotUpload() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog");

        var exception = Should.Throw<ArgumentException>(() => db.WithSqlDataPack(o => {
            o.PackSource = SqlDataPackSource.Upload;
            o.Visibility = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;
        }));

        exception.Message.ShouldContain("cannot supply an uploaded file");
    }

    [Fact]
    public void UploadInput_FiltersToPackExtensions() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var file = ImportArguments(db).Single(a => a.Name == CommandArguments.PackFile);

        file.InputType.ShouldBe(InputType.File);
        file.FileFilter.ShouldBe(".sqlite,.sqldatapack,.db");
    }

    [Fact]
    public void ImportSchema_DefaultsToChecked() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var input = ImportArguments(db).Single(a => a.Name == CommandArguments.ImportSchema);

        input.InputType.ShouldBe(InputType.Boolean);
        input.Value.ShouldBe("true");
    }

    [Theory]
    [InlineData(CommandArguments.Reset)]
    [InlineData(CommandArguments.ConfirmDestroy)]
    public void DestructiveInputs_DefaultToUnchecked(string name) {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var input = ImportArguments(db).Single(a => a.Name == name);

        input.InputType.ShouldBe(InputType.Boolean);
        input.Value.ShouldBe("false");
    }

    [Fact]
    public void ResetCommand_TakesOnlyAConfirmation() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var reset = CommandsOn(db).Single(c => c.Name == "sqldatapack-reset");

        reset.Arguments.ShouldNotBeNull();
        reset.Arguments!.Select(a => a.Name).ShouldBe([CommandArguments.ConfirmDestroy]);
    }

    [Fact]
    public void BothCommands_AreGatedOnResourceState() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        CommandsOn(db).ShouldAllBe(c => c.UpdateState != null);
    }

    [Fact]
    public void NullBuilder_Throws() {
        IResourceBuilder<SqlServerDatabaseResource> db = null!;

        Should.Throw<ArgumentNullException>(() => db.WithSqlDataPack());
    }

    private static IReadOnlyList<InteractionInput> ImportArguments(IResourceBuilder<SqlServerDatabaseResource> db) =>
        CommandsOn(db).Single(c => c.Name == "sqldatapack-import").Arguments ?? [];
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter WithSqlDataPackTests`
Expected: FAIL to compile, `WithSqlDataPack` does not exist.

- [ ] **Step 4: Write the command definitions**

`src/SqlDataPack.Aspire.Hosting/SqlDataPackCommands.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// The Aspire side of the integration: argument declarations, validation adapters, and the two
/// command bodies. All of the real work lives in the operation classes.
/// </summary>
internal static class SqlDataPackCommands {
    public const string ImportCommandName = "sqldatapack-import";
    public const string ResetCommandName = "sqldatapack-reset";
    public const string FileFilter = ".sqlite,.sqldatapack,.db";

    // CommandOptions.Description becomes ResourceCommandAnnotation.DisplayDescription, which the
    // dashboard uses as a tooltip on the menu item, not as dialog body text. There is no dialog
    // message slot on a declarative command, so the V1 caveat rides on an input description, which
    // does support markdown. Task 12 step 4 checks where it actually lands.
    private const string ImportCaveat =
        """
        **V1 does not merge or remove existing data.** Without reset, schema + data import expects a
        blank database. Data-only import expects the required schema to already exist and its target
        tables to be empty.
        """;

    public static IReadOnlyList<InteractionInput> ImportArguments(SqlDataPackSource packSource) {
        var inputs = new List<InteractionInput>();

        if (packSource.HasFlag(SqlDataPackSource.Upload)) {
            inputs.Add(new InteractionInput {
                Name = CommandArguments.PackFile,
                Label = "SQL Data Pack",
                Description = "Upload a pack from this machine.",
                InputType = InputType.File,
                FileFilter = FileFilter
            });
        }

        if (packSource.HasFlag(SqlDataPackSource.Path)) {
            inputs.Add(new InteractionInput {
                Name = CommandArguments.PackPath,
                Label = "SQL Data Pack path",
                Description = "Path to a pack on the machine running the AppHost. Use this for packs over the upload limit.",
                InputType = InputType.Text,
                Placeholder = "C:\\packs\\dev-slice.sqlite"
            });
        }

        inputs.Add(new InteractionInput {
            Name = CommandArguments.ImportSchema,
            Label = "Import schema",
            Description = $"Checked imports schema and data. Unchecked imports data only.\n\n{ImportCaveat}",
            EnableDescriptionMarkdown = true,
            InputType = InputType.Boolean,
            Value = "true"
        });

        inputs.Add(new InteractionInput {
            Name = CommandArguments.Reset,
            Label = "Reset database first",
            Description = "Drops and recreates the database before importing.",
            InputType = InputType.Boolean,
            Value = "false"
        });

        inputs.Add(new InteractionInput {
            Name = CommandArguments.ConfirmDestroy,
            Label = "Yes, delete all data and schema first",
            Description = "Required when Reset database first is checked.",
            InputType = InputType.Boolean,
            Value = "false"
        });

        return inputs;
    }

    public static IReadOnlyList<InteractionInput> ResetArguments(string databaseName) => [
        new InteractionInput {
            Name = CommandArguments.ConfirmDestroy,
            Label = $"Yes, reset \"{databaseName}\"",
            Description = $"All data and schema in the local development database \"{databaseName}\" will be deleted.",
            InputType = InputType.Boolean,
            Value = "false"
        }
    ];

    public static CommandOptions ImportOptionsFor(SqlDataPackCommandOptions options) => new() {
        Description = "Import a SQL Data Pack into this local development database.",
        Arguments = ImportArguments(options.PackSource),
        Visibility = options.Visibility,
        IconName = "DatabaseArrowDown",
        UpdateState = UpdateState,
        ValidateArguments = context => ValidateImportAsync(context, options.PackSource)
    };

    public static CommandOptions ResetOptionsFor(SqlServerDatabaseResource resource, SqlDataPackCommandOptions options) => new() {
        Description = $"Drop and recreate \"{resource.DatabaseName}\" as an empty database.",
        Arguments = ResetArguments(resource.DatabaseName),
        Visibility = options.Visibility,
        IconName = "DatabaseWarning",
        UpdateState = UpdateState,
        ValidateArguments = context => {
            AddFailures(context, ImportValidation.ValidateReset(context.Inputs));
            return Task.CompletedTask;
        }
    };

    public static ResourceCommandState UpdateState(UpdateCommandStateContext context) =>
        ResourceAvailability.StateFor(context.ResourceSnapshot.State?.Text, context.ResourceSnapshot.HealthStatus);

    private static async Task ValidateImportAsync(InputsDialogValidationContext context, SqlDataPackSource packSource) {
        var result = await ImportValidation.ValidateAsync(
            context.Inputs, packSource, new SqlDataPackInspector(), context.CancellationToken);

        AddFailures(context, result.Failures);
    }

    private static void AddFailures(InputsDialogValidationContext context, IReadOnlyList<ValidationFailure> failures) {
        foreach (var failure in failures) {
            context.AddValidationError(failure.InputName, failure.Message);
        }
    }

    public static Func<ExecuteCommandContext, Task<ExecuteCommandResult>> ImportHandler(
        SqlServerDatabaseResource resource, SqlDataPackCommandOptions options) => async context => {

        var progress = CreateProgress(context);

        try {
            var validation = await ImportValidation.ValidateAsync(
                context.Arguments, options.PackSource, new SqlDataPackInspector(), context.CancellationToken);

            if (validation.Request is null) {
                return Failure(string.Join(" ", validation.Failures.Select(f => f.Message)));
            }

            var request = validation.Request;

            await progress.RunAsync("Validating SQL Data Pack",
                _ => Task.CompletedTask, context.CancellationToken);

            if (request.Reset) {
                var serverConnectionString = await RequireConnectionStringAsync(resource.Parent, context.CancellationToken);
                await progress.RunAsync("Resetting database",
                    ct => SqlServerDatabaseResetter.ResetAsync(serverConnectionString, resource.DatabaseName, ct),
                    context.CancellationToken);
            }

            var databaseConnectionString = await RequireConnectionStringAsync(resource, context.CancellationToken);
            var phase = request.ImportSchema ? "Applying schema and importing data" : "Importing data";

            await progress.RunAsync(phase,
                ct => SqlDataPackImportOperation.ImportAsync(request, databaseConnectionString, context.Logger, ct),
                context.CancellationToken);

            return new ExecuteCommandResult { Success = true, Message = "SQL Data Pack imported successfully." };
        }
        catch (OperationCanceledException) {
            return new ExecuteCommandResult { Success = false, Canceled = true };
        }
        catch (Exception ex) {
            context.Logger.LogError(ex, "Importing a SQL Data Pack into {Database} failed.", resource.DatabaseName);
            return Failure(ex.Message);
        }
    };

    public static Func<ExecuteCommandContext, Task<ExecuteCommandResult>> ResetHandler(
        SqlServerDatabaseResource resource) => async context => {

        var progress = CreateProgress(context);

        try {
            var failures = ImportValidation.ValidateReset(context.Arguments);
            if (failures.Count > 0) {
                return Failure(failures[0].Message);
            }

            var serverConnectionString = await RequireConnectionStringAsync(resource.Parent, context.CancellationToken);

            await progress.RunAsync("Resetting database",
                ct => SqlServerDatabaseResetter.ResetAsync(serverConnectionString, resource.DatabaseName, ct),
                context.CancellationToken);

            return new ExecuteCommandResult { Success = true, Message = "Database reset successfully." };
        }
        catch (OperationCanceledException) {
            return new ExecuteCommandResult { Success = false, Canceled = true };
        }
        catch (Exception ex) {
            context.Logger.LogError(ex, "Resetting {Database} failed.", resource.DatabaseName);
            return Failure(ex.Message);
        }
    };

    private static IProgressScope CreateProgress(ExecuteCommandContext context) {
        var interactions = context.Services.GetService<IInteractionService>();
        return interactions is null
            ? new LoggingProgressScope(context.Logger)
            : new AspireProgressScope(interactions, context.Logger);
    }

    private static async Task<string> RequireConnectionStringAsync(
        IResourceWithConnectionString resource, CancellationToken cancellationToken) {

        var connectionString = await resource.GetConnectionStringAsync(cancellationToken);
        return connectionString
            ?? throw new InvalidOperationException($"Resource '{resource.Name}' has no connection string yet.");
    }

    private static ExecuteCommandResult Failure(string message) =>
        new() { Success = false, ErrorMessage = message };
}
```

- [ ] **Step 5: Write the registration extension**

`src/SqlDataPack.Aspire.Hosting/SqlDataPackBuilderExtensions.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

using global::SqlDataPack.Aspire.Hosting;

/// <summary>
/// Adds SQL Data Pack development commands to an Aspire SQL Server database resource.
/// </summary>
public static class SqlDataPackBuilderExtensions {
    /// <summary>
    /// Adds the <c>Import SQL Data Pack</c> and <c>Reset Database</c> commands to a local
    /// development database. Does nothing outside run mode.
    /// </summary>
    /// <param name="builder">The database resource builder.</param>
    /// <param name="configure">Optional settings for pack source and client visibility.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IResourceBuilder<SqlServerDatabaseResource> WithSqlDataPack(
        this IResourceBuilder<SqlServerDatabaseResource> builder,
        Action<SqlDataPackCommandOptions>? configure = null) {

        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlDataPackCommandOptions();
        configure?.Invoke(options);

        if (options.PackSource == SqlDataPackSource.Upload
            && options.Visibility.HasFlag(ResourceCommandVisibility.Api)) {
            throw new ArgumentException(
                "PackSource.Upload cannot be combined with Api visibility, because an API client such as MCP "
                + "cannot supply an uploaded file. Add SqlDataPackSource.Path, or remove ResourceCommandVisibility.Api.",
                nameof(configure));
        }

        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode) {
            return builder;
        }

        var resource = builder.Resource;

        return builder
            .WithCommand(
                SqlDataPackCommands.ImportCommandName,
                "Import SQL Data Pack",
                SqlDataPackCommands.ImportHandler(resource, options),
                SqlDataPackCommands.ImportOptionsFor(options))
            .WithCommand(
                SqlDataPackCommands.ResetCommandName,
                "Reset Database",
                SqlDataPackCommands.ResetHandler(resource),
                SqlDataPackCommands.ResetOptionsFor(resource, options));
    }
}
```

The extension lives in namespace `Aspire.Hosting` so it is available without an extra `using` in an AppHost, which is the convention every Aspire integration follows.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --filter WithSqlDataPackTests`
Expected: PASS, 16 tests.

If `IconName` values `DatabaseArrowDown` or `DatabaseWarning` are rejected, drop the `IconName` lines rather than guessing at another icon name. They are cosmetic.

- [ ] **Step 7: Run the whole unit suite**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj`
Expected: PASS, all tests from Tasks 1 through 9.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Wire up SQL Data Pack import and reset resource commands"
```

---

### Task 10: Integration suite and reset behaviour

**Files:**
- Create: `tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj`
- Create: `tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlServerFixture.cs`
- Create: `tests/SqlDataPack.Aspire.Hosting.IntegrationTests/ResetTests.cs`

**Interfaces:**
- Consumes: `SqlServerDatabaseResetter` (Task 7).
- Produces: `SqlServerFixture` with `string ServerConnectionString { get; }` and
  `Task<string> CreateDatabaseAsync(string name)`, used by Task 11.

- [ ] **Step 1: Create the integration test project**

`tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj" />
  </ItemGroup>

</Project>
```

Then: `dotnet sln SqlDataPack.Aspire.slnx add tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj`

- [ ] **Step 2: Write the fixture**

`tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlServerFixture.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime {
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ServerConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync(string name) {
        await ExecuteOnMasterAsync($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];");

        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = name };
        return builder.ConnectionString;
    }

    public async Task ExecuteOnMasterAsync(string sql) {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string connectionString, string sql) {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T))!;
    }
}

[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
```

- [ ] **Step 3: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.IntegrationTests/ResetTests.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public sealed class ResetTests(SqlServerFixture fixture) {
    [Fact]
    public async Task Reset_RemovesEveryTable() {
        var name = $"reset_{Guid.NewGuid():N}";
        var connectionString = await fixture.CreateDatabaseAsync(name);

        await using (var connection = new SqlConnection(connectionString)) {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.Widgets(Id int primary key); INSERT INTO dbo.Widgets VALUES (1);";
            await command.ExecuteNonQueryAsync();
        }

        await SqlServerDatabaseResetter.ResetAsync(fixture.ServerConnectionString, name, CancellationToken.None);

        var tables = await fixture.ScalarAsync<int>(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;");
        tables.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_LeavesTheDatabaseUsable() {
        var name = $"reset_{Guid.NewGuid():N}";
        var connectionString = await fixture.CreateDatabaseAsync(name);

        await SqlServerDatabaseResetter.ResetAsync(fixture.ServerConnectionString, name, CancellationToken.None);

        var actual = await fixture.ScalarAsync<string>(connectionString, "SELECT DB_NAME();");
        actual.ShouldBe(name);
    }

    [Fact]
    public async Task Reset_EvictsAnOpenConnectionInsteadOfFailing() {
        var name = $"reset_{Guid.NewGuid():N}";
        var connectionString = await fixture.CreateDatabaseAsync(name);

        await using var holder = new SqlConnection(connectionString);
        await holder.OpenAsync();

        await Should.NotThrowAsync(() =>
            SqlServerDatabaseResetter.ResetAsync(fixture.ServerConnectionString, name, CancellationToken.None));
    }

    [Fact]
    public async Task Reset_CreatesTheDatabaseWhenItDoesNotExist() {
        var name = $"reset_{Guid.NewGuid():N}";

        await SqlServerDatabaseResetter.ResetAsync(fixture.ServerConnectionString, name, CancellationToken.None);

        var exists = await fixture.ScalarAsync<int>(
            new SqlConnectionStringBuilder(fixture.ServerConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{name}';");
        exists.ShouldBe(1);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails, then passes**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj`
Expected: PASS, 4 tests. Docker must be running. First run pulls the SQL Server image and takes several minutes.

If it fails to compile because `SqlServerDatabaseResetter` is not visible, confirm the `InternalsVisibleTo` entry from Task 1 names `SqlDataPack.Aspire.Hosting.IntegrationTests`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add Docker-backed integration suite covering database reset"
```

---

### Task 11: Import integration tests

**Files:**
- Create: `tests/SqlDataPack.Aspire.Hosting.IntegrationTests/PackFactory.cs`
- Create: `tests/SqlDataPack.Aspire.Hosting.IntegrationTests/ImportTests.cs`

**Interfaces:**
- Consumes: `SqlServerFixture` (Task 10), `ImportRequest` (Task 4), `SqlDataPackImportOperation` (Task 8), `SqlDataPackInspector` (Task 2).
- Produces: `PackFactory.ExportAsync(string sourceConnectionString, string outputPath, bool withDacpac)`.

Packs are generated at test time from a real database rather than committed as binary fixtures. The core library's package writer is internal to its own assembly, so hand-building a pack file here would duplicate a versioned format this repo does not own.

- [ ] **Step 1: Write the pack factory**

`tests/SqlDataPack.Aspire.Hosting.IntegrationTests/PackFactory.cs`:

```csharp
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

/// <summary>
/// Builds real packs by exporting from a real database. The library's package writer is internal
/// to its own assembly, so this is the only supported way to produce a valid pack.
/// </summary>
internal static class PackFactory {
    public static async Task ExportAsync(string sourceConnectionString, string outputPath, bool withDacpac) {
        var options = ExportOptions.Default;
        options.SchemaCaptureMode = withDacpac ? SchemaCaptureMode.Dacpac : SchemaCaptureMode.None;

        await new SqlDataPackExporter().ExportAsync(sourceConnectionString, outputPath, options, CancellationToken.None);
    }
}
```

Verified signature: `SqlDataPackExporter.ExportAsync(string sqlServerConnectionString, string sqliteFilePath, ExportOptions? options = null, CancellationToken cancellationToken = default)`. Connection string first, output path second, which is the opposite order from `ImportAsync`. `SchemaCaptureMode` has exactly two members, `None` and `Dacpac`, and lives in `SqlDataPack.Models`.

- [ ] **Step 2: Write the failing test**

`tests/SqlDataPack.Aspire.Hosting.IntegrationTests/ImportTests.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public sealed class ImportTests(SqlServerFixture fixture) : IDisposable {
    private readonly string _packDirectory = Directory.CreateTempSubdirectory("sqldatapack-aspire").FullName;

    public void Dispose() => Directory.Delete(_packDirectory, recursive: true);

    private string PackPath(string name) => Path.Combine(_packDirectory, $"{name}.sqlite");

    private async Task<string> SeedSourceAsync(string name) {
        var connectionString = await fixture.CreateDatabaseAsync(name);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE dbo.Widgets(Id int NOT NULL PRIMARY KEY, Name nvarchar(64) NOT NULL);
            INSERT INTO dbo.Widgets(Id, Name) VALUES (1, N'first'), (2, N'second');
            """;
        await command.ExecuteNonQueryAsync();
        return connectionString;
    }

    [Fact]
    public async Task SchemaAndData_ImportIntoABlankDatabase() {
        var source = await SeedSourceAsync($"src_{Guid.NewGuid():N}");
        var pack = PackPath("with-schema");
        await PackFactory.ExportAsync(source, pack, withDacpac: true);

        var targetName = $"tgt_{Guid.NewGuid():N}";
        var target = await fixture.CreateDatabaseAsync(targetName);

        await SqlDataPackImportOperation.ImportAsync(
            new ImportRequest(pack, ImportSchema: true, Reset: false), target, NullLogger.Instance, CancellationToken.None);

        var rows = await fixture.ScalarAsync<int>(target, "SELECT COUNT(*) FROM dbo.Widgets;");
        rows.ShouldBe(2);
    }

    [Fact]
    public async Task DataOnly_ImportsIntoAnExistingEmptySchema() {
        var source = await SeedSourceAsync($"src_{Guid.NewGuid():N}");
        var pack = PackPath("data-only");
        await PackFactory.ExportAsync(source, pack, withDacpac: false);

        var targetName = $"tgt_{Guid.NewGuid():N}";
        var target = await fixture.CreateDatabaseAsync(targetName);
        await using (var connection = new SqlConnection(target)) {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.Widgets(Id int NOT NULL PRIMARY KEY, Name nvarchar(64) NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        await SqlDataPackImportOperation.ImportAsync(
            new ImportRequest(pack, ImportSchema: false, Reset: false), target, NullLogger.Instance, CancellationToken.None);

        var rows = await fixture.ScalarAsync<int>(target, "SELECT COUNT(*) FROM dbo.Widgets;");
        rows.ShouldBe(2);
    }

    [Fact]
    public async Task ResetThenImport_ReplacesTheDatabase() {
        var source = await SeedSourceAsync($"src_{Guid.NewGuid():N}");
        var pack = PackPath("reset-then-import");
        await PackFactory.ExportAsync(source, pack, withDacpac: true);

        var targetName = $"tgt_{Guid.NewGuid():N}";
        var target = await fixture.CreateDatabaseAsync(targetName);
        await using (var connection = new SqlConnection(target)) {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.Stale(Id int);";
            await command.ExecuteNonQueryAsync();
        }

        await SqlServerDatabaseResetter.ResetAsync(fixture.ServerConnectionString, targetName, CancellationToken.None);
        await SqlDataPackImportOperation.ImportAsync(
            new ImportRequest(pack, ImportSchema: true, Reset: true), target, NullLogger.Instance, CancellationToken.None);

        var stale = await fixture.ScalarAsync<int>(target,
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'Stale';");
        stale.ShouldBe(0);

        var rows = await fixture.ScalarAsync<int>(target, "SELECT COUNT(*) FROM dbo.Widgets;");
        rows.ShouldBe(2);
    }

    [Fact]
    public async Task Inspector_ReportsWhetherAPackCarriesSchema() {
        var source = await SeedSourceAsync($"src_{Guid.NewGuid():N}");
        var withSchema = PackPath("inspect-with");
        var withoutSchema = PackPath("inspect-without");
        await PackFactory.ExportAsync(source, withSchema, withDacpac: true);
        await PackFactory.ExportAsync(source, withoutSchema, withDacpac: false);

        var inspector = new SqlDataPackInspector();

        (await inspector.InspectAsync(withSchema, CancellationToken.None)).ContainsDacpac.ShouldBeTrue();
        (await inspector.InspectAsync(withoutSchema, CancellationToken.None)).ContainsDacpac.ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj`
Expected: PASS, 8 tests total across both integration files.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add integration coverage for schema, data-only, and reset-then-import"
```

---

### Task 12: Sample AppHost

**Files:**
- Create: `samples/SqlDataPack.Aspire.Sample/SqlDataPack.Aspire.Sample.csproj`
- Create: `samples/SqlDataPack.Aspire.Sample/AppHost.cs`
- Create: `samples/SqlDataPack.Aspire.Sample/Properties/launchSettings.json`

**Interfaces:**
- Consumes: `WithSqlDataPack` (Task 9).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Create the sample project**

`samples/SqlDataPack.Aspire.Sample/SqlDataPack.Aspire.Sample.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.3" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" />
    <PackageReference Include="Aspire.Hosting.SqlServer" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/SqlDataPack.Aspire.Hosting/SqlDataPack.Aspire.Hosting.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the AppHost**

`samples/SqlDataPack.Aspire.Sample/AppHost.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
                 .WithDataVolume("sqldatapack-aspire-sample");

sql.AddDatabase("catalog")
   .WithSqlDataPack();

builder.Build().Run();
```

- [ ] **Step 3: Add to the solution and build**

Run:

```bash
dotnet sln SqlDataPack.Aspire.slnx add samples/SqlDataPack.Aspire.Sample/SqlDataPack.Aspire.Sample.csproj
dotnet build SqlDataPack.Aspire.slnx
```

Expected: Build succeeded.

- [ ] **Step 4: Verify the acceptance criteria by hand**

Run: `dotnet run --project samples/SqlDataPack.Aspire.Sample`

Then in the dashboard, confirm each of these and note the result:

1. The `catalog` resource shows `Import SQL Data Pack` and `Reset Database`.
2. Both commands are disabled while the resource is starting, and enabled once running.
3. `Reset Database` refuses to run until the confirmation box is ticked.
4. `Import SQL Data Pack` shows an upload field, a path field, and three checkboxes.
5. Submitting with neither pack supplied shows an error against the path field.
6. Submitting a pack with no dacpac and `Import schema` ticked shows an error against that checkbox.
7. Ticking `Reset database first` with `Import schema` unticked shows an error against the reset checkbox.
8. A successful import reports `SQL Data Pack imported successfully.` and the Console tab shows per-table progress.
9. The V1 caveat text is legible somewhere in the dialog. If it is buried in a tooltip, move `ImportCaveat` onto a different input's `Description` until it shows, and say which one in the commit.

Then, for the MCP acceptance criteria, which no automated test covers because they need a live
client attached to a running AppHost:

10. Connect an MCP client to the running AppHost and list the resource commands on `catalog`. Both appear.
11. Invoke `sqldatapack-import` with `{"packPath": "<path to a pack>", "importSchema": true}` and no dashboard interaction. It succeeds.
12. Invoke `sqldatapack-reset` with no `confirmDestroy`. It is refused rather than resetting, and refused without waiting on a dashboard prompt.

If the MCP surface cannot be exercised at this point, record checks 10 through 12 as unverified in
the commit message. Do not tick them off unobserved.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add sample AppHost demonstrating the SQL Data Pack commands"
```

---

### Task 13: Formatter, CI, and documentation

**Files:**
- Create: `.editorconfig`, `build/cleanup.sh`, `.config/dotnet-tools.json`
- Create: `.github/workflows/ci.yml`
- Create: `README.md`, `AGENTS.md`

**Interfaces:**
- Consumes: everything.
- Produces: nothing.

- [ ] **Step 1: Copy the formatter setup from the core repo**

Run:

```bash
mkdir -p build .config
cp /d/code/sqldatapack/.editorconfig .editorconfig
cp /d/code/sqldatapack/build/cleanup.sh build/cleanup.sh
cp /d/code/sqldatapack/.config/dotnet-tools.json .config/dotnet-tools.json
```

Then open `build/cleanup.sh` and change any hard-coded solution name from `SqlDataPack.slnx` to `SqlDataPack.Aspire.slnx`. If any of those source files does not exist, skip that copy and note it; do not invent a replacement.

- [ ] **Step 2: Write the CI workflow**

`.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore SqlDataPack.Aspire.slnx --locked-mode

      - name: Build
        run: dotnet build SqlDataPack.Aspire.slnx --no-restore -c Release

      - name: Unit tests
        run: dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj --no-build -c Release

      - name: Integration tests
        run: dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj --no-build -c Release

  format:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - run: dotnet tool restore
      - run: build/cleanup.sh
      - run: git diff --exit-code
```

The integration job needs Docker. `ubuntu-latest` runners have it. If the SQL Server image will not start there, move that step to a separate job with a documented skip rather than deleting the tests.

- [ ] **Step 3: Write the README**

`README.md`:

````markdown
# SqlDataPack.Aspire.Hosting

Adds `Import SQL Data Pack` and `Reset Database` commands to a local Aspire SQL Server database.

This is a **local development integration**. It is not for production, and it deliberately refuses
arbitrary connection strings, external SQL Server instances, and Azure SQL.

## Install

```bash
dotnet add package SqlDataPack.Aspire.Hosting
```

## Use

```csharp
var sql = builder.AddSqlServer("sql");

sql.AddDatabase("catalog")
   .WithSqlDataPack();
```

Run the AppHost, open the `catalog` resource in the dashboard, and pick `Import SQL Data Pack`.

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

## Limits

- The upload field is capped by Aspire's server-side upload limit, 100 MB by default. Use the path
  field for anything larger. That limit cannot be raised from here.
- The path field reads any file the AppHost process can read. That is the point on your own
  machine. Think twice if the AppHost is reachable by something else.
- Visibility controls discovery, not authorization.
- Reset does not re-run a `WithCreationScript` configured on the database. You get a plain empty
  database back.
- V1 does not merge or remove existing data. Import into a blank database, or use reset.
````

- [ ] **Step 4: Write AGENTS.md**

`AGENTS.md`:

```markdown
# Repository Guidelines

Local development Aspire integration for SqlDataPack. The design and the reasoning behind it are in
`docs/superpowers/specs/2026-08-25-sqldatapack-aspire-hosting-design.md`. Read that before changing
behaviour; it records API facts that are easy to get wrong.

## Traps that have already bitten

- `InteractionInputCollection.GetBoolean` and `GetString` throw when an input has no value, which is
  what an unchecked checkbox looks like. Always read arguments through `CommandArguments`.
- `InteractionFile` has only an internal constructor and `InteractionInput.Files` a non-public
  setter, so a File input cannot be supplied by an MCP or CLI client, and cannot be faked in a test.
- `CustomResourceSnapshot.HealthStatus` has a private setter, which is why the availability rule is
  a pure function over two values rather than a lambda over a snapshot.
- Only `PromptProgressAsync` and friends are experimental. `ASPIREINTERACTION001` belongs in
  `AspireProgressScope.cs` and nowhere else.

## Commands

```bash
dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj          # fast
dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/SqlDataPack.Aspire.Hosting.IntegrationTests.csproj  # needs Docker
dotnet tool restore && build/cleanup.sh                                                             # formatter
```

After a version change in `Directory.Packages.props`, run
`dotnet restore SqlDataPack.Aspire.slnx --force-evaluate` and commit the refreshed lock files.
CI restores with `--locked-mode`.
```

- [ ] **Step 5: Verify the whole thing builds and the lock files are current**

Run:

```bash
dotnet restore SqlDataPack.Aspire.slnx --force-evaluate
dotnet build SqlDataPack.Aspire.slnx -c Release
dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/SqlDataPack.Aspire.Hosting.Tests.csproj
```

Expected: build succeeds, unit tests pass, and `packages.lock.json` exists next to every csproj.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add formatter setup, CI workflow, and documentation"
```

---

## Verification Checklist

Run before calling V1 done. Every line needs real output, not an assumption.

- [ ] `dotnet build SqlDataPack.Aspire.slnx -c Release` succeeds with zero warnings.
- [ ] `dotnet test tests/SqlDataPack.Aspire.Hosting.Tests/...` passes.
- [ ] `dotnet test tests/SqlDataPack.Aspire.Hosting.IntegrationTests/...` passes with Docker running.
- [ ] `grep -rn "ASPIREINTERACTION001" src/` matches only `AspireProgressScope.cs`.
- [ ] `grep -rn "GetBoolean\|GetString" src/` matches only `CommandArguments.cs`.
- [ ] `grep -rn "—\|–" src/ docs/ README.md AGENTS.md` returns nothing.
- [ ] `dotnet restore SqlDataPack.Aspire.slnx --locked-mode` succeeds.
- [ ] The 9 manual dashboard checks in Task 12 Step 4 have all been observed.
- [ ] The 3 MCP checks in Task 12 Step 4 have been observed, or recorded as unverified. Acceptance criteria 11 through 13 in the spec have no automated coverage, because a File input cannot be faked and an MCP invocation needs a live client.
