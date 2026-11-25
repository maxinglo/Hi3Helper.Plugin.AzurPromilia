using System.Net;
using System.Text.Json;
using Hi3Helper.Plugin.AzurPromilia;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;

internal static class LauncherBackgroundTests
{
    private const string Cdn = "https://l1-all-plat-server.manjuu.com/azurpromilia/";

    internal static async Task RunAsync()
    {
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("AzurPromiliaBackground-");
        string? verifiedRoot = temporary.FullName;
        const string fallbackStill = Cdn + "img/fallback.png";
        const string fallbackVideo = Cdn + "video/fallback.mp4";
        var handler = new BackgroundHandler();
        string downloadedMedia = Path.Combine(temporary.FullName, "downloaded");
        var media = new OfficialLauncherMedia(fallbackStill, fallbackVideo, () => verifiedRoot,
            new HttpClient(handler), downloadedMedia);
        try
        {
            string cacheDirectory = Path.Combine(temporary.FullName, "AppData", "data");
            Directory.CreateDirectory(cacheDirectory);
            string cache = Path.Combine(cacheDirectory, "cache.json");

            const string firstStill = Cdn + "img/current/静态.png";
            const string firstVideo = Cdn + "video/current/动态.mp4";
            await WriteCacheAsync(cache, firstStill, firstVideo);
            Require(GetBackgroundPaths(media, firstStill) == (firstVideo, firstStill),
                "Before initialization the verified launcher still and video must be available as URLs.");
            await InitializeAsync(media);
            var first = VerifyPreparedBackground(media, firstStill, firstVideo);
            await InitializeAsync(media);
            Require(VerifyPreparedBackground(media, firstStill, firstVideo) == first && handler.Requests == 2,
                "Refreshing the same official selection must reuse its prepared local files.");

            const string nextStill = Cdn + "img/next/still.jpg";
            const string nextVideo = Cdn + "video/next/motion.webm";
            await WriteCacheAsync(cache, nextStill, nextVideo);
            Require(VerifyPreparedBackground(media, firstStill, firstVideo) == first,
                "A launcher cache update must not change the selected assets until initialization refreshes them.");
            await InitializeAsync(media);
            var next = VerifyPreparedBackground(media, nextStill, nextVideo);
            Require(next != first && handler.Requests == 4, "A new selection must download its current assets.");

            const string incomplete = Cdn + "video/next/incomplete.mp4";
            await WriteCacheAsync(cache, nextStill, incomplete);
            await InitializeAsync(media);
            Require(GetBackgroundPaths(media, nextStill) == (next.Still, incomplete) &&
                    Directory.GetFiles(downloadedMedia).Length == 4,
                "An incomplete video must keep the still usable and leave no published file or temporary payload.");

            foreach ((string? still, string? video) in new (string?, string?)[]
            {
                (nextStill, null),
                ("https://untrusted.example/azurpromilia/img/test.png", nextVideo),
                ("https://user@l1-all-plat-server.manjuu.com/azurpromilia/img/test.png", nextVideo),
                (nextStill + "#fragment", nextVideo),
                (nextStill, nextVideo + "?private=value"),
                (Cdn + "img/still.svg", nextVideo),
                (nextStill, Cdn + "video/movie.png"),
                (Cdn + "unrelated/test.png", nextVideo)
            })
            {
                await WriteCacheAsync(cache, still, video);
                await InitializeAsync(media);
                VerifyPreparedBackground(media, fallbackStill, fallbackVideo);
            }

            await File.WriteAllTextAsync(cache, "{");
            await InitializeAsync(media);
            VerifyPreparedBackground(media, fallbackStill, fallbackVideo);

            verifiedRoot = null;
            await WriteCacheAsync(cache, nextStill, nextVideo);
            await InitializeAsync(media);
            VerifyPreparedBackground(media, fallbackStill, fallbackVideo);
            Require(handler.Requests == 7, "Invalid launcher cache URLs must not be requested and the public fallbacks must be reused.");
        }
        finally
        {
            media.Dispose();
            temporary.Delete(recursive: true);
        }
    }

    private static Task WriteCacheAsync(string file, string? still, string? video) =>
        File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { bgUrl = still, videoUrl = video }));

    private static async Task InitializeAsync(OfficialLauncherMedia media)
    {
        Guid token = Guid.CreateVersion7();
        media.InitAsync(in token, out nint init);
        Require(await init.AsTask<int>().WaitAsync(TimeSpan.FromSeconds(10)) == 0, "The background API must initialize.");
    }

    private static (string Video, string Still) VerifyPreparedBackground(OfficialLauncherMedia media, string stillUrl, string videoUrl)
    {
        var (video, still) = GetBackgroundPaths(media, stillUrl);
        VerifyPreparedAsset(video, videoUrl);
        VerifyPreparedAsset(still, stillUrl);
        return (video, still);
    }

    private static void VerifyPreparedAsset(string path, string source)
    {
        Require(Path.IsPathFullyQualified(path) && new Uri(path).LocalPath == path && !Path.GetFileName(path).Contains('%'),
            "Collapse must be able to round-trip the cache path through a file URI without changing the filename.");
        Require(Path.GetExtension(path) == Path.GetExtension(new Uri(source).AbsolutePath),
            "Collapse must recognize the background type from its file extension.");
        Require(File.ReadAllText(path) == new Uri(source).AbsoluteUri, "The local file must contain the selected asset's response.");
    }

    private static (string First, string Second) GetBackgroundPaths(OfficialLauncherMedia media, string poster)
    {
        Require(media.PosterUrl == poster, "The preset poster must expose the active publisher URL.");
        media.GetBackgroundFlag(out LauncherBackgroundFlag flag);
        Require(flag == (LauncherBackgroundFlag.TypeIsVideo | LauncherBackgroundFlag.IsSourceFile),
            "The media provider must continue to report video support.");
        using var entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
        Require(entries.Length == 2, "China must always return a video and a separately selectable static background.");
        using LauncherPathEntry first = entries[0];
        using LauncherPathEntry second = entries[1];
        return (first.Path ?? throw new InvalidOperationException("The first background path is unavailable."),
            second.Path ?? throw new InvalidOperationException("The second background path is unavailable."));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class BackgroundHandler : HttpMessageHandler
    {
        private int _requests;

        internal int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            Uri uri = request.RequestUri!;
            var content = new StringContent(uri.AbsoluteUri);
            if (uri.AbsolutePath.EndsWith("/incomplete.mp4", StringComparison.Ordinal))
                content.Headers.ContentLength = 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
