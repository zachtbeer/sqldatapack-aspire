using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

/// <summary>
/// Covers the handler paths reachable without a real Aspire resource or a running container:
/// everything that returns before a connection string is resolved. Those are the safety-relevant
/// paths, since nothing destructive can have happened by then. The successful import/reset paths
/// need real Aspire resource plumbing and are exercised by the Docker integration suite instead.
/// </summary>
public sealed class SqlDataPackCommandsTests {
    private readonly string _existingPack = Path.Combine(Path.GetTempPath(), $"pack-that-exists-{Guid.NewGuid():N}.sqlite");

    private sealed class FakeProgressScope : IProgressScope {
        public List<string> Phases { get; } = [];

        public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
            Phases.Add(message);
            await work(cancellationToken);
        }
    }

    private sealed class FakeInspector : IPackInspector {
        public int Calls { get; private set; }

        public Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken) {
            Calls++;
            return Task.FromResult(new PackInfo(ContainsDacpac: true));
        }
    }

    private sealed class NullServiceProvider : IServiceProvider {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void Describe_FlattensTheInnerExceptionChain() {
        var exception = new InvalidOperationException(
            "Failed to deploy dacpac schema.",
            new Exception("Deployment plan generation failed.", new Exception("Target platform mismatch.")));

        var message = SqlDataPackCommands.Describe(exception);

        message.ShouldBe("Failed to deploy dacpac schema. Deployment plan generation failed. Target platform mismatch.");
    }

    [Fact]
    public void Describe_DropsRepeatedMessagesFromWrapperExceptions() {
        var exception = new InvalidOperationException("Same text.", new Exception("Same text."));

        SqlDataPackCommands.Describe(exception).ShouldBe("Same text.");
    }

    private static SqlServerDatabaseResource BuildResource() {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return builder.AddSqlServer("sql").AddDatabase("catalog").Resource;
    }

    private static InteractionInput Boolean(string name, bool value) => new() {
        Name = name,
        Label = name,
        InputType = InputType.Boolean,
        Value = value ? "true" : "false"
    };

    private static InteractionInput Text(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Text, Value = value };

    private static ExecuteCommandContext Context(InteractionInputCollection arguments) => new() {
        Services = new NullServiceProvider(),
        ResourceName = "catalog",
        CancellationToken = CancellationToken.None,
        Logger = NullLogger.Instance,
        Arguments = arguments
    };

    [Fact]
    public async Task ResetHandler_ConfirmDestroyAbsent_FailsWithoutRunningAPhase() {
        var progress = new FakeProgressScope();
        var handler = SqlDataPackCommands.ResetHandler(BuildResource(), _ => progress);
        var args = new InteractionInputCollection([]);

        var result = await handler(Context(args));

        result.Success.ShouldBeFalse();
        progress.Phases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResetHandler_ConfirmDestroyFalse_FailsWithoutRunningAPhase() {
        var progress = new FakeProgressScope();
        var handler = SqlDataPackCommands.ResetHandler(BuildResource(), _ => progress);
        var args = new InteractionInputCollection([Boolean(CommandArguments.ConfirmDestroy, false)]);

        var result = await handler(Context(args));

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("catalog");
        progress.Phases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ImportHandler_ResetTrueConfirmDestroyFalse_FailsWithoutRunningAPhase() {
        File.WriteAllText(_existingPack, string.Empty);
        try {
            var progress = new FakeProgressScope();
            var inspector = new FakeInspector();
            var options = new SqlDataPackCommandOptions { PackSource = SqlDataPackSource.Path };
            var handler = SqlDataPackCommands.ImportHandler(BuildResource(), options, _ => progress, () => inspector);
            var args = new InteractionInputCollection([
                Text(CommandArguments.PackPath, _existingPack),
                Boolean(CommandArguments.ImportSchema, true),
                Boolean(CommandArguments.Reset, true),
                Boolean(CommandArguments.ConfirmDestroy, false)
            ]);

            var result = await handler(Context(args));

            result.Success.ShouldBeFalse();
            progress.Phases.ShouldBeEmpty();
        }
        finally {
            File.Delete(_existingPack);
        }
    }

    [Fact]
    public async Task ImportHandler_NoPackSupplied_FailsWithoutRunningAPhase() {
        var progress = new FakeProgressScope();
        var inspector = new FakeInspector();
        var options = new SqlDataPackCommandOptions { PackSource = SqlDataPackSource.Path };
        var handler = SqlDataPackCommands.ImportHandler(BuildResource(), options, _ => progress, () => inspector);
        var args = new InteractionInputCollection([
            Text(CommandArguments.PackPath, null),
            Boolean(CommandArguments.ImportSchema, true),
            Boolean(CommandArguments.Reset, false),
            Boolean(CommandArguments.ConfirmDestroy, false)
        ]);

        var result = await handler(Context(args));

        result.Success.ShouldBeFalse();
        progress.Phases.ShouldBeEmpty();
        inspector.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ImportHandler_ImportSchemaFalseResetTrue_FailsWithoutRunningAPhase() {
        File.WriteAllText(_existingPack, string.Empty);
        try {
            var progress = new FakeProgressScope();
            var inspector = new FakeInspector();
            var options = new SqlDataPackCommandOptions { PackSource = SqlDataPackSource.Path };
            var handler = SqlDataPackCommands.ImportHandler(BuildResource(), options, _ => progress, () => inspector);
            var args = new InteractionInputCollection([
                Text(CommandArguments.PackPath, _existingPack),
                Boolean(CommandArguments.ImportSchema, false),
                Boolean(CommandArguments.Reset, true),
                Boolean(CommandArguments.ConfirmDestroy, true)
            ]);

            var result = await handler(Context(args));

            result.Success.ShouldBeFalse();
            progress.Phases.ShouldBeEmpty();
        }
        finally {
            File.Delete(_existingPack);
        }
    }
}
