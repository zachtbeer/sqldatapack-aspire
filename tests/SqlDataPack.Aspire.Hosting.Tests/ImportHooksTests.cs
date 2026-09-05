using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlDataPack.Models;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

/// <summary>
/// Covers the pre-import hook against a real SQLite file. The post-import hook needs a real SQL
/// Server and lives in the Docker integration suite instead.
/// </summary>
public sealed class ImportHooksTests : IDisposable {
    private readonly string _directory = Directory.CreateTempSubdirectory("sqldatapack-hooks").FullName;

    public void Dispose() {
        if (Directory.Exists(_directory)) {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Pooling stays off here too, so a helper connection can never be the thing holding the file.
    private static string ConnectionStringFor(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private string CreatePack(string name) {
        var path = Path.Combine(_directory, $"{name}.sqlite");

        using var connection = new SqliteConnection(ConnectionStringFor(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Widgets(Id integer primary key, Name text); INSERT INTO Widgets VALUES (1, 'first');";
        command.ExecuteNonQuery();

        return path;
    }

    private string ReadName(string path) {
        using var connection = new SqliteConnection(ConnectionStringFor(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Widgets WHERE Id = 1;";
        return (string)command.ExecuteScalar()!;
    }

    private static ImportRequest RequestFor(string packPath, bool importSchema = true) =>
        new(packPath, importSchema, Reset: false);

    private static readonly SqlDataPackManifest Manifest = TestManifest.For(true, ("dbo", "Widgets", "t_dbo_Widgets"));

    [Fact]
    public async Task RunBeforeAsync_HookWritesToThePack_ChangesSurvive() {
        var pack = CreatePack("writes");

        await ImportHooks.RunBeforeAsync(async (context, ct) => {
            await using var command = context.Connection.CreateCommand();
            command.CommandText = "UPDATE Widgets SET Name = 'masked' WHERE Id = 1;";
            await command.ExecuteNonQueryAsync(ct);
        }, RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None);

        ReadName(pack).ShouldBe("masked");
    }

    [Fact]
    public async Task RunBeforeAsync_HandsTheHookAnOpenConnectionAndTheRequestDetails() {
        var pack = CreatePack("context");
        var ran = false;

        await ImportHooks.RunBeforeAsync((context, _) => {
            ran = true;
            context.PackPath.ShouldBe(pack);
            context.ImportSchema.ShouldBeFalse();
            context.Connection.State.ShouldBe(System.Data.ConnectionState.Open);
            return Task.CompletedTask;
        }, RequestFor(pack, importSchema: false), Manifest, NullLogger.Instance, CancellationToken.None);

        ran.ShouldBeTrue();
    }

    // Deleting an open SQLite file fails on Windows, so this is the check that the connection was
    // really closed and not parked in a pool where the importer would trip over it.
    [Fact]
    public async Task RunBeforeAsync_ReleasesTheFileWhenItReturns() {
        var pack = CreatePack("unlocked");

        await ImportHooks.RunBeforeAsync((_, _) => Task.CompletedTask, RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None);

        Should.NotThrow(() => File.Delete(pack));
    }

    [Fact]
    public async Task RunBeforeAsync_MissingPack_FailsInsteadOfCreatingAnEmptyOne() {
        var pack = Path.Combine(_directory, "not-here.sqlite");

        await Should.ThrowAsync<InvalidOperationException>(() => ImportHooks.RunBeforeAsync(
            (_, _) => Task.CompletedTask, RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None));

        File.Exists(pack).ShouldBeFalse();
    }

    [Fact]
    public async Task RunBeforeAsync_HookThrows_SaysNothingWasImportedAndKeepsTheReason() {
        var pack = CreatePack("throws");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ImportHooks.RunBeforeAsync(
            (_, _) => throw new InvalidOperationException("no such column: Emial"),
            RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None));

        var message = SqlDataPackCommands.Describe(exception);
        message.ShouldContain("BeforeImport");
        message.ShouldContain("nothing was imported");
        message.ShouldContain("no such column: Emial");
    }

    // A pack does not name its tables after the source, so a hook that writes "UPDATE Widgets"
    // hits "no such table". This lookup is the only way to get the real name.
    [Fact]
    public async Task SqliteTableFor_MapsASourceTableOntoItsNameInsideThePack() {
        var pack = CreatePack("lookup");

        await ImportHooks.RunBeforeAsync((context, _) => {
            context.SqliteTableFor("dbo", "Widgets").ShouldBe("t_dbo_Widgets");
            context.SqliteTableFor("DBO", "widgets").ShouldBe("t_dbo_Widgets");
            return Task.CompletedTask;
        }, RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task SqliteTableFor_TableNotInThePack_SaysWhatThePackDoesCarry() {
        var pack = CreatePack("lookup-miss");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ImportHooks.RunBeforeAsync(
            (context, _) => {
                context.SqliteTableFor("dbo", "Customer");
                return Task.CompletedTask;
            }, RequestFor(pack), Manifest, NullLogger.Instance, CancellationToken.None));

        var message = SqlDataPackCommands.Describe(exception);
        message.ShouldContain("does not carry the table dbo.Customer");
        message.ShouldContain("dbo.Widgets");
    }

    [Fact]
    public async Task RunBeforeAsync_HookCancels_PropagatesCancellationUnwrapped() {
        var pack = CreatePack("cancels");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => ImportHooks.RunBeforeAsync(
            (_, ct) => Task.FromCanceled(ct), RequestFor(pack), Manifest, NullLogger.Instance, source.Token));
    }
}
