using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class TaiwanGameReleaseSource
{
    private const string Profile = "AzurPromiliaTW";
    private const string LauncherId = "1a3ee4447516436a9ec4b5d1ec1635e2";
    private const string AppId = "434";
    private const string Channel = "C500";
    private const string GameFolder = "AzurPromilia_TC_game";
    private const string Executable = "AzurPromilia.exe";
    private const string Publisher = "JIMI PTE. LTD.";
    private const string PackagePath = "/pc-launcher/client/gameversion/packageconfig";
    private const string VersionPath = "/pc-launcher/client/gameversion/config";
    private static readonly Uri[] ApiOrigins =
    [
        new("https://api-grp-rgn1.jimi-global.com"), new("https://l2-api-grp-rgn1.jimi-global.com")
    ];
    private static readonly HashSet<string> CdnHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "syncstation-rgn1.jimi-global.com", "l2-syncstation-rgn1.jimi-global.com"
    };
    private static readonly string DeviceId = Guid.NewGuid().ToString();

    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http, CancellationToken token,
        Func<string?> findVerifiedLauncherDirectory)
    {
        token.ThrowIfCancellationRequested();
        ChinaGameReleaseSource.LauncherConfiguration? configuration = ChinaGameReleaseSource.ReadLocalConfiguration(
            findVerifiedLauncherDirectory, ChinaGameReleaseSource.ReadLauncherConfiguration, IsTaiwanConfiguration);
        if (configuration is null)
        {
            Uri download = await FindInstallerAsync(http, token).ConfigureAwait(false);
            configuration = await ChinaLauncherBootstrap.LoadAsync(http, download, "taiwan-launcher-", Publisher,
                IsTaiwanConfiguration, token).ConfigureAwait(false);
        }
        return await LoadAsync(http, configuration, token).ConfigureAwait(false);
    }

    internal static async Task<GameInstallPlan> LoadAsync(HttpClient http,
        ChinaGameReleaseSource.LauncherConfiguration configuration, CancellationToken token)
    {
        if (!IsTaiwanConfiguration(configuration))
            throw new InvalidDataException("The publisher launcher configuration is for a different channel.");

        Exception? lastFailure = null;
        foreach (Uri origin in ApiOrigins)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument summary = await ChinaGameReleaseSource.GetAsync(http, origin, VersionPath, configuration,
                    "zht", false, token).ConfigureAwait(false);
                JsonElement release = ChinaGameReleaseSource.RequireReleased(summary.RootElement);
                if (Text(release, "gameFolderName") != GameFolder || Text(release, "launchEntry") != Executable)
                    throw new InvalidDataException("The publisher's release does not identify the Taiwan game.");

                using JsonDocument packages = await ChinaGameReleaseSource.GetAsync(http, origin, PackagePath, configuration,
                    "zht", true, token).ConfigureAwait(false);
                if (Text(ChinaGameReleaseSource.RequireReleased(packages.RootElement), "versionCode") != Text(release, "versionCode"))
                    throw new InvalidDataException("The publisher's game version changed during the release query.");

                GameInstallPlan plan = await ManjuuReleaseIndex.LoadAsync(http, Profile, packages.RootElement, preload: false,
                    CdnHosts, configuration.FormatKey, configuration.Iv, token).ConfigureAwait(false);
                if (!plan.Files.Any(file => file.InstallPath.Equals(Executable, StringComparison.OrdinalIgnoreCase) && file.Size > 0))
                    throw new InvalidDataException("The Taiwan game manifest is missing its game executable.");
                return plan;
            }
            catch (Exception error) when (GameCdn.IsTransferFailure(error, token) || error is JsonException)
            {
                lastFailure = error;
            }
        }

        throw new InvalidDataException("The Taiwan game release is unavailable from the publisher.", lastFailure);
    }

    internal static async Task<Uri> FindInstallerAsync(HttpClient http, CancellationToken token)
    {
        const string query = "gameId=43";
        Exception? lastFailure = null;
        foreach (Uri origin in ApiOrigins)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, $"/activity/web/download/info?{query}"));
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                request.Headers.Add("timestamp", timestamp);
                request.Headers.Add("sign", Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + query))));
                request.Headers.Add("device-id", DeviceId);
                request.Headers.Add("grp-trace-id", Guid.NewGuid().ToString());
                request.Headers.Add("sdk-version", "1.2.3");
                request.Headers.Add("lang", "zh-Hant");
                request.Headers.Add("grp-client-info", """{"platform":"Web","device_language":"zh-TW"}""");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using JsonDocument json = await JsonDocument.ParseAsync(content, cancellationToken: token).ConfigureAwait(false);
                JsonElement code = Property(json.RootElement, "code");
                if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int status) && status == 0 &&
                    Uri.TryCreate(Text(Property(json.RootElement, "data"), "nativeLauncherDownloadUrl"), UriKind.Absolute, out Uri? download) &&
                    IsOfficialDownload(download))
                    return download;

                throw new InvalidDataException("The publisher did not provide a Taiwan launcher download.");
            }
            catch (Exception error) when (GameCdn.IsTransferFailure(error, token) || error is JsonException)
            {
                lastFailure = error;
            }
        }
        throw new InvalidDataException("The Taiwan publisher launcher download is unavailable.", lastFailure);
    }

    private static bool IsOfficialDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        CdnHosts.Contains(uri.IdnHost) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.StartsWith("/azurpromilia/launcher/sc500/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsTaiwanConfiguration(ChinaGameReleaseSource.LauncherConfiguration configuration) =>
        configuration.LauncherId == LauncherId && configuration.AppId == AppId && configuration.ChannelCode == Channel;

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static string Text(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : string.Empty;
    }
}
