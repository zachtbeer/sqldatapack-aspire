using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

/// <summary>
/// The two hooks against a real pack and a real database. The pre-import hook has plenty of unit
/// coverage; what needs a container is proving that its edits actually land in SQL Server, and that
/// the post-import hook sees the imported rows.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public sealed class ImportHookTests(SqlServerFixture fixture) : IDisposable {
    private readonly string _packDirectory = Directory.CreateTempSubdirectory("sqldatapack-aspire-hooks").FullName;

    public void Dispose() => Directory.Delete(_packDirectory, recursive: true);

    private async Task<string> SeedSourceAsync() {
        var connectionString = await fixture.CreateDatabaseAsync($"src_{Guid.NewGuid():N}");
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
    public async Task BeforeImport_EditsTheDataThatEndsUpInSqlServer() {
        var source = await SeedSourceAsync();
        var pack = Path.Combine(_packDirectory, "before-hook.sqlite");
        await PackFactory.ExportAsync(source, pack, withDacpac: true);

        var request = new ImportRequest(pack, ImportSchema: true, Reset: false);
        var info = await new SqlDataPackInspector().InspectAsync(pack, CancellationToken.None);

        await ImportHooks.RunBeforeAsync(async (context, ct) => {
            var table = context.SqliteTableFor("dbo", "Widgets");

            // The whole reason SqliteTableFor exists: a pack does not name its tables after the
            // source, so "UPDATE Widgets" would fail with "no such table".
            table.ShouldNotBe("Widgets");

            await using var command = context.Connection.CreateCommand();
            command.CommandText = $"UPDATE \"{table}\" SET Name = 'masked';";
            await command.ExecuteNonQueryAsync(ct);
        }, request, info.Manifest, NullLogger.Instance, CancellationToken.None);

        var target = await fixture.CreateDatabaseAsync($"tgt_{Guid.NewGuid():N}");
        await SqlDataPackImportOperation.ImportAsync(request, target, DacpacDeploymentOptions.Default, NullLogger.Instance, CancellationToken.None);

        var masked = await fixture.ScalarAsync<int>(target, "SELECT COUNT(*) FROM dbo.Widgets WHERE Name = N'masked';");
        masked.ShouldBe(2);
    }

    [Fact]
    public async Task AfterImport_WritesToTheTargetAndSeesTheImportResult() {
        var source = await SeedSourceAsync();
        var pack = Path.Combine(_packDirectory, "after-hook.sqlite");
        await PackFactory.ExportAsync(source, pack, withDacpac: true);

        var targetName = $"tgt_{Guid.NewGuid():N}";
        var target = await fixture.CreateDatabaseAsync(targetName);

        var result = await SqlDataPackImportOperation.ImportAsync(
            new ImportRequest(pack, ImportSchema: true, Reset: false), target, DacpacDeploymentOptions.Default, NullLogger.Instance, CancellationToken.None);

        SqlDataPackAfterImportContext? seen = null;

        await ImportHooks.RunAfterAsync(async (context, ct) => {
            seen = context;
            await using var command = context.Connection.CreateCommand();
            command.CommandText = "INSERT INTO dbo.Widgets(Id, Name) VALUES (99, N'dev-seed');";
            await command.ExecuteNonQueryAsync(ct);
        }, target, targetName, result, NullLogger.Instance, CancellationToken.None);

        seen.ShouldNotBeNull();
        seen.DatabaseName.ShouldBe(targetName);
        seen.Result.RowCount.ShouldBe(2);

        var rows = await fixture.ScalarAsync<int>(target, "SELECT COUNT(*) FROM dbo.Widgets;");
        rows.ShouldBe(3);
    }

    [Fact]
    public async Task AfterImport_HookThrows_SaysTheImportItselfSucceeded() {
        var targetName = $"tgt_{Guid.NewGuid():N}";
        var target = await fixture.CreateDatabaseAsync(targetName);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ImportHooks.RunAfterAsync(
            (_, _) => throw new InvalidOperationException("Invalid object name 'dbo.Widgits'."),
            target, targetName, new SqlDataPackResult(0, 0, []), NullLogger.Instance, CancellationToken.None));

        var message = SqlDataPackCommands.Describe(exception);
        message.ShouldContain("imported successfully");
        message.ShouldContain("AfterImport");
        message.ShouldContain("Invalid object name 'dbo.Widgits'.");
    }

    // Connecting to a database that does not exist fails at open, which is the shape of a container
    // that went away between the import finishing and the hook connection opening.
    [Fact]
    public async Task AfterImport_ConnectionCannotBeOpened_SaysTheHookDidNotRun() {
        var target = await fixture.CreateDatabaseAsync($"tgt_{Guid.NewGuid():N}");
        var missing = new SqlConnectionStringBuilder(target) { InitialCatalog = "no_such_database" }.ConnectionString;
        var ran = false;

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ImportHooks.RunAfterAsync(
            (_, _) => {
                ran = true;
                return Task.CompletedTask;
            },
            missing, "no_such_database", new SqlDataPackResult(0, 0, []), NullLogger.Instance, CancellationToken.None));

        ran.ShouldBeFalse();

        var message = SqlDataPackCommands.Describe(exception);
        message.ShouldContain("imported successfully");
        message.ShouldContain("did not run");
        message.ShouldNotContain("whatever the hook managed to change");
    }
}
