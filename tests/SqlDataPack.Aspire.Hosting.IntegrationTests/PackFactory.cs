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
