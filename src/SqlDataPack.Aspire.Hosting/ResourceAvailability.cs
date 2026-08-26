using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Whether the commands can run against a resource in a given state. Takes the two values rather
/// than a snapshot because a snapshot's health cannot be constructed outside Aspire's assembly.
/// </summary>
internal static class ResourceAvailability {
    public static ResourceCommandState StateFor(string? stateText, HealthStatus? health) {
        if (!string.Equals(stateText, KnownResourceStates.Running, StringComparison.Ordinal)) {
            return ResourceCommandState.Disabled;
        }

        // null means no health check is registered for this resource. Treating that as unhealthy
        // would disable the commands permanently.
        return health is null or HealthStatus.Healthy ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
    }
}
