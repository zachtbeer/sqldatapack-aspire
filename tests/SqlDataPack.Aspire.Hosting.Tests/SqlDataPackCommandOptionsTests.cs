using Aspire.Hosting.ApplicationModel;
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
