using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class ChinaLauncherBootstrapTests
{
    internal static async Task RunAsync()
    {
        using var http = new HttpClient(new SiteFixture());
        Uri download = await ChinaLauncherBootstrap.FindDownloadAsync(http, CancellationToken.None);
        Require(download.AbsoluteUri == "https://syncstation.manjuu.com/azurpromilia/launcher/c000/test.exe",
            "The website download must reject another region and use its verified mainland China mirror.");

        using var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("res/app_config.dat").Open())
            entry.Write("synthetic config"u8);

        byte[] zip = payload.ToArray();
        using var installer = new MemoryStream();
        installer.Write(new byte[127]);
        installer.Write(zip);
        installer.Write(new byte[300_000]);
        long signedLength = installer.Length;
        installer.Write(zip);
        Require(ChinaLauncherBootstrap.LocateEmbeddedArchive(installer, signedLength) == (127L, (long)zip.Length),
            "Exclude archive signatures outside the Authenticode payload.");

        try
        {
            _ = ChinaLauncherBootstrap.LocateEmbeddedArchive(installer, 127);
            throw new InvalidOperationException("The archive reader accepted an executable with no embedded payload.");
        }
        catch (InvalidDataException) { }
    }

    private sealed class SiteFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            string timestamp = request.Headers.GetValues("timestamp").Single();
            Require(uri.PathAndQuery == "/activity/web/download/info?gameId=35" &&
                request.Headers.GetValues("sign").Single() == Convert.ToHexStringLower(
                    MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "gameId=35"))),
                "Use the publisher website's anonymous download request.");

            string channel = uri.IdnHost == "api-grp.manjuu.com" ? "c500" : "c000";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    { "code": 0, "data": { "nativeLauncherDownloadUrl": "https://syncstation.manjuu.com/azurpromilia/launcher/{{channel}}/test.exe" } }
                    """)
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
