using Aspire.Hosting.ApplicationModel;

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
}
