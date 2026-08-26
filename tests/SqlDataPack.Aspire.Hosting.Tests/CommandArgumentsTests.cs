using Aspire.Hosting;
using Shouldly;
using Xunit;

namespace SqlDataPack.Aspire.Hosting.Tests;

public sealed class CommandArgumentsTests {
    private static InteractionInputCollection Collection(params InteractionInput[] inputs) => new(inputs);

    private static InteractionInput Boolean(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Boolean, Value = value };

    private static InteractionInput Text(string name, string? value) =>
        new() { Name = name, Label = name, InputType = InputType.Text, Value = value };

    [Fact]
    public void ReadBoolean_UnsetInput_ReturnsFalseInsteadOfThrowing() {
        var args = Collection(Boolean(CommandArguments.Reset, null));

        CommandArguments.ReadBoolean(args, CommandArguments.Reset).ShouldBeFalse();
    }

    [Fact]
    public void ReadBoolean_MissingInput_ReturnsFalse() {
        var args = Collection(Boolean(CommandArguments.Reset, "true"));

        CommandArguments.ReadBoolean(args, CommandArguments.ConfirmDestroy).ShouldBeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void ReadBoolean_ParsesValue(string value, bool expected) {
        var args = Collection(Boolean(CommandArguments.ImportSchema, value));

        CommandArguments.ReadBoolean(args, CommandArguments.ImportSchema).ShouldBe(expected);
    }

    [Fact]
    public void ReadText_MissingInput_ReturnsNull() {
        var args = Collection(Text(CommandArguments.PackPath, "c:/packs/a.sqlite"));

        CommandArguments.ReadText(args, "nope").ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadText_BlankValue_ReturnsNull(string? value) {
        var args = Collection(Text(CommandArguments.PackPath, value));

        CommandArguments.ReadText(args, CommandArguments.PackPath).ShouldBeNull();
    }

    [Fact]
    public void ReadText_TrimsSurroundingWhitespace() {
        var args = Collection(Text(CommandArguments.PackPath, "  c:/packs/a.sqlite  "));

        CommandArguments.ReadText(args, CommandArguments.PackPath).ShouldBe("c:/packs/a.sqlite");
    }

    [Fact]
    public void ReadUploadedFilePath_NoFileSelected_ReturnsNull() {
        var args = Collection(new InteractionInput {
            Name = CommandArguments.PackFile,
            Label = "pack",
            InputType = InputType.File
        });

        CommandArguments.ReadUploadedFilePath(args, CommandArguments.PackFile).ShouldBeNull();
    }
}
