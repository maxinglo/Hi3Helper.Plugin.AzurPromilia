using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal sealed record LauncherContentSnapshot(
    LauncherNewsItem[] News, LauncherCarouselItem[] Carousel, LauncherSocialItem[] Social,
    bool NewsLoaded = true, bool CarouselLoaded = true)
{
    internal static LauncherContentSnapshot Empty { get; } = new([], [], []);
}

internal sealed record LauncherNewsItem(
    string Title, string Url, string Date, LauncherNewsEntryType Type, string Description = "");

internal sealed record LauncherCarouselItem(string ImageUrl, string Url, string Description);

internal sealed record LauncherSocialItem(
    string Name, string Url, string IconUrl, string? HoverIconUrl = null, string? QrImageUrl = null);
