using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal sealed record LauncherBackground(string StillUrl, string? VideoUrl);

internal static class LauncherBackgroundCache
{
    private const string CdnOrigin = "https://l1-all-plat-server.manjuu.com/";
    private const int MaxCacheSize = 16 * 1024;

    internal static async Task<string> GetOrDownloadAsync(HttpClient http, string directory, string url,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = new Uri(url);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + extension);
        if (IsCached(path))
            return path;

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
                if (output.Length == 0 || (response.Content.Headers.ContentLength is { } size && size != output.Length))
                    throw new InvalidDataException("The publisher's background download is incomplete.");
            }

            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException) when (IsCached(path)) { }
            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsCached(string path) => new FileInfo(path) is { Exists: true, Length: > 0 };

    internal static LauncherBackground? Read(Func<string?> findVerifiedLauncherDirectory)
    {
        try
        {
            if (findVerifiedLauncherDirectory() is not { } directory || !Path.IsPathFullyQualified(directory))
                return null;
            string path = Path.Combine(directory, "AppData", "data", "cache.json");
            if (!IsLocalFile(path))
                return null;

            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxCacheSize)
                return null;
            byte[] data = new byte[MaxCacheSize + 1];
            int read = input.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            if (read > MaxCacheSize)
                return null;

            using JsonDocument document = JsonDocument.Parse(data.AsMemory(0, read));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadUrl(root, "bgUrl", "/azurpromilia/img/", [".png", ".jpg", ".jpeg"], out string? still) ||
                !TryReadUrl(root, "videoUrl", "/azurpromilia/video/", [".mp4", ".webm"], out string? video))
                return null;

            return new LauncherBackground(still!, video);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or NotSupportedException or JsonException)
        {
            return null;
        }
    }

    internal static bool IsLocalFile(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            string fullPath = Path.GetFullPath(path);
            if (Path.GetPathRoot(fullPath) is not { Length: 3 } drive || drive[1] != Path.VolumeSeparatorChar)
                return false;

            string current = drive;
            FileAttributes attributes = FileAttributes.Directory;
            foreach (string segment in fullPath[drive.Length..].Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return (attributes & FileAttributes.Directory) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadUrl(JsonElement cache, string name, string folder, string[] extensions, out string? result)
    {
        result = null;
        if (!cache.TryGetProperty(name, out JsonElement field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { } raw || !raw.StartsWith(CdnOrigin, StringComparison.OrdinalIgnoreCase) ||
            raw.IndexOfAny(['?', '#', '\\']) >= 0 || raw.Any(char.IsControl) ||
            raw.Contains("%2f", StringComparison.OrdinalIgnoreCase) || raw.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) ||
            !extensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase))
            return false;

        result = raw;
        return true;
    }
}
