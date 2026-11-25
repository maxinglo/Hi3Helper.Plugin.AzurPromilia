using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class GameIndexTests
{
    private const string Md5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string EmptyDigest = "00000000000000000000000000000000";

    internal static void Run()
    {
        string text = $"3,2,1\r\n0,0,{EmptyDigest},Data,\r\n" +
            $"1,3,{Md5.ToUpperInvariant()},Data\\data.r,objects/data.r\r\n" +
            $"2,0,{EmptyDigest},Data/空文件,objects/empty\r\n";
        ManjuuGameIndex manifest = Read(text);
        Require(manifest.TotalBytes == 3 && manifest.Directories.SequenceEqual(["Data"]) &&
            manifest.Files[0] == new GameFile(3, Md5, "Data/data.r", "objects/data.r") &&
            manifest.Files[1].InstallPath == "Data/空文件", "The game index must retain sizes, hashes and normalized paths.");

        using var cipher = Aes.Create();
        cipher.KeySize = 192;
        cipher.GenerateKey();
        cipher.GenerateIV();
        byte[] cipherText = cipher.EncryptCbc(Encoding.UTF8.GetBytes(text), cipher.IV, PaddingMode.PKCS7);
        using var encrypted = new MemoryStream(Encoding.ASCII.GetBytes(Convert.ToBase64String(cipherText)));
        ManjuuGameIndex decrypted = ManjuuGameIndex.ReadEncrypted(encrypted, cipher.Key, cipher.IV);
        Require(decrypted.TotalBytes == manifest.TotalBytes && decrypted.Files.SequenceEqual(manifest.Files) &&
            decrypted.Directories.SequenceEqual(manifest.Directories), "Decoded index contents must be read without changing any entries.");

        foreach (string invalid in new[]
        {
            $"1,1,0\n0,3,{Md5},../outside,objects/data.r",
            $"1,1,0\n0,3,{Md5},C:\\outside,objects/data.r",
            $"1,1,0\n0,3,{Md5},Data/aux.txt,objects/data.r",
            $"1,1,0\n0,3,{Md5},Data/asset.r,https://invalid.example/asset.r",
            $"1,1,0\n0,3,{Md5},Data/asset.r,objects/%2e%2e/asset.r",
            $"1,1,0\n0,3,{Md5},.collapse-azurpromilia/temp,objects/data.r",
            $"2,1,1\n0,0,{EmptyDigest},Data,\n1,3,{Md5},data,objects/data.r",
            $"2,2,0\n0,3,{Md5},Data,objects/data.r\n1,3,{Md5},Data/child,objects/child.r",
            $"2,2,0\n0,{long.MaxValue},{Md5},Data/a,objects/a\n1,1,{Md5},Data/b,objects/b",
            $"1,1,0\n2,3,{Md5},Data/asset.r,objects/data.r",
            $"1,1,0\n0,3,{Md5},Data/asset.r,objects/data.r\n1,3,{Md5},Data/other.r,objects/other.r"
        })
        {
            try
            {
                Read(invalid);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            throw new InvalidOperationException("A malformed game index was accepted.");
        }
    }

    internal static async Task RunReleaseAsync()
    {
        using var cipher = Aes.Create();
        cipher.KeySize = 192;
        cipher.GenerateKey();
        cipher.GenerateIV();
        byte[] plain = Encoding.UTF8.GetBytes($"1,1,0\n0,3,{Md5},AzurPromilia.exe,objects/game.exe\n");
        byte[] payload = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            cipher.EncryptCbc(plain, cipher.IV, PaddingMode.PKCS7)));
        string digest = Convert.ToHexString(MD5.HashData(payload));
        using var handler = new IndexFixture(payload);
        using var http = new HttpClient(handler);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "primary.example", "mirror.example", "spare.example" };
        using JsonDocument configuration = JsonDocument.Parse($$"""
            {
              "code": 0,
              "data": {
                "released": { "versionCode": "1.0.0.29" },
                "preload": {
                  "versionCode": "1.0.0.30",
                  "domainList": ["https://unlisted.example/", "https://primary.example/prod", "https://primary.example/prod/",
                                 "https://mirror.example:8443/game/", "https://mirror.example/game/", "https://spare.example/assets"],
                  "gameExcludedFileList": ["AzurPromilia.exe"],
                  "gamePackageList": [
                    { "packageType": 1, "downloadUrl": "slice.bin" },
                    { "packageType": 2, "downloadUrl": "/index/1.0.0.30", "fileMd5": "{{digest}}", "fileSize": {{payload.Length}} }
                  ]
                }
              }
            }
            """);
        GameInstallPlan plan = await ManjuuReleaseIndex.LoadAsync(http, "AzurPromiliaCN", configuration.RootElement,
            preload: true, hosts, cipher.Key, cipher.IV, CancellationToken.None);
        Require(handler.Requests.SequenceEqual(["https://primary.example/prod/index/1.0.0.30", "https://mirror.example/game/index/1.0.0.30"]) &&
            plan.Profile == "AzurPromiliaCN" && plan.Version == new Version(1, 0, 0, 30) &&
            plan.BaseUris.SequenceEqual([new Uri("https://mirror.example/game/"), new Uri("https://primary.example/prod/"),
                new Uri("https://spare.example/assets/")]) &&
            plan.IndexSha256 == Convert.ToHexStringLower(SHA256.HashData(payload)) &&
            plan.Files.SequenceEqual([new GameFile(3, Md5, "AzurPromilia.exe", "objects/game.exe")]),
            "The verified index source must lead the configured, allowed CDN roots without duplicate or unapproved roots.");

        foreach (var response in new[] { default(JsonElement), configuration.RootElement })
        {
            try
            {
                _ = await ManjuuReleaseIndex.LoadAsync(http, "AzurPromiliaCN", response, preload: false, hosts,
                    cipher.Key, cipher.IV, CancellationToken.None);
                throw new InvalidOperationException("A missing release index unexpectedly produced an installation plan.");
            }
            catch (InvalidDataException) { }
        }
        Require(handler.Requests.Count == 2, "A missing release index must not contact any CDN.");
    }

    private sealed class IndexFixture(byte[] index) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(uri.AbsoluteUri);
            return Task.FromResult(uri.Host switch
            {
                "primary.example" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[index.Length]) },
                "mirror.example" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(index) },
                _ => throw new InvalidOperationException("The release index used an unregistered CDN.")
            });
        }
    }

    private static ManjuuGameIndex Read(string text)
    {
        using var reader = new StringReader(text);
        return ManjuuGameIndex.Read(reader);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
