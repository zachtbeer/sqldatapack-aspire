using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// The Aspire side of the integration: argument declarations, validation adapters, and the two
/// command bodies. All of the real work lives in the operation classes.
/// </summary>
internal static class SqlDataPackCommands {
    public const string ImportCommandName = "sqldatapack-import";
    public const string ResetCommandName = "sqldatapack-reset";
    public const string FileFilter = ".sqlite,.sqldatapack,.db";
    public const string DocsUrl = "https://zachtbeer.github.io/sqldatapack/";

    // CommandOptions.Description becomes ResourceCommandAnnotation.DisplayDescription, which the
    // dashboard uses as a tooltip on the menu item, not as dialog body text. There is no dialog
    // message slot on a declarative command, so the V1 caveat rides on an input description, which
    // does support markdown. Task 12 step 4 checks where it actually lands.
    private const string ImportCaveat =
        """
        **V1 does not merge or remove existing data.** Without reset, schema + data import expects a
        blank database. Data-only import expects the required schema to already exist and its target
        tables to be empty.
        """;

    public static IReadOnlyList<InteractionInput> ImportArguments(SqlDataPackSource packSource) {
        var inputs = new List<InteractionInput>();

        var uploadDeclared = packSource.HasFlag(SqlDataPackSource.Upload);

        if (uploadDeclared) {
            inputs.Add(new InteractionInput {
                Name = CommandArguments.PackFile,
                Label = "SqlDataPack file",
                Description = $"Pick a SqlDataPack file to upload. Up to 100 MB. [What is a SqlDataPack file?]({DocsUrl})",
                EnableDescriptionMarkdown = true,
                InputType = InputType.File,
                FileFilter = FileFilter
            });
        }

        if (packSource.HasFlag(SqlDataPackSource.Path)) {
            inputs.Add(new InteractionInput {
                Name = CommandArguments.PackPath,
                // Only say "Or" when there is something to choose between.
                Label = uploadDeclared ? "Or, path to a SqlDataPack file" : "Path to a SqlDataPack file",
                Description = $"Full path to a SqlDataPack file already on this computer. Use this for files larger than 100 MB. [What is a SqlDataPack file?]({DocsUrl})",
                EnableDescriptionMarkdown = true,
                InputType = InputType.Text,
                Placeholder = "C:\\packs\\dev-slice.sqlite"
            });
        }

        inputs.Add(new InteractionInput {
            Name = CommandArguments.ImportSchema,
            Label = "Import schema",
            Description = $"Checked imports schema and data. Unchecked imports data only.\n\n{ImportCaveat}",
            EnableDescriptionMarkdown = true,
            InputType = InputType.Boolean,
            Value = "true"
        });

        inputs.Add(new InteractionInput {
            Name = CommandArguments.Reset,
            Label = "Reset database first",
            Description = "Drops and recreates the database before importing.",
            InputType = InputType.Boolean,
            Value = "false"
        });

        inputs.Add(new InteractionInput {
            Name = CommandArguments.ConfirmDestroy,
            Label = "Yes, delete all data and schema first",
            Description = "Required when Reset database first is checked.",
            InputType = InputType.Boolean,
            Value = "false"
        });

        return inputs;
    }

    public static IReadOnlyList<InteractionInput> ResetArguments(string databaseName) {
        return [
            new InteractionInput {
                Name = CommandArguments.ConfirmDestroy,
                Label = $"Yes, reset \"{databaseName}\"",
                Description = $"All data and schema in the local development database \"{databaseName}\" will be deleted.",
                InputType = InputType.Boolean,
                Value = "false"
            }
        ];
    }

    public static CommandOptions ImportOptionsFor(SqlDataPackCommandOptions options) {
        return new CommandOptions {
            Description = "Import a SqlDataPack file into this local development database.",
            Arguments = ImportArguments(options.PackSource),
            Visibility = options.Visibility,
            IconName = "DatabaseArrowDown",
            UpdateState = UpdateState,
            ValidateArguments = context => ValidateImportAsync(context, options.PackSource)
        };
    }

    public static CommandOptions ResetOptionsFor(SqlServerDatabaseResource resource, SqlDataPackCommandOptions options) {
        return new CommandOptions {
            Description = $"Drop and recreate \"{resource.DatabaseName}\" as an empty database.",
            Arguments = ResetArguments(resource.DatabaseName),
            Visibility = options.Visibility,
            IconName = "DatabaseWarning",
            UpdateState = UpdateState,
            ValidateArguments = context => {
                AddFailures(context, ImportValidation.ValidateReset(context.Inputs, resource.DatabaseName));
                return Task.CompletedTask;
            }
        };
    }

    public static ResourceCommandState UpdateState(UpdateCommandStateContext context) {
        return ResourceAvailability.StateFor(context.ResourceSnapshot.State?.Text, context.ResourceSnapshot.HealthStatus);
    }

    private static async Task ValidateImportAsync(InputsDialogValidationContext context, SqlDataPackSource packSource) {
        var result = await ImportValidation.ValidateAsync(context.Inputs, packSource, new SqlDataPackInspector(), context.CancellationToken);

        AddFailures(context, result.Failures);
    }

    private static void AddFailures(InputsDialogValidationContext context, IReadOnlyList<ValidationFailure> failures) {
        foreach (var failure in failures) {
            context.AddValidationError(failure.InputName, failure.Message);
        }
    }

