using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// What a <see cref="SqlDataPackCommandOptions.BeforeImport"/> hook gets to work with. The
/// connection is open on the pack file itself and is closed for you when the hook returns.
/// </summary>
public sealed class SqlDataPackBeforeImportContext {
    internal SqlDataPackBeforeImportContext(SqliteConnection connection, string packPath, bool importSchema, SqlDataPackManifest manifest, ILogger logger) {
        Connection = connection;
        PackPath = packPath;
        ImportSchema = importSchema;
        Manifest = manifest;
        Logger = logger;
    }

    /// <summary>An open read-write connection to the pack. Do not dispose it; the caller does.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Full path of the pack file being imported.</summary>
    public string PackPath { get; }

    /// <summary>Whether this import will also deploy the pack's schema.</summary>
    public bool ImportSchema { get; }

    /// <summary>
    /// The pack's manifest: which tables and columns it carries, what the export left out, and how
    /// each SQL Server table maps onto a table inside the SQLite file.
    /// </summary>
    public SqlDataPackManifest Manifest { get; }

    /// <summary>The command's logger. Anything written here shows up in the resource's Console tab.</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// The name of the SQLite table holding a SQL Server table's rows. A pack does not name its
    /// tables after the source, so this is how a hook builds its SQL.
    /// </summary>
    /// <param name="schema">Source schema name, for example <c>dbo</c>.</param>
    /// <param name="table">Source table name.</param>
    /// <returns>The table name to use in SQL against <see cref="Connection"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The pack does not carry that table, or it carries more than one that differs only by case
    /// and none of them is spelled the way you asked.
    /// </exception>
    public string SqliteTableFor(string schema, string table) {
        var candidates = Manifest.Tables
            .Where(t => NameMatches(t, schema, table, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) {
            throw new InvalidOperationException($"The pack does not carry the table {schema}.{table}. It carries: {string.Join(", ", Manifest.Tables.Select(t => t.FullName))}.");
        }

        if (candidates.Count == 1) {
            return candidates[0].SqliteTable;
        }

        // A case-sensitive source collation can carry dbo.Customer and dbo.customer at once. Only
        // the exact spelling says which one the hook meant.
        var exact = candidates.Where(t => NameMatches(t, schema, table, StringComparison.Ordinal)).ToList();

        return exact.Count == 1
            ? exact[0].SqliteTable
            : throw new InvalidOperationException($"The pack carries more than one table named {schema}.{table} apart from case: {string.Join(", ", candidates.Select(t => t.FullName))}. The source collation is case sensitive, so ask for one of those spellings exactly.");
    }

    private static bool NameMatches(SqlDataPackTableManifest table, string schema, string tableName, StringComparison comparison) =>
        string.Equals(table.SourceSchema, schema, comparison) && string.Equals(table.SourceTable, tableName, comparison);
}

/// <summary>
/// What a <see cref="SqlDataPackCommandOptions.AfterImport"/> hook gets to work with. Only runs
/// after the import succeeded, so the data is already in the database.
/// </summary>
public sealed class SqlDataPackAfterImportContext {
    internal SqlDataPackAfterImportContext(SqlConnection connection, string databaseName, SqlDataPackResult result, ILogger logger) {
        Connection = connection;
        DatabaseName = databaseName;
        Result = result;
        Logger = logger;
    }

    /// <summary>An open connection to the target database. Do not dispose it; the caller does.</summary>
    public SqlConnection Connection { get; }

    /// <summary>Name of the database that was imported into.</summary>
    public string DatabaseName { get; }

    /// <summary>What the import reported: table count, row count, and any warnings.</summary>
    public SqlDataPackResult Result { get; }

    /// <summary>The command's logger. Anything written here shows up in the resource's Console tab.</summary>
    public ILogger Logger { get; }
}

/// <summary>
/// Opens the connection each hook needs, runs it, and turns a failure into a message that says
/// where in the import it happened. Holds no Aspire types so it can be driven straight from a test.
/// </summary>
internal static class ImportHooks {
    // Pooling off, because Microsoft.Data.Sqlite parks a pooled connection with the file handle
    // still held and the importer opens the same file moments later.
    public static async Task RunBeforeAsync(Func<SqlDataPackBeforeImportContext, CancellationToken, Task> hook, ImportRequest request, SqlDataPackManifest manifest, ILogger logger, CancellationToken cancellationToken) {
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = request.PackPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);

        try {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidOperationException($"The pack file '{request.PackPath}' could not be opened for writing, so the BeforeImport hook did not run and nothing was imported.", ex);
        }

        var context = new SqlDataPackBeforeImportContext(connection, request.PackPath, request.ImportSchema, manifest, logger);

        try {
            await hook(context, cancellationToken);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidOperationException("The BeforeImport hook failed, so nothing was imported. Any changes it already made to the pack file are still there.", ex);
        }
    }

    public static async Task RunAfterAsync(Func<SqlDataPackAfterImportContext, CancellationToken, Task> hook, string databaseConnectionString, string databaseName, SqlDataPackResult result, ILogger logger, CancellationToken cancellationToken) {
        await using var connection = new SqlConnection(databaseConnectionString);

        try {
            await connection.OpenAsync(cancellationToken);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidOperationException($"The data imported successfully into \"{databaseName}\", but the connection the AfterImport hook needed could not be opened, so the hook did not run and changed nothing.", ex);
        }

        var context = new SqlDataPackAfterImportContext(connection, databaseName, result, logger);

        try {
            await hook(context, cancellationToken);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidOperationException($"The data imported successfully, but the AfterImport hook failed. \"{databaseName}\" holds the imported data and whatever the hook managed to change before it stopped.", ex);
        }
    }
}
