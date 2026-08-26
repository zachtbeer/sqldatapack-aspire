using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

using global::SqlDataPack.Aspire.Hosting;

/// <summary>
/// Adds SqlDataPack development commands to an Aspire SQL Server database resource.
/// </summary>
public static class SqlDataPackBuilderExtensions {
    /// <summary>
    /// Adds the <c>Import SqlDataPack</c> and <c>Reset Database</c> commands to a local
    /// development database. Does nothing outside run mode.
    /// </summary>
    /// <param name="builder">The database resource builder.</param>
    /// <param name="configure">Optional settings for pack source and client visibility.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IResourceBuilder<SqlServerDatabaseResource> WithSqlDataPack(this IResourceBuilder<SqlServerDatabaseResource> builder, Action<SqlDataPackCommandOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlDataPackCommandOptions();
        configure?.Invoke(options);

        if (options.PackSource == 0) {
            throw new ArgumentException(
                "PackSource must declare at least one pack input (Upload, Path, or both).",
                nameof(configure));
        }

        if (options.PackSource == SqlDataPackSource.Upload
            && options.Visibility.HasFlag(ResourceCommandVisibility.Api)) {
            throw new ArgumentException("PackSource.Upload cannot be combined with Api visibility, because an API client such as MCP cannot supply an uploaded file. Add SqlDataPackSource.Path, or remove ResourceCommandVisibility.Api.", nameof(configure));
        }

        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode) {
            return builder;
        }

        var resource = builder.Resource;

        return builder
            .WithCommand(
                SqlDataPackCommands.ImportCommandName,
                "Import SqlDataPack",
                SqlDataPackCommands.ImportHandler(resource, options),
                SqlDataPackCommands.ImportOptionsFor(options))
            .WithCommand(
                SqlDataPackCommands.ResetCommandName,
                "Reset Database",
                SqlDataPackCommands.ResetHandler(resource),
                SqlDataPackCommands.ResetOptionsFor(resource, options));
    }
}
