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

        // Not cancellable: the batch drops the database before recreating it, so cancelling
        // mid-flight can leave the developer with no database at all. The whole thing takes
        // seconds, so there is nothing worth cancelling once it has started.
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
