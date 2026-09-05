using Aspire.Hosting.ApplicationModel;
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Where the import command is allowed to get a SqlDataPack file from.
/// </summary>
[Flags]
public enum SqlDataPackSource {
    /// <summary>A file uploaded through the dashboard. Dashboard only, and capped at the server upload limit.</summary>
    Upload = 1,

    /// <summary>A path on the machine running the AppHost. The only source a non-UI client can supply.</summary>
    Path = 2,

    /// <summary>Both. This is the default.</summary>
    UploadOrPath = Upload | Path
}

/// <summary>
/// Settings for the SqlDataPack resource commands.
/// </summary>
public sealed class SqlDataPackCommandOptions {
    /// <summary>Where a pack may come from. Defaults to <see cref="SqlDataPackSource.UploadOrPath"/>.</summary>
    public SqlDataPackSource PackSource { get; set; } = SqlDataPackSource.UploadOrPath;

    /// <summary>
    /// Which clients discover the commands. Defaults to both the dashboard and API clients such as MCP.
    /// Visibility controls discovery and display, not authorization.
    /// </summary>
    public ResourceCommandVisibility Visibility { get; set; } = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;

    /// <summary>
    /// Lets a schema import run when the pack's target platform does not match the target server,
    /// for example an Azure SQL export going into a local SQL Server 2022 container. Defaults to
    /// <see langword="true"/>, which is the opposite of the SqlDataPack library default.
    /// </summary>
    /// <remarks>
    /// The usual local development case is a pack taken from somewhere else and dropped into a
    /// container, where the platform stamp differs but nothing in the schema actually needs the
    /// source platform. Set this to <see langword="false"/> to get DacFx's fail-fast behaviour back.
    /// It does not rescue a schema that genuinely uses features the target server does not have;
    /// that still fails, just later and with a message about the specific feature.
    /// </remarks>
    public bool AllowIncompatiblePlatform { get; set; } = true;

    /// <summary>
    /// Optional hook to set any other <see cref="DacpacDeploymentOptions"/> property before a
    /// schema import. Runs after <see cref="AllowIncompatiblePlatform"/> is applied, so it can
    /// override that too. Only used when the import includes schema.
    /// </summary>
    public Action<DacpacDeploymentOptions>? ConfigureSchemaDeployment { get; set; }

    /// <summary>
    /// Optional hook to change the data in the pack before any of it reaches SQL Server. Runs
    /// after the arguments validate and before the database is reset, so a failure here leaves the
    /// target database untouched.
    /// </summary>
    /// <remarks>
    /// The connection is open on the pack file itself, so the hook rewrites that file on disk. If
    /// the pack came from the path field, that is the developer's own file. Write hooks that
    /// survive being run twice: <c>SET Email = 'dev@example.test'</c> is fine,
    /// <c>SET Email = Email || '.test'</c> grows a longer suffix on every import.
    /// </remarks>
    public Func<SqlDataPackBeforeImportContext, CancellationToken, Task>? BeforeImport { get; set; }

    /// <summary>
    /// Optional hook to change the data in the target database once the import has succeeded.
    /// Does not run when the import fails.
    /// </summary>
    public Func<SqlDataPackAfterImportContext, CancellationToken, Task>? AfterImport { get; set; }

    internal DacpacDeploymentOptions BuildSchemaDeploymentOptions() {
        var deployment = DacpacDeploymentOptions.Default;
        deployment.AllowIncompatiblePlatform = AllowIncompatiblePlatform;
        ConfigureSchemaDeployment?.Invoke(deployment);
        return deployment;
    }
}
