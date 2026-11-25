using Hi3Helper.Plugin.AzurPromilia;
using Hi3Helper.Plugin.AzurPromilia.Content;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;

internal static class LauncherContentTests
{
    internal static async Task RunAsync()
    {
        const string Site = "https://example.invalid/";
        var snapshot = new LauncherContentSnapshot(
            [
                new("Maintenance", Site + "notice", "11/25", LauncherNewsEntryType.Notice),
                new("Duplicate", Site + "notice", "11/25", LauncherNewsEntryType.Info),
                new("Malformed", "file:///C:/launcher.exe", "", LauncherNewsEntryType.Info)
            ],
            [
                new(Site + "events.png", Site + "event", "Current event"),
                new(Site + "illustration.png", "", "Announcement artwork"),
                new(Site + "untrusted.png", "javascript:open()", "Invalid target")
            ],
            [
                new("Community", Site + "community", Site + "icon.png", Site + "hover.png"),
                new("QR channel", "", Site + "qr-icon.png", QrImageUrl: Site + "qr.png"),
                new("Invalid icon", Site + "invalid", "http://example.invalid/icon.png")
            ]);
        int requests = 0;
        var api = new OfficialLauncherNews("Fixture", (_, _) => ++requests switch
        {
            1 => Task.FromResult(snapshot),
            2 => Task.FromException<LauncherContentSnapshot>(new HttpRequestException("Simulated outage")),
            _ => Task.FromResult(new LauncherContentSnapshot([], [], snapshot.Social, NewsLoaded: false))
        });
        try
        {
            await InitAsync(api);
            await InitAsync(api);
            Require(requests == 2, "Content should refresh on each initialization.");

            using var news = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(api.GetNewsEntries);
            Require(news.Length == 1, "Duplicate and non-HTTPS news must not be marshalled.");
            using LauncherNewsEntry notice = news[0];
            Require(notice.Url == Site + "notice" && notice.Title == "Maintenance" && notice.PostDate == "11/25"
                && notice.Type == LauncherNewsEntryType.Notice, "Notice metadata did not survive the ABI export.");

            using var carousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(api.GetCarouselEntries);
            Require(carousel.Length == 2, "Carousel must retain official banners without click targets.");
            using LauncherCarouselEntry eventBanner = carousel[0];
            using LauncherCarouselEntry staticBanner = carousel[1];
            Require(eventBanner.ClickUrl == Site + "event" && eventBanner.ImageUrl == Site + "events.png"
                && staticBanner.ClickUrl == Site + "illustration.png", "Carousel click targets were marshalled incorrectly.");

            using var social = PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(api.GetSocialMediaEntries);
            Require(social.Length == 2, "Social entries with unsafe assets must be removed.");
            using LauncherSocialMediaEntry community = social[0];
            using LauncherSocialMediaEntry qr = social[1];
            Require(community.ClickUrl == Site + "community" && community.IconHoverPath == Site + "hover.png"
                && community.Flags == (LauncherSocialMediaEntryFlag.IconIsPath | LauncherSocialMediaEntryFlag.IconHoverIsPath),
                "Community entry was not marshalled.");
            Require(qr.ClickUrl is null && qr.QrPath == Site + "qr.png"
                && qr.Flags == (LauncherSocialMediaEntryFlag.IconIsPath | LauncherSocialMediaEntryFlag.QrImageIsPath),
                "QR-only entry was not marshalled.");

            await InitAsync(api);
            using var retainedNews = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(api.GetNewsEntries);
            using var emptyCarousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(api.GetCarouselEntries);
            Require(retainedNews.Length == 1 && emptyCarousel.Length == 0,
                "A failed feed must retain prior entries while a successful empty feed replaces them.");
            using LauncherNewsEntry retainedNotice = retainedNews[0];
            Require(retainedNotice.Url == Site + "notice", "The previously loaded notice was lost.");
        }
        finally
        {
            api.Free();
        }
    }

    private static async Task InitAsync(ILauncherApiNews api)
    {
        Guid token = Guid.CreateVersion7();
        api.InitAsync(in token, out nint pending);
        Require(await pending.AsTask<int>().WaitAsync(TimeSpan.FromSeconds(10)) == 0,
            "An offline content feed must not prevent the game page from initializing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
