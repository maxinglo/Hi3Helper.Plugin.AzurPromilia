using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class ChinaGameReleaseSourceTests
{
    private const string LauncherId = "93cdd43e096046138053253dc56d3562";
    private const string TestSecret = "synthetic application credential";
    private const string Primary = "api-grp.manjuu.com";
    private const string Mirror = "api-grp-l2.manjuu.com";
    private const string Cdn = "syncstation.manjuu.com";

    internal static async Task RunAsync()
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 192;
        aes.GenerateKey();
        aes.GenerateIV();
        byte[] plaintext = "1,1,0\n0,3,900150983cd24fb0d6963f7d28e17f72,AzurPromilia.exe,assets/AzurPromilia.exe\n"u8.ToArray();
        byte[] index = Encoding.ASCII.GetBytes(Convert.ToBase64String(aes.EncryptCbc(plaintext, aes.IV, PaddingMode.PKCS7)));
        var app = new ChinaGameReleaseSource.LauncherConfiguration(LauncherId, "354", "C000", "1.0.2.2", TestSecret,
            aes.Key, aes.IV);

        using var requests = new ReleaseFixture(index);
        using var http = new HttpClient(requests);
        GameInstallPlan plan = await ChinaGameReleaseSource.LoadAsync(http, app, CancellationToken.None);
        Require(plan.Profile == "AzurPromiliaCN" && plan.Version == new Version(1, 0, 0, 31) &&
            plan.Files is [{ InstallPath: "AzurPromilia.exe" }] &&
            plan.IndexSha256 == Convert.ToHexStringLower(SHA256.HashData(index)),
            "The guest release must load the current, verified mainland China game index.");
        Require(requests.Origins.SequenceEqual([Primary, Primary, Mirror, Mirror, Cdn]),
            "A changed release version must try the mirror without fetching its mismatched index.");

        try
        {
            _ = await ChinaGameReleaseSource.LoadAsync(http, new CancellationToken(canceled: true), () => null);
            throw new InvalidOperationException("A canceled release query started publisher configuration discovery.");
        }
        catch (OperationCanceledException) { }
        Require(requests.Origins.Count == 5, "A canceled release query must not issue bootstrap requests.");
    }

    private sealed class ReleaseFixture(byte[] index) : HttpMessageHandler
    {
        internal List<string> Origins { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            Origins.Add(uri.IdnHost);
            if (uri.IdnHost == Cdn)
            {
                Require(uri.AbsolutePath == "/releases/index.txt" && !request.Headers.Contains("sign"),
                    "CDN requests must use the verified release path and omit API signing headers.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(index) });
            }

            const string summaryPath = "/pc-launcher/client/gameversion/config";
            const string packagePath = "/pc-launcher/client/gameversion/packageconfig";
            bool isPackage = uri.AbsolutePath == packagePath;
            string query = $"launcherId={LauncherId}{(isPackage ? "&uid=" : string.Empty)}";
            Require((uri.AbsolutePath is summaryPath or packagePath) && uri.Query == $"?{query}" &&
                !request.Headers.Contains("Authorization") && !request.Headers.Contains("utoken"),
                "The source must call the publisher's guest release routes without account context.");
            string timestamp = request.Headers.GetValues("timestamp").Single();
            string signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + query + "354C000" + TestSecret)));
            Require(request.Headers.GetValues("sign").Single() == signature &&
                request.Headers.GetValues("grp-channel-code").Single() == "C000",
                "The source must use the correct publisher application signature and channel.");

            string version = uri.IdnHost == Primary && isPackage ? "1.0.0.30" : "1.0.0.31";
            string digest = Convert.ToHexStringLower(MD5.HashData(index));
            string json = isPackage
                ? $$"""
                    { "code": 0, "data": { "released": { "versionCode": "{{version}}", "domainList": ["https://syncstation.manjuu.com/"],
                      "gamePackageList": [{ "packageType": 2, "fileSize": {{index.Length}}, "fileMd5": "{{digest}}", "downloadUrl": "/releases/index.txt" }] } } }
                    """
                : $$"""
                    { "code": 0, "data": { "released": { "versionCode": "{{version}}", "gameFolderName": "AzurPromilia_Game", "launchEntry": "AzurPromilia.exe" } } }
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
