using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime {
    // The parameterless MsSqlBuilder() constructor is obsolete; pass the image directly instead.
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
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