    // progressFactory and inspectorFactory default to the real Aspire-backed implementations, so
    // WithSqlDataPack's registration path is unchanged. Tests pass fakes to drive the handlers
    // without touching a real dashboard or a real SqlDataPack file.
    public static Func<ExecuteCommandContext, Task<ExecuteCommandResult>> ImportHandler(SqlServerDatabaseResource resource, SqlDataPackCommandOptions options, Func<ExecuteCommandContext, IProgressScope>? progressFactory = null, Func<IPackInspector>? inspectorFactory = null) {
        var resolveProgress = progressFactory ?? CreateProgress;
        var resolveInspector = inspectorFactory ?? (() => new SqlDataPackInspector());

        return async context => {
            var progress = resolveProgress(context);

            try {
                var validation = await ImportValidation.ValidateAsync(context.Arguments, options.PackSource, resolveInspector(), context.CancellationToken);

                if (validation.Request is null) {
                    return Failure(string.Join(" ", validation.Failures.Select(f => f.Message)));
                }

                var request = validation.Request;

                await progress.RunAsync("Validating SqlDataPack", _ => Task.CompletedTask, context.CancellationToken);

                // Ahead of the reset on purpose: a hook that throws should not leave the developer
                // with a database that has already been dropped.
                if (options.BeforeImport is not null) {
                    await progress.RunAsync("Running pre-import changes", ct => ImportHooks.RunBeforeAsync(options.BeforeImport, request, validation.Manifest!, context.Logger, ct), context.CancellationToken);
                }

                if (request.Reset) {
                    await RunResetAsync(resource, progress, context.CancellationToken);
                }

                var databaseConnectionString = await RequireConnectionStringAsync(resource, context.CancellationToken);
                var phase = request.ImportSchema ? "Applying schema and importing data" : "Importing data";

                var result = await progress.RunAsync(phase, ct => SqlDataPackImportOperation.ImportAsync(request, databaseConnectionString, options.BuildSchemaDeploymentOptions(), context.Logger, ct), context.CancellationToken);

                if (options.AfterImport is not null) {
                    try {
                        await progress.RunAsync("Running post-import changes", ct => ImportHooks.RunAfterAsync(options.AfterImport, databaseConnectionString, resource.DatabaseName, result, context.Logger, ct), context.CancellationToken);
                    }
                    catch (OperationCanceledException) {
                        // The rows are already committed. A bare "canceled" sends the developer into
                        // a second import, which V1 cannot merge into a database that is no longer
                        // empty.
                        return new ExecuteCommandResult {
                            Success = false,
                            Canceled = true,
                            Message = $"The post-import changes were canceled. The import itself finished and \"{resource.DatabaseName}\" holds the imported data."
                        };
                    }
                }

                return new ExecuteCommandResult { Success = true, Message = ImportSuccessMessage(result) };
            }
            catch (OperationCanceledException) {
                return new ExecuteCommandResult { Success = false, Canceled = true };
            }
            catch (Exception ex) {
                context.Logger.LogError(ex, "Importing a SqlDataPack file into {Database} failed.", resource.DatabaseName);
                return Failure(Describe(ex));
            }
        };
    }

    public static Func<ExecuteCommandContext, Task<ExecuteCommandResult>> ResetHandler(SqlServerDatabaseResource resource, Func<ExecuteCommandContext, IProgressScope>? progressFactory = null) {
        var resolveProgress = progressFactory ?? CreateProgress;

        return async context => {
            var progress = resolveProgress(context);

            try {
                var failures = ImportValidation.ValidateReset(context.Arguments, resource.DatabaseName);
                if (failures.Count > 0) {
                    return Failure(failures[0].Message);
                }

                await RunResetAsync(resource, progress, context.CancellationToken);

                return new ExecuteCommandResult { Success = true, Message = "Database reset successfully." };
            }
            catch (OperationCanceledException) {
                return new ExecuteCommandResult { Success = false, Canceled = true };
            }
            catch (Exception ex) {
                context.Logger.LogError(ex, "Resetting {Database} failed.", resource.DatabaseName);
                return Failure(Describe(ex));
            }
        };
    }

    // Warnings already reach the Console tab through the logger, so they are not repeated here.
    private static string ImportSuccessMessage(SqlDataPackResult result) {
        return $"SqlDataPack imported successfully. {result.TableCount} tables, {result.RowCount:N0} rows.";
    }

    private static IProgressScope CreateProgress(ExecuteCommandContext context) {
        var interactions = context.Services.GetService<IInteractionService>();
        return interactions is null ? new LoggingProgressScope(context.Logger) : new AspireProgressScope(interactions, context.Logger);
    }

    // Shared by ImportHandler's reset-first branch and ResetHandler itself, so the two commands
    // cannot drift on the phase message, the connection string source, or the ordering.
    private static async Task RunResetAsync(SqlServerDatabaseResource resource, IProgressScope progress, CancellationToken cancellationToken) {
        var serverConnectionString = await RequireConnectionStringAsync(resource.Parent, cancellationToken);
        await progress.RunAsync("Resetting database", ct => SqlServerDatabaseResetter.ResetAsync(serverConnectionString, resource.DatabaseName, ct), cancellationToken);
    }

    private static async Task<string> RequireConnectionStringAsync(IResourceWithConnectionString resource, CancellationToken cancellationToken) {
        var connectionString = await resource.GetConnectionStringAsync(cancellationToken);
        return connectionString ?? throw new InvalidOperationException($"Resource '{resource.Name}' has no connection string yet.");
    }

    // The dashboard shows Message and nothing else, and the top of a DacFx failure chain is
    // usually "deployment plan generation failed" with the actual reason two levels down.
    public static string Describe(Exception exception) {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException) {
            var message = current.Message?.Trim();
            if (!string.IsNullOrEmpty(message) && !messages.Contains(message, StringComparer.Ordinal)) {
                messages.Add(message);
            }
        }

        return string.Join(" ", messages);
    }

    private static ExecuteCommandResult Failure(string message) {
        return new ExecuteCommandResult { Success = false, Message = message };
    }
}
