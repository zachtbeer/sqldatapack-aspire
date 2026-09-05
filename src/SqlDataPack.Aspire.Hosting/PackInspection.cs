using SqlDataPack.Models;

namespace SqlDataPack.Aspire.Hosting;

/// <summary>What the commands need to know about a pack before touching a database.</summary>
internal sealed record PackInfo(bool ContainsDacpac, SqlDataPackManifest Manifest);

/// <summary>Thrown when a path is not a SqlDataPack file we can read.</summary>
internal sealed class PackUnreadableException : Exception {
    public PackUnreadableException(string message, Exception? innerException = null)
        : base(message, innerException) {
    }
}

/// <summary>Reads just enough of a pack to validate it. Exists so tests can fake it.</summary>
internal interface IPackInspector {
    Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken);
}

/// <summary>The real inspector, backed by SqlDataPackReader.</summary>
internal sealed class SqlDataPackInspector : IPackInspector {
    public async Task<PackInfo> InspectAsync(string path, CancellationToken cancellationToken) {
        if (!File.Exists(path)) {
            throw new PackUnreadableException($"The file '{path}' does not exist.");
        }

        try {
            var manifest = await new SqlDataPackReader().ReadManifestAsync(path, cancellationToken);
            return new PackInfo(manifest.ContainsDacpac, manifest);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            throw new PackUnreadableException($"The file '{path}' is not a readable SqlDataPack file.", ex);
        }
    }
}
