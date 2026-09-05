using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting.Tests;

/// <summary>
/// Builds manifests for tests that never touch a real pack. Only the table map and the dacpac flag
/// matter here; the rest are the values a real export would stamp.
/// </summary>
internal static class TestManifest {
    public static SqlDataPackManifest For(bool containsDacpac, params (string Schema, string Table, string SqliteTable)[] tables) {
        var manifests = tables
            .Select(t => new SqlDataPackTableManifest(t.Schema, t.Table, t.SqliteTable, 0, 0, 0, 1000, []))
            .ToList();

        return new SqlDataPackManifest(
            PackageFormatVersion: 1,
            ApplicationVersion: "1.0.0",
            ExportedAtUtc: DateTimeOffset.UnixEpoch,
            SourceSchemaHash: "test",
            Tables: manifests,
            ImportOrder: manifests.Select(t => t.FullName).ToList(),
            Exclusions: [],
            Warnings: [],
            ContainsDacpac: containsDacpac,
            DacpacSchemaScope: null,
            SourceEngineEdition: null);
    }
}
