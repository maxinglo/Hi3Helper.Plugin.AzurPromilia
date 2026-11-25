using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class ChinaLauncherContentParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 16 };
    private static readonly (string Key, LauncherNewsEntryType Type)[] NewsCategories =
    [
        ("1", LauncherNewsEntryType.Info),
        ("3", LauncherNewsEntryType.Event),
        ("2", LauncherNewsEntryType.Notice)
    ];

    internal static LauncherContentSnapshot? Parse(byte[] body)
    {
        using JsonDocument document = JsonDocument.Parse(body, DocumentOptions);
        JsonElement root = document.RootElement;
        if (!TryGet(root, "code", JsonValueKind.Number, out JsonElement code) || !code.TryGetInt32(out int result) || result != 0 ||
            !TryGet(root, "data", JsonValueKind.Object, out JsonElement data) ||
            !TryGet(data, "txtContentMap", JsonValueKind.Object, out JsonElement text) ||
            !TryGet(data, "mediaContentMap", JsonValueKind.Object, out JsonElement media))
            return null;

        var news = new List<LauncherNewsItem>();
        foreach ((string key, LauncherNewsEntryType type) in NewsCategories)
            if (!ReadItems(text, key, row => ParseNews(row, type), news))
                return null;

        var carousel = new List<LauncherCarouselItem>();
        var social = new List<LauncherSocialItem>();
        return ReadItems(media, "2", ParseCarousel, carousel) && ReadItems(media, "11", ParseSocial, social)
            ? new([.. news], [.. carousel], [.. social])
            : null;
    }

    private static bool ReadItems<T>(JsonElement map, string key, Func<JsonElement, T?> parse, List<T> destination)
        where T : class
    {
        if (!map.TryGetProperty(key, out JsonElement rows))
            return true;
        if (rows.ValueKind != JsonValueKind.Array)
            return false;

        var items = new List<(int Sort, T Item)>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (parse(row) is not { } item)
                return false;
            int sort = TryGet(row, "sort", JsonValueKind.Number, out JsonElement order) && order.TryGetInt32(out int value)
                ? value : int.MaxValue;
            items.Add((sort, item));
        }

        destination.AddRange(items.OrderBy(item => item.Sort).Select(item => item.Item));
        return true;
    }

    private static LauncherNewsItem? ParseNews(JsonElement row, LauncherNewsEntryType type)
    {
        string title = Text(row, "title");
        string date = Text(row, "publishDate");
        return title.Length != 0 && date.Length != 0 && Destination(Text(row, "linkUrl")) is { } url
            ? new(title, url, date, type)
            : null;
    }

    private static LauncherCarouselItem? ParseCarousel(JsonElement row)
    {
        string link = Text(row, "linkUrl");
        string? destination = Destination(link);
        return Image(Text(row, "image")) is { } image && (destination is not null || link.Length == 0)
            ? new(image, destination ?? "", Text(row, "title"))
            : null;
    }

    private static LauncherSocialItem? ParseSocial(JsonElement row)
    {
        string? icon = Image(Text(row, "icon"));
        string? qr = Image(Text(row, "image"));
        string? url = Destination(Text(row, "linkUrl"));
        if (icon is null || (qr is null && url is null))
            return null;

        string title = Text(row, "title");
        if (title.Length == 0)
            title = SocialName(icon, qr);
        return new(title, url ?? "", icon, QrImageUrl: qr);
    }

    private static string SocialName(string icon, string? qr)
    {
        string file = Uri.UnescapeDataString(Path.GetFileName(icon));
        if (file.Contains("bilibili", StringComparison.OrdinalIgnoreCase))
            return "哔哩哔哩";
        if (file.Contains("weibo", StringComparison.OrdinalIgnoreCase))
            return "微博";
        if (file.Contains("weixin", StringComparison.OrdinalIgnoreCase))
            return "微信公众号";
        if (qr is not null && Uri.UnescapeDataString(Path.GetFileName(qr)).Contains("抖音", StringComparison.Ordinal))
            return "抖音";
        return "官方社区";
    }

    private static string? Destination(string value)
    {
        Uri? uri = PublicUri(value);
        return uri?.IdnHost is "azurpromilia.manjuu.com" or "pay.manjuu.com" or "weibo.com" or "www.weibo.com"
            or "space.bilibili.com" or "www.bilibili.com" or "v.douyin.com" or "www.douyin.com"
            or "l.taptap.cn" or "www.taptap.cn" or "d.3839.com"
            ? uri.AbsoluteUri
            : null;
    }

    private static string? Image(string value)
    {
        Uri? uri = PublicUri(value);
        return uri?.IdnHost is "l1-all-plat-server.manjuu.com" &&
            uri.AbsolutePath.StartsWith("/azurpromilia/img/", StringComparison.Ordinal) &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif"
            ? uri.AbsoluteUri
            : null;
    }

    private static Uri? PublicUri(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains('\\') && !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsDefaultPort && uri.UserInfo.Length == 0
            ? uri : null;

    private static string Text(JsonElement element, string name) =>
        TryGet(element, name, JsonValueKind.String, out JsonElement value) ? value.GetString()?.Trim() ?? "" : "";

    private static bool TryGet(JsonElement element, string name, JsonValueKind kind, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == kind;
    }
}
