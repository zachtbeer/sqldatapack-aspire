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
