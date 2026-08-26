using Aspire.Hosting;

namespace SqlDataPack.Aspire.Hosting;

internal sealed record ValidationFailure(string InputName, string Message);

internal sealed record ImportRequest(string PackPath, bool ImportSchema, bool Reset);

internal sealed record ImportValidationResult(ImportRequest? Request, IReadOnlyList<ValidationFailure> Failures);

/// <summary>
/// Every rule from the design's validation table. Runs before the command body, so nothing
/// destructive has happened when a failure is returned.
/// </summary>
internal static class ImportValidation {
    public static async Task<ImportValidationResult> ValidateAsync(InteractionInputCollection arguments, SqlDataPackSource packSource, IPackInspector inspector, CancellationToken cancellationToken) {
        var failures = new List<ValidationFailure>();

        var uploadDeclared = packSource.HasFlag(SqlDataPackSource.Upload);
        var pathDeclared = packSource.HasFlag(SqlDataPackSource.Path);

        var uploadedPath = uploadDeclared ? CommandArguments.ReadUploadedFilePath(arguments, CommandArguments.PackFile) : null;
        var typedPath = pathDeclared ? CommandArguments.ReadText(arguments, CommandArguments.PackPath) : null;

        // Which input a "you must supply a pack" error belongs to.
        var missingPackInput = pathDeclared ? CommandArguments.PackPath : CommandArguments.PackFile;

        string? packPath = null;
        var packInput = missingPackInput;

        if (uploadedPath is not null && typedPath is not null) {
            failures.Add(new ValidationFailure(CommandArguments.PackPath, "Supply either an uploaded file or a path, not both."));
        }
        else if (uploadedPath is null && typedPath is null) {
            failures.Add(new ValidationFailure(missingPackInput, pathDeclared && uploadDeclared ? "Upload a SqlDataPack file, or give the path to one on this computer." : uploadDeclared ? "Upload a SqlDataPack file." : "Give the path to a SqlDataPack file on this computer."));
        }
        else if (uploadedPath is not null) {
            packPath = uploadedPath;
            packInput = CommandArguments.PackFile;
        }
        else {
            packPath = typedPath;
            packInput = CommandArguments.PackPath;

            if (!File.Exists(packPath)) {
                failures.Add(new ValidationFailure(CommandArguments.PackPath, $"No file exists at '{packPath}' on the machine running the AppHost."));
                packPath = null;
            }
        }

        var importSchema = CommandArguments.ReadBoolean(arguments, CommandArguments.ImportSchema);
        var reset = CommandArguments.ReadBoolean(arguments, CommandArguments.Reset);
        var confirmDestroy = CommandArguments.ReadBoolean(arguments, CommandArguments.ConfirmDestroy);

        if (reset && !importSchema) {
            failures.Add(new ValidationFailure(CommandArguments.Reset, "Reset creates a completely empty database, so a data-only import would have no schema to import into. Turn on Import schema, or turn off Reset."));
        }

        if (reset && !confirmDestroy) {
            failures.Add(new ValidationFailure(CommandArguments.ConfirmDestroy, "Confirm that the database may be dropped and recreated before importing."));
        }

        if (packPath is not null) {
            PackInfo? info = null;
            try {
                info = await inspector.InspectAsync(packPath, cancellationToken);
            }
            catch (PackUnreadableException ex) {
                failures.Add(new ValidationFailure(packInput, ex.Message));
            }

            if (info is not null && importSchema && !info.ContainsDacpac) {
                failures.Add(new ValidationFailure(CommandArguments.ImportSchema, "This pack carries no schema. Export it with SchemaCaptureMode.Dacpac, or turn off Import schema to import data only."));
            }
        }

        return failures.Count > 0 ? new ImportValidationResult(null, failures) : new ImportValidationResult(new ImportRequest(packPath!, importSchema, reset), failures);
    }

    public static IReadOnlyList<ValidationFailure> ValidateReset(InteractionInputCollection arguments, string databaseName) {
        return CommandArguments.ReadBoolean(arguments, CommandArguments.ConfirmDestroy) ? [] : [new ValidationFailure(CommandArguments.ConfirmDestroy, $"Confirm that \"{databaseName}\" may be dropped and recreated.")];
    }
}
