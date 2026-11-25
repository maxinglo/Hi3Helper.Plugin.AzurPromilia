using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class JapanGameReleaseSource
{
    private const string Profile = "AzurPromiliaJP";
    internal const string GameTag = "AZUPRO_JP";
    private const string Executable = "AzurPromiliaJP";
    private const int ApiResponseLimit = 1024 * 1024;
    private const int ManifestLimit = 32 * 1024 * 1024;
    private static readonly Uri ApiOrigin = new("https://api-launcher-jp.yo-star.com");
    private static readonly HashSet<string> PublisherCdnHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher-pkg-azupro-jp.yo-star.com", "launcher-pkg-azupro-jp-bk.yo-star.com"
    };

    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http, CancellationToken token,
        Func<string?> findVerifiedLauncherDirectory)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            LauncherConfiguration configuration = ChinaGameReleaseSource.ReadLocalConfiguration(
                findVerifiedLauncherDirectory, ReadLauncherConfiguration)
                ?? await JapanLauncherBootstrap.LoadAsync(http, token).ConfigureAwait(false);
            return await LoadAsync(http, configuration, token).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Yostar returned unreadable Japanese game release data.", error);
        }
    }

    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http, LauncherConfiguration configuration,
        CancellationToken token)
    {
        using JsonDocument gameDocument = await GetApiAsync(http, configuration, "/api/launcher/game/config", token)
            .ConfigureAwait(false);
        JsonElement game = RequireData(gameDocument.RootElement);
        string currentVersion = Text(game, "game_latest_version");
        string packagePath = Text(game, "game_latest_file_path");
        if (!Version.TryParse(currentVersion, out Version? version) || !IsRelativePath(packagePath) ||
            Text(game, "game_start_exe_name") != Executable)
            throw new InvalidDataException("Yostar did not identify its current Japanese game release.");

        string query = $"?version={Uri.EscapeDataString(currentVersion)}&file_path={Uri.EscapeDataString(packagePath)}";
        using JsonDocument manifestDocument = await GetApiAsync(http, configuration, "/api/launcher/game/config/json" + query,
            token).ConfigureAwait(false);
        Uri manifestUri = RequirePublisherUri(Text(RequireData(manifestDocument.RootElement), "url"));

        using JsonDocument cdnDocument = await GetApiAsync(http, configuration, "/api/launcher/advanced/game/download/cdn",
            token).ConfigureAwait(false);
        JsonElement cdns = RequireData(cdnDocument.RootElement);
        Uri[] roots = [RequirePublisherUri(Text(cdns, "primary_cdn")), RequirePublisherUri(Text(cdns, "back_up_cdn"))];

        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        byte[] bytes = await ReadResponseAsync(http, request, ManifestLimit, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return ParseManifest(bytes, version, roots);
    }

    internal static GameInstallPlan ParseManifest(ReadOnlyMemory<byte> bytes, Version version, IReadOnlyList<Uri> roots)
    {
        using JsonDocument manifest = JsonDocument.Parse(bytes);
        string source = ReadRootedPath(Text(manifest.RootElement, "source"));
        IReadOnlyList<GameFile> files = ReadFiles(Property(manifest.RootElement, "file"), Executable + ".exe");

        var cdns = new List<Uri>(roots.Count);
        string escapedSource = string.Join('/', source.Split('/').Select(Uri.EscapeDataString)) + '/';
        foreach (Uri candidate in roots.Distinct())
        {
            if (!GameCdn.TryNormalizeRoot(candidate, out Uri? root) || !IsPublisherUri(root) ||
                !GameCdn.TryResolvePath(root, escapedSource, out Uri? cdn))
                throw new InvalidDataException("Yostar's Japanese game CDN path is invalid.");
            cdns.Add(cdn);
        }
        if (cdns.Count == 0)
            throw new InvalidDataException("Yostar's Japanese game manifest has no CDN.");

        return new GameInstallPlan(Profile, version, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), cdns, files, []);
    }

    internal static IReadOnlyList<GameFile> ReadFiles(JsonElement rows, string executable)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw new InvalidDataException("Yostar's Japanese game manifest has no files.");

        var files = new List<GameFile>(rows.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string path = ReadRootedPath(Text(row, "path"));
            if (!long.TryParse(Text(row, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                !ulong.TryParse(Text(row, "hash"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong crc64) ||
                !paths.Add(path))
                throw new InvalidDataException("Yostar's Japanese game manifest has an invalid file entry.");
            files.Add(GameFile.WithCrc64(size, crc64, path, path));
        }
        if (!files.Any(file => file.Size > 0 && file.InstallPath.Equals(executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Yostar's Japanese game manifest is missing its game executable.");

        return files;
    }

    private static LauncherConfiguration ReadLauncherConfiguration(string directory)
    {
        using FileStream archive = File.OpenRead(Path.Combine(directory, ChinaGameReleaseSource.ArchivePath));
        return ReadLauncherConfiguration(archive);
    }

    internal static LauncherConfiguration ReadLauncherConfiguration(Stream archive)
    {
        using JsonDocument entries = ChinaGameReleaseSource.ReadArchiveHeader(archive, out long dataStart);
        using JsonDocument package = JsonDocument.Parse(ChinaGameReleaseSource.ReadArchiveEntry(archive, entries.RootElement,
            dataStart, "package.json"));
        string source = Encoding.UTF8.GetString(ChinaGameReleaseSource.ReadArchiveEntry(archive, entries.RootElement,
            dataStart, "out/main/index.js"));

        const string authorization = "const Authorization = utils$1.getAuthHeader({";
        const string saltProperty = "salt: \"";
        int start = source.IndexOf(authorization, StringComparison.Ordinal);
        int end = start < 0 ? -1 : source.IndexOf("});", start + authorization.Length, StringComparison.Ordinal);
        if (end < 0 || end - start > 1024 || Text(package.RootElement, "name") != "AZUPRO_JP_Gamelauncher" ||
            !Version.TryParse(Text(package.RootElement, "version"), out Version? version))
            throw new InvalidDataException("The Japanese publisher launcher configuration is invalid.");

        ReadOnlySpan<char> call = source.AsSpan(start, end - start);
        int saltStart = call.IndexOf(saltProperty, StringComparison.Ordinal);
        if (saltStart < 0)
            throw new InvalidDataException("The Japanese publisher launcher request format is unsupported.");
        ReadOnlySpan<char> salt = call[(saltStart + saltProperty.Length)..];
        int saltEnd = salt.IndexOf('"');
        if (saltEnd is < 1 or > 128)
            throw new InvalidDataException("The Japanese publisher launcher request format is invalid.");

        return new LauncherConfiguration(version, salt[..saltEnd].ToString());
    }

    private static async Task<JsonDocument> GetApiAsync(HttpClient http, LauncherConfiguration configuration, string path,
        CancellationToken token)
    {
        using HttpRequestMessage request = configuration.CreateGuestRequest(new Uri(ApiOrigin, path));
        byte[] bytes = await ReadResponseAsync(http, request, ApiResponseLimit, token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private static async Task<byte[]> ReadResponseAsync(HttpClient http, HttpRequestMessage request, int limit,
        CancellationToken token)
    {
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(limit, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private static JsonElement RequireData(JsonElement document)
    {
        JsonElement code = Property(document, "code");
        JsonElement data = Property(document, "data");
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int status) || status != 200 ||
            data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Yostar did not return Japanese game release data.");
        return data;
    }

    private static Uri RequirePublisherUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IsPublisherUri(uri))
            throw new InvalidDataException("Yostar returned an invalid Japanese game CDN URL.");
        return uri;
    }

    private static bool IsPublisherUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        PublisherCdnHosts.Contains(uri.IdnHost);

    private static string ReadRootedPath(string value)
    {
        if (value.Length < 2 || value[0] != '/' || !IsRelativePath(value[1..]))
            throw new InvalidDataException("Yostar's Japanese game manifest contains an invalid path.");
        return value[1..];
    }

    private static bool IsRelativePath(string value) => value.Length is > 0 and <= 32767 &&
        value.Split('/').All(segment => segment.Length is > 0 and <= 255 &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            segment.All(character => !char.IsControl(character) && !"\\:*?\"<>|#%".Contains(character)));

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static string Text(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : string.Empty;
    }

    internal sealed class LauncherConfiguration(Version version, string salt)
    {
        internal HttpRequestMessage CreateGuestRequest(Uri uri)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string header = FormattableString.Invariant($$"""{"game_tag":"{{GameTag}}","time":{{timestamp}},"version":"{{version}}"}""");
            byte[] signed = Encoding.UTF8.GetBytes(header + salt);
            string signature;
            try
            {
                signature = Convert.ToHexStringLower(MD5.HashData(signed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signed);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", $$"""{"head":{{header}},"sign":"{{signature}}"}""");
            return request;
        }
    }
}
