using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Runs one phase of work while showing the user something. Kept behind an interface because the
/// Aspire implementation depends on an experimental API.
/// </summary>
internal interface IProgressScope {
    Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken);

    /// <summary>
    /// Same thing for work that produces a value. An implementation that never invokes the work
    /// would otherwise hand back a default, which reads as a successful import of nothing.
    /// </summary>
    async Task<T> RunAsync<T>(string message, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) {
        var captured = default(T);
        var ran = false;

        await RunAsync(message, async ct => {
            captured = await work(ct);
            ran = true;
        }, cancellationToken);

        return ran
            ? captured!
            : throw new InvalidOperationException($"The \"{message}\" step did not run, so there is no result to report.");
    }
}

/// <summary>Used when interactions are unavailable. Logs the phase and runs the work.</summary>
internal sealed class LoggingProgressScope(ILogger logger) : IProgressScope {
    public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
        logger.LogInformation("{Phase}", message);
        await work(cancellationToken);
    }
}
