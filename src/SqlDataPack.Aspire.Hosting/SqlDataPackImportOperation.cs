using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Runs the actual import. Holds no Aspire types so it can be driven straight from a test.
/// </summary>
internal static class SqlDataPackImportOperation {
    public static Task<SqlDataPackResult> ImportAsync(ImportRequest request, string databaseConnectionString, DacpacDeploymentOptions deploymentOptions, ILogger logger, CancellationToken cancellationToken) {
        var options = ImportOptions.Default;
        options.Logger = logger;
        options.SchemaDeploymentMode = request.ImportSchema ? SchemaDeploymentMode.DeployDacpac : SchemaDeploymentMode.None;
        options.DacpacDeploymentOptions = deploymentOptions;
        return new SqlDataPackImporter().ImportAsync(request.PackPath, databaseConnectionString, options, cancellationToken);
    }
}
