namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class ChinaLauncherContentSource
{
    private const string AssetBase = "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/home/assets/";
    private static readonly LauncherSocialItem[] Social =
    [
        new("微博", "https://weibo.com/u/7832445927", AssetBase + "weibo-BLhdyCSZ.png", AssetBase + "weibo-hover-NHMcYBuf.png"),
        new("TapTap", "https://l.taptap.cn/yANLQDH3?channel=rep-rep_xjkzxfe8mjr", AssetBase + "tap-DoxMh1tE.png", AssetBase + "tap-hover-CTGjNvpV.png"),
        new("哔哩哔哩", "https://space.bilibili.com/3546569016085336", AssetBase + "bilibili-DRyh522N.png", AssetBase + "bilibili-hover-CRokrJ4b.png"),
        new("抖音", "https://v.douyin.com/hGo2FaBmk9M/", AssetBase + "douyin-jv4hbaZm.png", AssetBase + "douyin-hover-KBsntWi_.png"),
        new("好游快爆", "https://d.3839.com/ErB", AssetBase + "hykb-CbzeVX5o.png", AssetBase + "hykb-hover-C54mARG7.png")
    ];

    private static readonly ManjuuWebsiteContentSource Source = new(
        "https://api-grp.manjuu.com", "https://azurpromilia.manjuu.com", "zh",
        "/announce/web/resource/listbanner", "imageUrl", Social);

    internal static Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token) =>
        Source.LoadAsync(http, token);

    internal static async Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token,
        Func<string?> findVerifiedLauncherDirectory)
    {
        LauncherContentSnapshot? cached = await Task.Run(
            () => ChinaLauncherContentCache.Read(findVerifiedLauncherDirectory, token), token).ConfigureAwait(false);
        return cached ?? await LoadAsync(http, token).ConfigureAwait(false);
    }
}
