using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ProgressScopeTests {
    [Fact]
    public async Task LoggingScope_RunsTheWork() {
        var scope = new LoggingProgressScope(NullLogger.Instance);
        var ran = false;

        await scope.RunAsync("Importing data", _ => {
            ran = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task LoggingScope_PropagatesFailure() {
        var scope = new LoggingProgressScope(NullLogger.Instance);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync("Importing data", _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        exception.Message.ShouldBe("boom");
    }

    [Fact]
    public async Task LoggingScope_PassesCancellationThrough() {
        var scope = new LoggingProgressScope(NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        CancellationToken seen = default;
        await scope.RunAsync("Importing data", ct => {
            seen = ct;
            return Task.CompletedTask;
        }, cts.Token);

        seen.IsCancellationRequested.ShouldBeTrue();
    }
}
