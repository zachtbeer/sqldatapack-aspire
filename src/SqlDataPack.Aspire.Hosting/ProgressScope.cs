using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Runs one phase of work while showing the user something. Kept behind an interface because the
/// Aspire implementation depends on an experimental API.
/// </summary>
internal interface IProgressScope {
    Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>Used when interactions are unavailable. Logs the phase and runs the work.</summary>
internal sealed class LoggingProgressScope(ILogger logger) : IProgressScope {
    public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
        logger.LogInformation("{Phase}", message);
        await work(cancellationToken);
    }
}
