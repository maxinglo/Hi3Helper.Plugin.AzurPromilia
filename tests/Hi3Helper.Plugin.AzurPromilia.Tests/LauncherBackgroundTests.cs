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
        var media = new OfficialLauncherMedia(fallbackStill, fallbackVideo, () => verifiedRoot);
        try
        {
            string cacheDirectory = Path.Combine(temporary.FullName, "AppData", "data");
            Directory.CreateDirectory(cacheDirectory);
            string cache = Path.Combine(cacheDirectory, "cache.json");

            const string firstStill = Cdn + "img/current/静态.png";
            const string firstVideo = Cdn + "video/current/动态.mp4";
            await WriteCacheAsync(cache, firstStill, firstVideo);
            VerifyBackground(media, firstStill, firstVideo);

            const string nextStill = Cdn + "img/next/still.jpg";
            const string nextVideo = Cdn + "video/next/motion.webm";
            await WriteCacheAsync(cache, nextStill, nextVideo);
            VerifyBackground(media, firstStill, firstVideo);
            await InitializeAsync(media);
            VerifyBackground(media, nextStill, nextVideo);

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
                VerifyBackground(media, fallbackStill, fallbackVideo);
            }

            await File.WriteAllTextAsync(cache, "{");
            await InitializeAsync(media);
            VerifyBackground(media, fallbackStill, fallbackVideo);

            verifiedRoot = null;
            await WriteCacheAsync(cache, nextStill, nextVideo);
            await InitializeAsync(media);
            VerifyBackground(media, fallbackStill, fallbackVideo);
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

    private static void VerifyBackground(OfficialLauncherMedia media, string still, string video)
    {
        Require(media.PosterUrl == still, "The poster must expose the active static URL, including before the media API initializes.");
        using var entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
        Require(entries.Length == 2, "China must always return a video and a separately selectable static background.");
        using LauncherPathEntry first = entries[0];
        using LauncherPathEntry second = entries[1];
        Require(first.Path == video && second.Path == still, "The active video and poster must come from the same selection.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
