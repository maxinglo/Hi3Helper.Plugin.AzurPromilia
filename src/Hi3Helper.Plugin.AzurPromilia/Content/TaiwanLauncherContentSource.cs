namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class TaiwanLauncherContentSource
{
    private static readonly LauncherSocialItem[] Social =
    [
        new("Facebook", "https://www.facebook.com/AzurPromiliaTC", "", IconData: OfficialSocialIcons.Facebook),
        new("Instagram", "https://www.instagram.com/azurpromilia.official", "", IconData: OfficialSocialIcons.Instagram),
        new("YouTube", "https://www.youtube.com/@AzurPromiliaTC", "", IconData: OfficialSocialIcons.YouTube),
        // Keep Taiwan Discord available without a network icon request.
        new("Discord", "https://discord.gg/U34m2k2RkC", "", IconData: OfficialSocialIcons.Discord)
    ];

    private static readonly ManjuuWebsiteContentSource Source = new(
        "https://api-grp-rgn1.jimi-global.com", "https://azurpromilia.jimi-global.com", "zh-tw",
        "/announce/web/carousel/list", "pcCoverImageUrl", Social, useArticleImagesWhenCarouselIsEmpty: true);

    internal static Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token) =>
        Source.LoadAsync(http, token);
}
