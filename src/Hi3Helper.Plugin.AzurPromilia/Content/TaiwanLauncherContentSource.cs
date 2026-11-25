namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class TaiwanLauncherContentSource
{
    private static readonly LauncherSocialItem[] Social =
    [
        new("Facebook", "https://www.facebook.com/AzurPromiliaTC", "https://www.facebook.com/favicon.ico"),
        new("Instagram", "https://www.instagram.com/azurpromilia.official", "https://www.instagram.com/favicon.ico"),
        new("YouTube", "https://www.youtube.com/@AzurPromiliaTC", "https://www.youtube.com/favicon.ico"),
        // Bundle the Taiwan website's Discord artwork so the social button does not depend on a favicon request.
        new("Discord", "https://discord.gg/U34m2k2RkC", "", IconData: OfficialSocialIcons.Discord)
    ];

    private static readonly ManjuuWebsiteContentSource Source = new(
        "https://api-grp-rgn1.jimi-global.com", "https://azurpromilia.jimi-global.com", "zh-tw",
        "/announce/web/carousel/list", "pcCoverImageUrl", Social, useArticleImagesWhenCarouselIsEmpty: true);

    internal static Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token) =>
        Source.LoadAsync(http, token);
}
