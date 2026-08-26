using Aspire.Hosting;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>
/// Argument names and safe readers. InteractionInputCollection.GetBoolean and GetString throw when
/// an input has no value, which is what an unchecked box and an untouched field both look like.
/// </summary>
internal static class CommandArguments {
    public const string PackFile = "packFile";
    public const string PackPath = "packPath";
    public const string ImportSchema = "importSchema";
    public const string Reset = "reset";
    public const string ConfirmDestroy = "confirmDestroy";

    public static bool ReadBoolean(InteractionInputCollection arguments, string name) {
        return arguments.TryGetByName(name, out var input) && bool.TryParse(input.Value, out var value) && value;
    }

    public static string? ReadText(InteractionInputCollection arguments, string name) {
        if (!arguments.TryGetByName(name, out var input)) {
            return null;
        }

        var value = input.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string? ReadUploadedFilePath(InteractionInputCollection arguments, string name) {
        if (!arguments.TryGetByName(name, out var input)) {
            return null;
        }

        var file = input.Files is { Count: > 0 } files ? files[0] : null;
        return string.IsNullOrWhiteSpace(file?.FilePath) ? null : file.FilePath;
    }
}
