using Aspire.Hosting.ApplicationModel;
using SqlDataPack.Models;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class SqlDataPackCommandOptionsTests {
    [Fact]
    public void Defaults_AcceptBothPackSourcesAndBothSurfaces() {
        var options = new SqlDataPackCommandOptions();

        options.PackSource.ShouldBe(SqlDataPackSource.UploadOrPath);
        options.Visibility.ShouldBe(ResourceCommandVisibility.UI | ResourceCommandVisibility.Api);
    }

    // The SqlDataPack library defaults this to false. Local development targets rarely match the
    // platform a pack was exported from, so the integration flips it.
    [Fact]
    public void AllowIncompatiblePlatform_DefaultsToTrue() {
        new SqlDataPackCommandOptions().AllowIncompatiblePlatform.ShouldBeTrue();
        DacpacDeploymentOptions.Default.AllowIncompatiblePlatform.ShouldBeFalse();
    }

    [Fact]
    public void BuildSchemaDeploymentOptions_CarriesAllowIncompatiblePlatform() {
        new SqlDataPackCommandOptions().BuildSchemaDeploymentOptions()
            .AllowIncompatiblePlatform.ShouldBeTrue();

        new SqlDataPackCommandOptions { AllowIncompatiblePlatform = false }.BuildSchemaDeploymentOptions()
            .AllowIncompatiblePlatform.ShouldBeFalse();
    }

    [Fact]
    public void BuildSchemaDeploymentOptions_LeavesEveryOtherLibraryDefaultAlone() {
        var built = new SqlDataPackCommandOptions().BuildSchemaDeploymentOptions();
        var library = DacpacDeploymentOptions.Default;

        built.BlockOnPossibleDataLoss.ShouldBe(library.BlockOnPossibleDataLoss);
        built.AllowObjectDrops.ShouldBe(library.AllowObjectDrops);
        built.DeployDatabaseOptions.ShouldBe(library.DeployDatabaseOptions);
        built.AdaptAzureSourceForOnPremTarget.ShouldBe(library.AdaptAzureSourceForOnPremTarget);
    }

    [Fact]
    public void ConfigureSchemaDeployment_ReachesTheOtherDacFxKnobs() {
        var options = new SqlDataPackCommandOptions {
            ConfigureSchemaDeployment = d => d.AdaptAzureSourceForOnPremTarget = false
        };

        options.BuildSchemaDeploymentOptions().AdaptAzureSourceForOnPremTarget.ShouldBeFalse();
    }

    // Documented precedence: the hook runs last, so it wins.
    [Fact]
    public void ConfigureSchemaDeployment_OverridesAllowIncompatiblePlatform() {
        var options = new SqlDataPackCommandOptions {
            AllowIncompatiblePlatform = true,
            ConfigureSchemaDeployment = d => d.AllowIncompatiblePlatform = false
        };

        options.BuildSchemaDeploymentOptions().AllowIncompatiblePlatform.ShouldBeFalse();
    }

    [Fact]
    public void BuildSchemaDeploymentOptions_ReturnsAFreshInstanceEachTime() {
        var options = new SqlDataPackCommandOptions();

        options.BuildSchemaDeploymentOptions().ShouldNotBeSameAs(options.BuildSchemaDeploymentOptions());
    }

    [Fact]
    public void UploadOrPath_IsUploadAndPathCombined() {
        SqlDataPackSource.UploadOrPath.ShouldBe(SqlDataPackSource.Upload | SqlDataPackSource.Path);
    }

    [Theory]
    [InlineData(SqlDataPackSource.Upload, true, false)]
    [InlineData(SqlDataPackSource.Path, false, true)]
    [InlineData(SqlDataPackSource.UploadOrPath, true, true)]
    public void PackSource_FlagsReadIndependently(SqlDataPackSource source, bool upload, bool path) {
        source.HasFlag(SqlDataPackSource.Upload).ShouldBe(upload);
        source.HasFlag(SqlDataPackSource.Path).ShouldBe(path);
    }
}
