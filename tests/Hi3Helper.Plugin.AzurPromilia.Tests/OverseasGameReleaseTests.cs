using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class OverseasGameReleaseTests
{
    internal static async Task RunAsync()
    {
        CheckJapanBootstrap();
        await CheckJapanAsync();
        await CheckTaiwanAsync();
        await CheckBootstrapFallbackAsync();
    }

    private static void CheckJapanBootstrap()
    {
        const string installerName = "AZUPRO_JP_Gamelauncher-1.8.1-setup.exe";
        byte[] digest = SHA512.HashData("fixture"u8);
        string metadata = $"""
            version: 1.8.1
            files:
              - url: {installerName}
                sha512: {Convert.ToBase64String(digest)}
                size: 128
            path: {installerName}
            sha512: {Convert.ToBase64String(digest)}
            """;
        JapanLauncherBootstrap.InstallerRelease release = JapanLauncherBootstrap.ParseInstallerRelease(metadata);
        Require(release.Download.Host == "launcher-pkg-azupro-jp.yo-star.com" &&
            release.Download.AbsolutePath.EndsWith('/' + installerName, StringComparison.Ordinal) &&
            release.Sha512.SequenceEqual(digest) && release.Size == 128,
            "The published Yostar installer must retain its own distribution URL, size and SHA512.");

        byte[] archive = Convert.FromHexString("377ABCAF271C0004E3FF1673000000000000000001000000000000008DEF02D200");
        byte[] payload = [0x12, 0x34, .. archive, 0x56];
        Require(JapanLauncherBootstrap.LocateArchive(payload) == (2, archive.Length),
            "The embedded 7z archive must be read at its own position and bounded to its declared length.");
        try
        {
            _ = JapanLauncherBootstrap.LocateArchive([.. payload, .. archive]);
            throw new InvalidOperationException("An installer containing two possible game archives was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static async Task CheckJapanAsync()
    {
        const string origin = "api-launcher-jp.yo-star.com";
        const string cdn = "launcher-pkg-azupro-jp.yo-star.com";
        const string mirror = "launcher-pkg-azupro-jp-bk.yo-star.com";
        const string manifest = """
            {"source":"/JP-game","file":[{"path":"/AzurPromiliaJP.exe","size":"9","hash":"11051210869376104954"}]}
            """;
        using var handler = new ReleaseFixture((uri, request) =>
        {
            bool hasAuth = request.Headers.Contains("Authorization");
            Require(hasAuth == (uri.Host == origin), "Yostar's launcher signature must stay on its release API.");
            return (uri.Host, uri.AbsolutePath) switch
            {
                (origin, "/api/launcher/game/config") => Json("""
                    {"code":200,"data":{"game_latest_version":"1.0.2","game_latest_file_path":"prod/JP.zip","game_start_exe_name":"AzurPromiliaJP"}}
                    """),
                (origin, "/api/launcher/game/config/json") when uri.Query == "?version=1.0.2&file_path=prod%2FJP.zip" =>
                    Json($$"""{"code":200,"data":{"url":"https://{{cdn}}/manifest.json"} }"""),
                (origin, "/api/launcher/advanced/game/download/cdn") =>
                    Json($$"""{"code":200,"data":{"primary_cdn":"https://{{cdn}}/","back_up_cdn":"https://{{mirror}}/"} }"""),
                (cdn, "/manifest.json") => Json(manifest),
                _ => throw new InvalidOperationException("The Japanese release used an unexpected request.")
            };
        });
        using var http = new HttpClient(handler);
        var configuration = new JapanGameReleaseSource.LauncherConfiguration(new Version(1, 8, 1), "fixture");
        GameInstallPlan plan = await JapanGameReleaseSource.LoadAsync(http, configuration, CancellationToken.None);
        Require(plan.Profile == "AzurPromiliaJP" && plan.Version == new Version(1, 0, 2) &&
            plan.BaseUris.SequenceEqual([new Uri($"https://{cdn}/JP-game/"), new Uri($"https://{mirror}/JP-game/")]) &&
            plan.Files.SequenceEqual([GameFile.WithCrc64(9, 11051210869376104954, "AzurPromiliaJP.exe", "AzurPromiliaJP.exe")]),
            "The Japanese game manifest must preserve its file entry, CRC64 and configured CDN paths.");

        foreach (string invalid in new[]
        {
            manifest.Replace("/JP-game", "//foreign.example", StringComparison.Ordinal),
            manifest.Replace("/AzurPromiliaJP.exe", "/../AzurPromiliaJP.exe", StringComparison.Ordinal),
            manifest.Replace("11051210869376104954", "18446744073709551616", StringComparison.Ordinal)
        })
        {
            try
            {
                _ = JapanGameReleaseSource.ParseManifest(Encoding.UTF8.GetBytes(invalid), plan.Version, plan.BaseUris);
                throw new InvalidOperationException("An invalid Japanese game manifest was accepted.");
            }
            catch (InvalidDataException) { }
        }
    }

    private static async Task CheckTaiwanAsync()
    {
        const string origin = "api-grp-rgn1.jimi-global.com";
        const string cdn = "syncstation-rgn1.jimi-global.com";
        const string installerPath = "/azurpromilia/launcher/sc500/fixture/AzurPromilia_setup.exe";
        const string launcherId = "1a3ee4447516436a9ec4b5d1ec1635e2";
        var expected = new GameFile(3, "900150983cd24fb0d6963f7d28e17f72", "AzurPromilia.exe", "data/game.exe");
        using Aes cipher = Aes.Create();
        cipher.KeySize = 192;
        cipher.GenerateKey();
        cipher.GenerateIV();
        byte[] plain = Encoding.UTF8.GetBytes($"1,1,0\n0,{expected.Size},{expected.Md5},{expected.InstallPath},{expected.RemotePath}\n");
        byte[] index = Encoding.ASCII.GetBytes(Convert.ToBase64String(cipher.EncryptCbc(plain, cipher.IV, PaddingMode.PKCS7)));
        string checksum = Convert.ToHexStringLower(MD5.HashData(index));
        using var handler = new ReleaseFixture((uri, request) => (uri.Host, uri.AbsolutePath) switch
        {
            (origin, "/activity/web/download/info") when uri.Query == "?gameId=43" && request.Headers.Contains("sign") =>
                Json($$"""{"code":0,"data":{"nativeLauncherDownloadUrl":"https://{{cdn}}{{installerPath}}"} }"""),
            (origin, "/pc-launcher/client/gameversion/config") when uri.Query == $"?launcherId={launcherId}" => Json("""
                {"code":0,"data":{"released":{"versionCode":"1.0.0.4","gameFolderName":"AzurPromilia_TC_game","launchEntry":"AzurPromilia.exe"}}}
                """),
            (origin, "/pc-launcher/client/gameversion/packageconfig") when uri.Query == $"?launcherId={launcherId}&uid=" => Json($$"""
                {"code":0,"data":{"released":{"versionCode":"1.0.0.4","domainList":["https://{{cdn}}/"],"gamePackageList":[
                    {"packageType":2,"fileSize":{{index.Length}},"fileMd5":"{{checksum}}","downloadUrl":"index.txt"}]} } }
                """),
            (cdn, "/index.txt") when !request.Headers.Contains("sign") =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(index) },
            _ => throw new InvalidOperationException("The Taiwan release used an unexpected request.")
        });
        using var http = new HttpClient(handler);
        var configuration = new ChinaGameReleaseSource.LauncherConfiguration(launcherId, "434", "C500", "1.0.2.2",
            "fixture", cipher.Key, cipher.IV);
        Require(ChinaGameReleaseSource.ReadLocalConfiguration(() => "fixture", _ => configuration,
            ChinaGameReleaseSource.IsChinaConfiguration) is null,
            "A local launcher from another region must be ignored in favor of its published installer.");
        Uri installer = await TaiwanGameReleaseSource.FindInstallerAsync(http, CancellationToken.None);
        GameInstallPlan plan = await TaiwanGameReleaseSource.LoadAsync(http, configuration, CancellationToken.None);
        Require(installer == new Uri($"https://{cdn}{installerPath}") && plan.Profile == "AzurPromiliaTW" &&
            plan.Version == new Version(1, 0, 0, 4) && plan.Files.SequenceEqual([expected]),
            "The Taiwan release must use its published channel and retain the executable in the verified game index.");
    }

    private static async Task CheckBootstrapFallbackAsync()
    {
        DirectoryInfo launcher = Directory.CreateTempSubdirectory("AzurPromilia-BootstrapFallback-");
        try
        {
            foreach (string relative in new[] { ChinaGameReleaseSource.ArchivePath, ChinaGameReleaseSource.NativeLibraryPath,
                         ChinaGameReleaseSource.ApplicationConfigPath })
            {
                string file = Path.Combine(launcher.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, new byte[16]);
            }

            (Func<HttpClient, CancellationToken, Func<string?>, Task<GameInstallPlan>> Load, string Discovery)[] sources =
            [
                (ChinaGameReleaseSource.LoadAsync, "/activity/web/download/info?gameId=35"),
                (TaiwanGameReleaseSource.LoadAsync, "/activity/web/download/info?gameId=43"),
                (JapanGameReleaseSource.LoadAsync, "/install_pkg/game_launcher/AZUPRO_JP/latest.yml")
            ];
            foreach (var (load, discovery) in sources)
            {
                bool reachedDiscovery = false;
                using var http = new HttpClient(new ReleaseFixture((uri, _) =>
                {
                    Require(uri.PathAndQuery == discovery, "Bootstrap must request its own publisher's installer metadata.");
                    reachedDiscovery = true;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }));
                try
                {
                    await load(http, CancellationToken.None, () => launcher.FullName);
                    throw new InvalidOperationException("Bootstrap succeeded without valid local or remote launcher data.");
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException) { }

                Require(reachedDiscovery, "An unreadable local launcher must fall back to signed publisher bootstrap.");
            }
        }
        finally
        {
            launcher.Delete(recursive: true);
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ReleaseFixture(Func<Uri, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.RequestUri!, request));
        }
    }
}
