using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class ManjuuReleaseIndex
{
    private const int FilePackageType = 2;

    // The caller supplies a CDN client with automatic redirects disabled.
    internal static async Task<GameInstallPlan> LoadAsync(HttpClient cdn, string profile, JsonElement response,
        bool preload, IReadOnlySet<string> publisherHosts, ReadOnlyMemory<byte> formatKey, ReadOnlyMemory<byte> iv,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        JsonElement status = Property(response, "code");
        JsonElement release = Property(Property(response, "data"), preload ? "preload" : "released");
        if (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code != 0 ||
            !Version.TryParse(String(release, "versionCode"), out Version? version))
            throw new InvalidDataException("The publisher did not provide the requested game version.");

        JsonElement packages = Property(release, "gamePackageList");
        if (packages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The publisher's game release is missing its package list.");
        JsonElement index = default;
        foreach (JsonElement package in packages.EnumerateArray())
        {
            JsonElement type = Property(package, "packageType");
            if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out int number) && number == FilePackageType)
                index = package;
        }

        string? digest = String(index, "fileMd5");
        string? remotePath = String(index, "downloadUrl");
        if (digest is not { Length: 32 } || !digest.All(char.IsAsciiHexDigit) ||
            !TryReadSize(Property(index, "fileSize"), out long size) || size <= 0 ||
            remotePath is not { Length: > 0 } || remotePath.StartsWith("//", StringComparison.Ordinal) ||
            remotePath.TrimStart('/').Split('/').Any(segment => segment is "" or "." or ".." ||
                segment.Any(character => char.IsControl(character) || "\\:%?#".Contains(character))))
            throw new InvalidDataException("The publisher's per-file package index is invalid.");

        JsonElement roots = Property(release, "domainList");
        if (roots.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            roots = Property(index, "cdnBaseUrls");
        if (roots.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The publisher's game release is missing its CDN list.");

        var allowedRoots = new List<(Uri Root, Uri Index)>();
        var seenRoots = new HashSet<Uri>();
        foreach (JsonElement entry in roots.EnumerateArray())
        {
            string? cdnRoot = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (Uri.TryCreate(cdnRoot, UriKind.Absolute, out Uri? candidate) &&
                GameCdn.TryNormalizeRoot(candidate, out Uri? root) && root.IsDefaultPort && publisherHosts.Contains(root.IdnHost) &&
                GameCdn.TryResolvePath(root, remotePath.TrimStart('/'), out Uri? indexUri) && seenRoots.Add(root))
                allowedRoots.Add((root, indexUri));
        }

        var indexFile = new GameFile(size, digest, string.Empty, remotePath);
        string temporary = Path.GetTempFileName();
        try
        {
            Exception? failure = null;
            foreach (var (root, indexUri) in allowedRoots)
            {
                try
                {
                    await VerifiedGameFileDownloader.DownloadAsync(cdn, indexUri, temporary, indexFile, null, token)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (GameCdn.IsTransferFailure(error, token))
                {
                    failure = error;
                    using FileStream discarded = File.Open(temporary, FileMode.Truncate, FileAccess.Write);
                    continue;
                }

                using FileStream input = File.OpenRead(temporary);
                string identity = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                input.Position = 0;
                ManjuuGameIndex parsed = ManjuuGameIndex.ReadEncrypted(input, formatKey.Span, iv.Span);
                token.ThrowIfCancellationRequested();
                Uri[] orderedRoots = [root, .. allowedRoots.Select(source => source.Root).Where(other => other != root)];
                return new GameInstallPlan(profile, version, identity, orderedRoots, parsed.Files, parsed.Directories);
            }

            throw new InvalidDataException("The publisher's verified package index is unavailable from its configured CDNs.", failure);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static string? String(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool TryReadSize(JsonElement value, out long size)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out size);
        return long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null,
            NumberStyles.None, CultureInfo.InvariantCulture, out size);
    }
}
