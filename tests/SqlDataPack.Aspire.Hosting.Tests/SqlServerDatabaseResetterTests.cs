using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class SqlServerDatabaseResetterTests {
    [Fact]
    public void BuildResetScript_DropsThenCreates() {
        var script = SqlServerDatabaseResetter.BuildResetScript("catalog");

        script.ShouldContain("ALTER DATABASE [catalog] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        script.ShouldContain("DROP DATABASE [catalog]");
        script.ShouldContain("CREATE DATABASE [catalog]");
        script.IndexOf("DROP DATABASE", StringComparison.Ordinal)
            .ShouldBeLessThan(script.IndexOf("CREATE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildResetScript_EscapesClosingBracketsInTheName() {
        var script = SqlServerDatabaseResetter.BuildResetScript("we][ird");

        script.ShouldContain("[we]][ird]");
    }

    [Fact]
    public void BuildResetScript_ToleratesAMissingDatabase() {
        var script = SqlServerDatabaseResetter.BuildResetScript("catalog");

        script.ShouldContain("DB_ID");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildResetScript_RejectsABlankName(string? name) {
        Should.Throw<ArgumentException>(() => SqlServerDatabaseResetter.BuildResetScript(name!));
    }
}
