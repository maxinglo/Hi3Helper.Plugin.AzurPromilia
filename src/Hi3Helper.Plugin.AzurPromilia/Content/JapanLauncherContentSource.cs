using System.Globalization;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class JapanLauncherContentSource
{
    private const string Api = "https://azurpromilia.jp/official/api/webcms/official?key=";

    internal static async Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token)
    {
        var news = LoadNewsAsync(http, token);
        var carousel = LoadCarouselAsync(http, token);
        await Task.WhenAll(news, carousel).ConfigureAwait(false);
        var (newsItems, newsLoaded) = await news.ConfigureAwait(false);
        var (carouselItems, carouselLoaded) = await carousel.ConfigureAwait(false);
        return new LauncherContentSnapshot(newsItems, carouselItems,
        [
            new("X", "https://x.com/AzurPromilia_JP?lang=ja", "", IconData: OfficialSocialIcons.X),
            new("YouTube", "https://www.youtube.com/@AzurPromilia_JP", "", IconData: OfficialSocialIcons.YouTube),
            // Keep the Japanese launcher's customer support entry beside X and YouTube.
            new("お問い合わせ", "https://www.azurpromilia.jp/contact", "", IconData: OfficialSocialIcons.JapanSupport)
        ], newsLoaded, carouselLoaded);
    }

    private static async Task<(LauncherNewsItem[] Items, bool Loaded)> LoadNewsAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using JsonDocument data = await GetAsync(http, Api + "news&index=1&size=20", token).ConfigureAwait(false);
            if (!TryGetRows(data, out JsonElement rows)) return ([], false);

            var items = new List<LauncherNewsItem>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out JsonElement id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long idValue) || idValue <= 0)
                    continue;
                string title = Text(row, "title");
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                string date = "";
                if (row.TryGetProperty("publishTime", out JsonElement published) && published.TryGetInt64(out long timestamp))
                {
                    try { date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToOffset(TimeSpan.FromHours(9)).ToString("MM/dd", CultureInfo.InvariantCulture); }
                    catch (ArgumentOutOfRangeException) { }
                }
                LauncherNewsEntryType type = Text(row, "type") switch
                {
                    "notice" => LauncherNewsEntryType.Notice,
                    "activity" => LauncherNewsEntryType.Event,
                    _ => LauncherNewsEntryType.Info
                };
                items.Add(new LauncherNewsItem(title, $"https://azurpromilia.jp/news/{idValue}", date, type));
            }
            return (items.ToArray(), items.Count != 0 || rows.GetArrayLength() == 0);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return ([], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return ([], false);
        }
    }

    private static async Task<(LauncherCarouselItem[] Items, bool Loaded)> LoadCarouselAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using JsonDocument data = await GetAsync(http, Api + "slideshow", token).ConfigureAwait(false);
            if (!TryGetRows(data, out JsonElement rows)) return ([], false);

            var items = new List<LauncherCarouselItem>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("bigImage", out JsonElement image))
                    continue;
                string imageUrl = image.ValueKind switch
                {
                    JsonValueKind.String => image.GetString() ?? "",
                    JsonValueKind.Array when image.GetArrayLength() > 0 && image[0].ValueKind == JsonValueKind.String => image[0].GetString() ?? "",
                    _ => ""
                };
                string url = Text(row, "urlLink");
                if (!IsHttps(imageUrl) || (url.Length != 0 && !IsHttps(url)))
                    continue;
                items.Add(new LauncherCarouselItem(imageUrl, url, Text(row, "title")));
            }
            return (items.ToArray(), items.Count != 0 || rows.GetArrayLength() == 0);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return ([], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return ([], false);
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(6));
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(request.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: request.Token).ConfigureAwait(false);
    }

    private static bool TryGetRows(JsonDocument document, out JsonElement rows)
    {
        rows = default;
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out JsonElement code) &&
            code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int result) && result == 0 &&
            root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("rows", out rows) && rows.ValueKind == JsonValueKind.Array;
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;
}
