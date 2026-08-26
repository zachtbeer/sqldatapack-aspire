using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class PackInspectionTests {
    [Fact]
    public async Task InspectAsync_MissingFile_ThrowsPackUnreadable() {
        var inspector = new SqlDataPackInspector();
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sqlite");

        var exception = await Should.ThrowAsync<PackUnreadableException>(() => inspector.InspectAsync(path, CancellationToken.None));

        exception.Message.ShouldContain("does not exist");
    }

    [Fact]
    public async Task InspectAsync_FileThatIsNotAPack_ThrowsPackUnreadable() {
        var inspector = new SqlDataPackInspector();
        var path = Path.Combine(Path.GetTempPath(), $"junk-{Guid.NewGuid():N}.sqlite");
        await File.WriteAllTextAsync(path, "this is not a SQLite database at all");

        try {
            var exception = await Should.ThrowAsync<PackUnreadableException>(() => inspector.InspectAsync(path, CancellationToken.None));

            exception.Message.ShouldContain("not a readable SqlDataPack file");
        }
        finally {
            File.Delete(path);
        }
    }
}
