using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class WithSqlDataPackTests {
    private static IDistributedApplicationBuilder RunModeBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });

    private static IDistributedApplicationBuilder PublishModeBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions {
            Args = ["--operation", "publish"],
            DisableDashboard = true
        });

    private static IReadOnlyList<ResourceCommandAnnotation> CommandsOn(IResourceBuilder<SqlServerDatabaseResource> db) =>
        db.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();

    [Fact]
    public void RunMode_RegistersBothCommands() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var names = CommandsOn(db).Select(c => c.Name).ToList();

        names.ShouldContain("sqldatapack-import");
        names.ShouldContain("sqldatapack-reset");
    }

    [Fact]
    public void RunMode_UsesTheSpecifiedDisplayNames() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var commands = CommandsOn(db);

        commands.Single(c => c.Name == "sqldatapack-import").DisplayName.ShouldBe("Import SqlDataPack");
        commands.Single(c => c.Name == "sqldatapack-reset").DisplayName.ShouldBe("Reset Database");
    }

    [Fact]
    public void PublishMode_RegistersNothing() {
        var db = PublishModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        CommandsOn(db).ShouldBeEmpty();
    }

    [Fact]
    public void DefaultVisibility_IncludesApiSoMcpClientsCanDiscoverIt() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        foreach (var command in CommandsOn(db)) {
            command.Visibility.HasFlag(ResourceCommandVisibility.Api).ShouldBeTrue();
            command.Visibility.HasFlag(ResourceCommandVisibility.UI).ShouldBeTrue();
        }
    }

    [Fact]
    public void Visibility_IsConfigurable() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => o.Visibility = ResourceCommandVisibility.Api);

        CommandsOn(db).ShouldAllBe(c => c.Visibility == ResourceCommandVisibility.Api);
    }

    [Fact]
    public void DefaultPackSource_DeclaresBothInputs() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldContain(CommandArguments.PackFile);
        names.ShouldContain(CommandArguments.PackPath);
    }

    [Fact]
    public void PathOnly_OmitsTheUploadInput() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => o.PackSource = SqlDataPackSource.Path);

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldNotContain(CommandArguments.PackFile);
        names.ShouldContain(CommandArguments.PackPath);
    }

    [Fact]
    public void UploadOnly_OmitsThePathInput() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog")
            .WithSqlDataPack(o => {
                o.PackSource = SqlDataPackSource.Upload;
                o.Visibility = ResourceCommandVisibility.UI;
            });

        var names = ImportArguments(db).Select(a => a.Name).ToList();

        names.ShouldContain(CommandArguments.PackFile);
        names.ShouldNotContain(CommandArguments.PackPath);
    }

    [Fact]
    public void NoPackSourceFlagsSet_Throws() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog");

        var exception = Should.Throw<ArgumentException>(() => db.WithSqlDataPack(o => o.PackSource = default));

        exception.Message.ShouldContain("PackSource");
    }

    [Fact]
    public void UploadOnlyWithApiVisibility_ThrowsBecauseAgentsCannotUpload() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog");

        var exception = Should.Throw<ArgumentException>(() => db.WithSqlDataPack(o => {
            o.PackSource = SqlDataPackSource.Upload;
            o.Visibility = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;
        }));

        exception.Message.ShouldContain("cannot supply an uploaded file");
    }

    [Fact]
    public void UploadInput_FiltersToPackExtensions() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var file = ImportArguments(db).Single(a => a.Name == CommandArguments.PackFile);

        file.InputType.ShouldBe(InputType.File);
        file.FileFilter.ShouldBe(".sqlite,.sqldatapack,.db");
    }

    [Fact]
    public void ImportSchema_DefaultsToChecked() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var input = ImportArguments(db).Single(a => a.Name == CommandArguments.ImportSchema);

        input.InputType.ShouldBe(InputType.Boolean);
        input.Value.ShouldBe("true");
    }

    [Theory]
    [InlineData(CommandArguments.Reset)]
    [InlineData(CommandArguments.ConfirmDestroy)]
    public void DestructiveInputs_DefaultToUnchecked(string name) {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var input = ImportArguments(db).Single(a => a.Name == name);

        input.InputType.ShouldBe(InputType.Boolean);
        input.Value.ShouldBe("false");
    }

    [Fact]
    public void ResetCommand_TakesOnlyAConfirmation() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        var reset = CommandsOn(db).Single(c => c.Name == "sqldatapack-reset");

        reset.Arguments.ShouldNotBeNull();
        reset.Arguments!.Select(a => a.Name).ShouldBe([CommandArguments.ConfirmDestroy]);
    }

    [Fact]
    public void BothCommands_AreGatedOnResourceState() {
        var db = RunModeBuilder().AddSqlServer("sql").AddDatabase("catalog").WithSqlDataPack();

        CommandsOn(db).ShouldAllBe(c => c.UpdateState != null);
    }

    [Fact]
    public void NullBuilder_Throws() {
        IResourceBuilder<SqlServerDatabaseResource> db = null!;

        Should.Throw<ArgumentNullException>(() => db.WithSqlDataPack());
    }

    private static IReadOnlyList<InteractionInput> ImportArguments(IResourceBuilder<SqlServerDatabaseResource> db) =>
        CommandsOn(db).Single(c => c.Name == "sqldatapack-import").Arguments ?? [];
}
