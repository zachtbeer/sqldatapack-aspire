using Aspire.Hosting;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class ImportValidationTests : IDisposable {
    // IPackInspector is faked in every test that uses this, so the file's contents never matter,
    // only that something real sits at the path (ImportValidation checks File.Exists before inspecting).
    private readonly string _existingPack = Path.Combine(Path.GetTempPath(), $"pack-that-exists-{Guid.NewGuid():N}.sqlite");

    public ImportValidationTests() {
        File.WriteAllText(_existingPack, string.Empty);
    }

    public void Dispose() {
        File.Delete(_existingPack);
    }

    private sealed class FakeInspector : IPackInspector {
        private readonly PackInfo? _info;
        private readonly Exception? _throws;

        private FakeInspector(PackInfo? info, Exception? throws) {
            _info = info;
            _throws = throws;
        }

        public static FakeInspector WithDacpac() => new(new PackInfo(ContainsDacpac: true), null);
        public static FakeInspector WithoutDacpac() => new(new PackInfo(ContainsDacpac: false), null);
        public static FakeInspector Unreadable() => new(null, new PackUnreadableException("not a readable SqlDataPack file"));

        public int Calls { get; private set; }

        public Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken) {
            Calls++;
            return _throws is not null ? Task.FromException<PackInfo>(_throws) : Task.FromResult(_info!);
        }
    }

    private static InteractionInput Boolean(string name, bool? value) => new() {
        Name = name,
        Label = name,
        InputType = InputType.Boolean,
        Value = value is null ? null : value.Value ? "true" : "false"
    };

    private static InteractionInput Text(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Text, Value = value };

    private static InteractionInputCollection Args(
        string? packPath = null, bool? importSchema = true, bool? reset = false, bool? confirmDestroy = false) =>
        new([
            Text(CommandArguments.PackPath, packPath),
            Boolean(CommandArguments.ImportSchema, importSchema),
            Boolean(CommandArguments.Reset, reset),
            Boolean(CommandArguments.ConfirmDestroy, confirmDestroy)
        ]);

    private static Task<ImportValidationResult> Validate(
        InteractionInputCollection args, IPackInspector inspector, SqlDataPackSource source = SqlDataPackSource.Path) =>
        ImportValidation.ValidateAsync(args, source, inspector, CancellationToken.None);

    [Fact]
    public async Task ValidPathPackWithDacpac_Succeeds() {
        var result = await Validate(Args(packPath: _existingPack), FakeInspector.WithDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request.ShouldNotBeNull();
        result.Request!.PackPath.ShouldBe(_existingPack);
        result.Request.ImportSchema.ShouldBeTrue();
        result.Request.Reset.ShouldBeFalse();
    }

    [Fact]
    public async Task NoPackSupplied_FailsOnPackPath() {
        var result = await Validate(Args(packPath: null), FakeInspector.WithDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackPath);
    }

    [Fact]
    public async Task NoPackSuppliedAndOnlyUploadDeclared_FailsOnPackFile() {
        var args = new InteractionInputCollection([
            new InteractionInput { Name = CommandArguments.PackFile, Label = "pack", InputType = InputType.File },
            Boolean(CommandArguments.ImportSchema, true),
            Boolean(CommandArguments.Reset, false),
            Boolean(CommandArguments.ConfirmDestroy, false)
        ]);

        var result = await Validate(args, FakeInspector.WithDacpac(), SqlDataPackSource.Upload);

        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackFile);
    }

    [Fact]
    public async Task UnreadablePack_FailsOnTheInputThatSuppliedIt() {
        var inspector = FakeInspector.Unreadable();

        var result = await Validate(Args(packPath: _existingPack), inspector);

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackPath);
        result.Failures[0].Message.ShouldContain("not a readable SqlDataPack file");
    }

    [Fact]
    public async Task ImportSchemaWithoutDacpac_FailsOnImportSchema() {
        var result = await Validate(Args(packPath: _existingPack, importSchema: true), FakeInspector.WithoutDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.ImportSchema);
        result.Failures[0].Message.ShouldContain("SchemaCaptureMode.Dacpac");
    }

    [Fact]
    public async Task DataOnlyImportFromPackWithoutDacpac_Succeeds() {
        var result = await Validate(Args(packPath: _existingPack, importSchema: false), FakeInspector.WithoutDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request!.ImportSchema.ShouldBeFalse();
    }

    [Fact]
    public async Task ResetWithDataOnly_FailsOnReset() {
        var result = await Validate(
            Args(packPath: _existingPack, importSchema: false, reset: true, confirmDestroy: true),
            FakeInspector.WithoutDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldContain(f => f.InputName == CommandArguments.Reset);
    }

    [Fact]
    public async Task ResetWithoutConfirmation_FailsOnConfirmDestroy() {
        var result = await Validate(
            Args(packPath: _existingPack, reset: true, confirmDestroy: false),
            FakeInspector.WithDacpac());

        result.Request.ShouldBeNull();
        result.Failures.ShouldContain(f => f.InputName == CommandArguments.ConfirmDestroy);
    }

    [Fact]
    public async Task ResetWithConfirmation_Succeeds() {
        var result = await Validate(
            Args(packPath: _existingPack, reset: true, confirmDestroy: true),
            FakeInspector.WithDacpac());

        result.Failures.ShouldBeEmpty();
        result.Request!.Reset.ShouldBeTrue();
    }

    [Fact]
    public async Task UnreadablePack_DoesNotAlsoReportSchemaProblem() {
        var result = await Validate(Args(packPath: _existingPack), FakeInspector.Unreadable());

        result.Failures.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MissingPack_NeverInspects() {
        var inspector = FakeInspector.WithDacpac();

        await Validate(Args(packPath: null), inspector);

        inspector.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task NonexistentTypedPath_FailsOnPackPathWithoutInspecting() {
        var missingPath = Path.Combine(Path.GetTempPath(), $"pack-that-does-not-exist-{Guid.NewGuid():N}.sqlite");
        var inspector = FakeInspector.WithDacpac();

        var result = await Validate(Args(packPath: missingPath), inspector);

        result.Request.ShouldBeNull();
        result.Failures.ShouldHaveSingleItem();
        result.Failures[0].InputName.ShouldBe(CommandArguments.PackPath);
        inspector.Calls.ShouldBe(0);
    }

    [Fact]
    public void ValidateReset_WithoutConfirmation_Fails() {
        var args = new InteractionInputCollection([Boolean(CommandArguments.ConfirmDestroy, false)]);

        var failures = ImportValidation.ValidateReset(args, "catalog");

        failures.ShouldHaveSingleItem();
        failures[0].InputName.ShouldBe(CommandArguments.ConfirmDestroy);
        failures[0].Message.ShouldContain("catalog");
    }

    [Fact]
    public void ValidateReset_WithConfirmation_Passes() {
        var args = new InteractionInputCollection([Boolean(CommandArguments.ConfirmDestroy, true)]);

        ImportValidation.ValidateReset(args, "catalog").ShouldBeEmpty();
    }
}
