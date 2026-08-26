using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ResourceAvailabilityTests {
    [Fact]
    public void RunningAndHealthy_IsEnabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Enabled);

    [Fact]
    public void RunningWithNoHealthChecksRegistered_IsEnabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, null)
            .ShouldBe(ResourceCommandState.Enabled);

    [Fact]
    public void RunningButUnhealthy_IsDisabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Unhealthy)
            .ShouldBe(ResourceCommandState.Disabled);

    [Fact]
    public void RunningButDegraded_IsDisabled() =>
        ResourceAvailability.StateFor(KnownResourceStates.Running, HealthStatus.Degraded)
            .ShouldBe(ResourceCommandState.Disabled);

    // KnownResourceStates members are static readonly, not const, so they cannot be InlineData
    // attribute arguments (CS0182). MemberData works with the real symbols instead.
    public static TheoryData<string> NotRunningStates => new() {
        KnownResourceStates.Starting,
        KnownResourceStates.Exited,
        KnownResourceStates.FailedToStart,
        KnownResourceStates.Finished,
        KnownResourceStates.Waiting,
    };

    [Theory]
    [MemberData(nameof(NotRunningStates))]
    public void NotRunning_IsDisabled(string state) =>
        ResourceAvailability.StateFor(state, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Disabled);

    [Fact]
    public void UnknownState_IsDisabled() =>
        ResourceAvailability.StateFor(null, HealthStatus.Healthy)
            .ShouldBe(ResourceCommandState.Disabled);
}
