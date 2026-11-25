using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class ChinaGameReleaseSource
{
    private const string Profile = "AzurPromiliaCN";
    private const string LauncherId = "93cdd43e096046138053253dc56d3562";
    private const string AppId = "354";
    private const string Channel = "C000";
    private const string GameFolder = "AzurPromilia_Game";
    private const string Executable = "AzurPromilia.exe";
    private const string PackagePath = "/pc-launcher/client/gameversion/packageconfig";
    private const string VersionPath = "/pc-launcher/client/gameversion/config";
    internal const string ArchivePath = "resources/app.asar";
    internal const string NativeLibraryPath = "resources/app.asar.unpacked/node_modules/napi-cutils/build/Release/cutils.node";
    internal const string ApplicationConfigPath = "res/app_config.dat";
    private static readonly Uri[] ApiOrigins = [new("https://api-grp.manjuu.com"), new("https://api-grp-l2.manjuu.com")];
    private static readonly HashSet<string> CdnHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "syncstation.manjuu.com", "l2-syncstation.manjuu.com"
    };

    // Use the publisher's guest distribution request with the launcher application configuration.
    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http, CancellationToken token,
        Func<string?> findVerifiedLauncherDirectory)
    {
        token.ThrowIfCancellationRequested();
        LauncherConfiguration? configuration = ReadLocalConfiguration(findVerifiedLauncherDirectory,
            ReadLauncherConfiguration, IsChinaConfiguration);
        configuration ??= await ChinaLauncherBootstrap.LoadAsync(http, token).ConfigureAwait(false);
        return await LoadAsync(http, configuration, token).ConfigureAwait(false);
    }

    internal static T? ReadLocalConfiguration<T>(Func<string?> find, Func<string, T> read, Predicate<T>? matches = null)
        where T : class
    {
        try
        {
            if (find() is not { } directory)
                return null;

            T configuration = read(directory);
            return matches is null || matches(configuration) ? configuration : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http, LauncherConfiguration configuration,
        CancellationToken token)
    {
        if (!IsChinaConfiguration(configuration))
            throw new InvalidDataException("The publisher launcher configuration is for a different channel.");

        Exception? lastFailure = null;
        foreach (Uri origin in ApiOrigins)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument summary = await GetAsync(http, origin, VersionPath, configuration, "zh", false, token)
                    .ConfigureAwait(false);
                JsonElement release = RequireReleased(summary.RootElement);
                if (Text(release, "gameFolderName") != GameFolder || Text(release, "launchEntry") != Executable)
                    throw new InvalidDataException("The publisher's release does not identify the mainland China game.");

                using JsonDocument packages = await GetAsync(http, origin, PackagePath, configuration, "zh", true, token)
                    .ConfigureAwait(false);
                if (Text(RequireReleased(packages.RootElement), "versionCode") != Text(release, "versionCode"))
                    throw new InvalidDataException("The publisher's game version changed during the release query.");

                return await ManjuuReleaseIndex.LoadAsync(http, Profile, packages.RootElement, preload: false,
                    CdnHosts, configuration.FormatKey, configuration.Iv, token).ConfigureAwait(false);
            }
            catch (Exception error) when (GameCdn.IsTransferFailure(error, token) || error is JsonException)
            {
                lastFailure = error;
            }
        }

        throw new InvalidDataException("The mainland China game release is unavailable from the publisher.", lastFailure);
    }

    internal static bool IsChinaConfiguration(LauncherConfiguration configuration) =>
        configuration.LauncherId == LauncherId && configuration.AppId == AppId && configuration.ChannelCode == Channel;

    internal static async Task<JsonDocument> GetAsync(HttpClient http, Uri origin, string path,
        LauncherConfiguration configuration, string language, bool includeUid, CancellationToken token)
    {
        using HttpRequestMessage request = configuration.CreateGuestRequest(origin, path, language, includeUid);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(content, cancellationToken: token).ConfigureAwait(false);
    }

    internal static JsonElement RequireReleased(JsonElement response)
    {
        JsonElement code = Property(response, "code");
        JsonElement release = Property(Property(response, "data"), "released");
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int value) || value != 0 ||
            !Version.TryParse(Text(release, "versionCode"), out _))
            throw new InvalidDataException("The publisher did not provide its current game release.");
        return release;
    }

    internal static LauncherConfiguration ReadLauncherConfiguration(string directory)
    {
        using FileStream archive = File.OpenRead(Path.Combine(directory, ArchivePath));
        using FileStream native = File.OpenRead(Path.Combine(directory, NativeLibraryPath));
        using FileStream applicationConfig = File.OpenRead(Path.Combine(directory, ApplicationConfigPath));
        return ReadLauncherConfiguration(archive, native, applicationConfig);
    }

    internal static LauncherConfiguration ReadLauncherConfiguration(Stream archive, Stream native, Stream applicationConfig)
    {
        try
        {
            using JsonDocument entries = ReadArchiveHeader(archive, out long dataStart);
            using JsonDocument package = JsonDocument.Parse(ReadArchiveEntry(archive, entries.RootElement, dataStart, "package.json"));
            string main = Encoding.UTF8.GetString(ReadArchiveEntry(archive, entries.RootElement, dataStart, "main.js"));
            const string marker = "const c=JSON.parse('";
            int start = main.IndexOf(marker, StringComparison.Ordinal);
            int end = start < 0 ? -1 : main.IndexOf("');", start + marker.Length, StringComparison.Ordinal);
            if (end < 0 || end - start > 8192)
                throw new InvalidDataException("The publisher launcher has an unsupported game index configuration.");

            using JsonDocument app = JsonDocument.Parse(main[(start + marker.Length)..end]);
            byte[] indexKey = Encoding.UTF8.GetBytes(Text(app.RootElement, "HM"));
            byte[] indexIv = Encoding.UTF8.GetBytes(Text(app.RootElement, "rs"));
            JsonElement build = Property(app.RootElement, "uz");
            string version = $"{Text(package.RootElement, "version")}.{build.GetRawText()}";
            if (indexKey.Length != 24 || indexIv.Length != 16 || !Version.TryParse(version, out _))
                throw new InvalidDataException("The publisher launcher has invalid game index parameters.");

            return ReadApplicationConfiguration(native, applicationConfig, version, indexKey, indexIv);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidOperationException or
                                      UnauthorizedAccessException)
        {
            throw new InvalidDataException("The publisher launcher configuration could not be read.");
        }
    }

    internal static JsonDocument ReadArchiveHeader(Stream archive, out long dataStart)
    {
        Span<byte> preamble = stackalloc byte[16];
        archive.ReadExactly(preamble);
        int pickleSize = BinaryPrimitives.ReadInt32LittleEndian(preamble);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(preamble[4..]);
        int payloadSize = BinaryPrimitives.ReadInt32LittleEndian(preamble[8..]);
        int jsonSize = BinaryPrimitives.ReadInt32LittleEndian(preamble[12..]);
        dataStart = 8L + headerSize;
        if (pickleSize != 4 || headerSize is < 8 or > 4_194_304 || payloadSize != headerSize - 4 ||
            jsonSize < 2 || jsonSize > headerSize - 8 || dataStart > archive.Length)
            throw new InvalidDataException("The publisher launcher archive header is invalid.");

        var header = new byte[jsonSize];
        archive.ReadExactly(header);
        return JsonDocument.Parse(header);
    }

    internal static byte[] ReadArchiveEntry(Stream archive, JsonElement entries, long dataStart, string name)
    {
        JsonElement entry = entries;
        foreach (string component in name.Split('/'))
            entry = Property(Property(entry, "files"), component);
        JsonElement size = Property(entry, "size");
        JsonElement integrity = Property(entry, "integrity");
        string hash = Text(integrity, "hash");
        if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out int length) ||
            length is < 1 or > 2_097_152 || Text(integrity, "algorithm") != "SHA256" || hash.Length != 64 ||
            !long.TryParse(Text(entry, "offset"), NumberStyles.None, CultureInfo.InvariantCulture, out long offset) ||
            offset > archive.Length - dataStart - length)
            throw new InvalidDataException("A publisher launcher archive entry is invalid.");

        var data = new byte[length];
        archive.Position = dataStart + offset;
        archive.ReadExactly(data);
        if (!Convert.ToHexStringLower(SHA256.HashData(data)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A publisher launcher archive entry failed its integrity check.");

        return data;
    }

    private static LauncherConfiguration ReadApplicationConfiguration(Stream native, Stream source, string version,
        byte[] indexKey, byte[] indexIv)
    {
        const string chinaNativeSha256 = "6a487a372e73f8bc8b46124b134c60b17ac8465711d71584d55890cc5b0d1a78";
        const string taiwanNativeSha256 = "8cd346e3f1320485f266678289baae9879128c3ac32f0360543643def993f9bd";
        const long keyOffset = 0x5FBEA8;
        const int ivSize = 16;
        // Read the configuration format parameter only from the matching publisher binary.
        if (native.Length is <= keyOffset + 32 or > 33_554_432 ||
            Convert.ToHexStringLower(SHA256.HashData(native)) is not (chinaNativeSha256 or taiwanNativeSha256))
            throw new InvalidDataException("The publisher launcher uses an unsupported application configuration format.");

        if (source.Length is <= ivSize or > 16_384 || source.Length % ivSize != 0)
            throw new InvalidDataException("The publisher launcher application configuration is invalid.");

        var cipher = new byte[(int)source.Length];
        source.ReadExactly(cipher);
        var key = new byte[32];
        native.Position = keyOffset;
        native.ReadExactly(key);
        byte[] plaintext;
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            plaintext = aes.DecryptCbc(cipher.AsSpan(ivSize), cipher.AsSpan(0, ivSize), PaddingMode.PKCS7);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(plaintext);
            JsonElement root = json.RootElement;
            string secret = Text(root, "secretKey");
            if (secret.Length is < 1 or > 128)
                throw new InvalidDataException("The publisher launcher guest request configuration is invalid.");

            return new LauncherConfiguration(Text(root, "launcherId"), Text(root, "appId"), Text(root, "channelCode"),
                version, secret, indexKey, indexIv);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static string Text(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : string.Empty;
    }

    internal sealed class LauncherConfiguration(string launcherId, string appId, string channelCode, string version,
        string secret, byte[] formatKey, byte[] iv)
    {
        private const string Platform = "Launcher";
        private static readonly string DeviceId = Guid.NewGuid().ToString();

        internal string LauncherId { get; } = launcherId;
        internal string AppId { get; } = appId;
        internal string ChannelCode { get; } = channelCode;
        internal ReadOnlyMemory<byte> FormatKey { get; } = formatKey;
        internal ReadOnlyMemory<byte> Iv { get; } = iv;

        internal HttpRequestMessage CreateGuestRequest(Uri origin, string path, string language, bool includeUid)
        {
            string query = $"launcherId={Uri.EscapeDataString(LauncherId)}{(includeUid ? "&uid=" : string.Empty)}";
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            byte[] signed = Encoding.UTF8.GetBytes(timestamp + query + AppId + ChannelCode + secret);
            string signature;
            try
            {
                signature = Convert.ToHexStringLower(MD5.HashData(signed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signed);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, $"{path}?{query}"));
            request.Headers.Add("timestamp", timestamp);
            request.Headers.Add("sign", signature);
            request.Headers.Add("device-id", DeviceId);
            request.Headers.Add("grp-trace-id", Guid.NewGuid().ToString());
            request.Headers.Add("grp-app-id", AppId);
            request.Headers.Add("grp-platform", Platform);
            request.Headers.Add("grp-channel-code", ChannelCode);
            request.Headers.Add("grp-sub-channel-code", string.Empty);
            request.Headers.Add("sdk-version", version);
            request.Headers.Add("channel-sdk-version", version);
            request.Headers.Add("grp-launcher-version-code", version);
            request.Headers.Add("lang", language);
            request.Headers.Add("grp-client-info", """{"device_id":"","device_os":"Windows","platform":"Launcher"}""");
            return request;
        }
    }
}
