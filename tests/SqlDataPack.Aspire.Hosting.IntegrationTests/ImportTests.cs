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
        // Reset: true is inert here; ImportAsync never reads it. The explicit ResetAsync call above
        // is what actually resets the database for this test.
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
