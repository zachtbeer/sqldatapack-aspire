using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Shows an Aspire progress dialog for one phase. This is the only file that touches the
/// experimental progress API. Replace this class when Aspire stabilises it.
/// </summary>
#pragma warning disable ASPIREINTERACTION001
internal sealed class AspireProgressScope(IInteractionService interactions, ILogger logger) : IProgressScope {
    public async Task RunAsync(string message, Func<CancellationToken, Task> work, CancellationToken cancellationToken) {
        logger.LogInformation("{Phase}", message);

        if (!interactions.IsAvailable) {
            await work(cancellationToken);
            return;
        }

        Exception? failure = null;

        await interactions.PromptProgressAsync(message, new ProgressInteractionOptions {
            Work = async context => {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, context.CancellationToken);
                try {
                    await work(linked.Token);
                }
                catch (Exception ex) {
                    // Let the dialog close cleanly, then rethrow on the calling thread so the
                    // command result reports the real reason.
                    failure = ex;
                }
            }
        }, cancellationToken);

        if (failure is not null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
#pragma warning restore ASPIREINTERACTION001
