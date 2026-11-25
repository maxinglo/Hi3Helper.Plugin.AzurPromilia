using System.Globalization;
using System.Net;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class KoreaLauncherContentSource
{
    private const string Site = "https://azurpromilia.nexon.com";
    private const string Icons = "https://azurpromilia-web.dn.nexoncdn.co.kr/home_73685DA/common/";

    internal static async Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token)
    {
        LauncherSocialItem[] social =
        [
            new("YouTube", "https://www.youtube.com/@azurpromiliaKR", Icons + "btn_youtube.png"),
            new("X", "https://x.com/azurpromiliaKR", Icons + "btn_x.png")
        ];
        try
        {
            using HttpResponseMessage response = await http.GetAsync(Site + "/api/home/sticky-news", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("stickyThreads", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                return new([], [], social, NewsLoaded: false, CarouselLoaded: false);

            var news = new List<LauncherNewsItem>();
            var carousel = new List<LauncherCarouselItem>();
            bool malformed = false;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    malformed = true;
                    continue;
                }
                if (Flag(row, "isDelete") || Flag(row, "isWebHide"))
                    continue;
                string id = Text(row, "threadId");
                string title = WebUtility.HtmlDecode(Text(row, "title"));
                if (id.Length == 0 || !id.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(title))
                {
                    malformed = true;
                    continue;
                }
                string board = Text(row, "boardId");
                string path = board switch { "6811" => "event", "6809" => "chronicle", "8058" => "update", "6858" => "gallery", _ => "notice" };
                string url = $"{Site}/news/{path}/{id}";
                string external = Text(row, "externalLink");
                if (IsHttps(external)) url = external;
                string date = "";
                if (row.TryGetProperty("createDate", out JsonElement value) && value.TryGetInt64(out long timestamp))
                {
                    try { date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(9)).ToString("MM/dd", CultureInfo.InvariantCulture); }
                    catch (ArgumentOutOfRangeException) { }
                }
                LauncherNewsEntryType type = board switch { "6811" => LauncherNewsEntryType.Event, "6808" => LauncherNewsEntryType.Notice, _ => LauncherNewsEntryType.Info };
                news.Add(new(title, url, date, type));
                string image = Text(row, "thumbnailImageUrl");
                if (IsHttps(image)) carousel.Add(new(image, url, title));
            }
            bool loaded = !malformed || news.Count != 0;
            return new(news.ToArray(), carousel.ToArray(), social, loaded, loaded);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return new([], [], social, NewsLoaded: false, CarouselLoaded: false);
        }
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
}
