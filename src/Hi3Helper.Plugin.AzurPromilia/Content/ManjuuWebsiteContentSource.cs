using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal sealed class ManjuuWebsiteContentSource(
    string apiBase, string siteBase, string language,
    string bannerPath, string bannerImageField, LauncherSocialItem[] social,
    bool useArticleImagesWhenCarouselIsEmpty = false)
{
    internal async Task<LauncherContentSnapshot> LoadAsync(HttpClient http, CancellationToken token)
    {
        Task<NewsFeed?> newsTask = LoadNewsAsync(http, token);
        Task<LauncherCarouselItem[]?> bannersTask = LoadBannersAsync(http, token);
        await Task.WhenAll(newsTask, bannersTask).ConfigureAwait(false);

        NewsFeed? news = await newsTask.ConfigureAwait(false);
        LauncherCarouselItem[]? banners = await bannersTask.ConfigureAwait(false);
        if (useArticleImagesWhenCarouselIsEmpty && banners is { Length: 0 })
            banners = news?.ArticleImages;

        return new(news?.Items ?? [], banners ?? [], social, NewsLoaded: news is not null,
            CarouselLoaded: banners is not null);
    }

    private async Task<NewsFeed?> LoadNewsAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using JsonDocument json = await GetAsync(
                http, "/announce/web/announcement/pagelatestlist", "page=1&pageSize=5", token).ConfigureAwait(false);
            if (!TryGetData(json.RootElement, out JsonElement data) ||
                data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("rows", out JsonElement rows) ||
                rows.ValueKind != JsonValueKind.Array)
                return null;

            var entries = new List<LauncherNewsItem>();
            var articleImages = new List<LauncherCarouselItem>();
            foreach (JsonElement row in rows.EnumerateArray())
            {
                string id = Text(row, "id");
                string title = Text(row, "title");
                if (id.Length == 0 || !id.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(title) ||
                    !row.TryGetProperty("startTime", out JsonElement time) || !time.TryGetInt64(out long timestamp))
                    continue;

                string date;
                try
                {
                    date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToOffset(TimeSpan.FromHours(8))
                        .ToString("MM/dd", CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException) { continue; }

                LauncherNewsEntryType type = Text(row, "type") switch
                {
                    "notice" => LauncherNewsEntryType.Notice,
                    "activity" => LauncherNewsEntryType.Event,
                    _ => LauncherNewsEntryType.Info
                };
                string url = $"{siteBase}/home/news/detail/{id}/";
                entries.Add(new(title, url, date, type));
                string cover = Text(row, "pcCoverImageUrl");
                if (useArticleImagesWhenCarouselIsEmpty && IsHttps(cover))
                    articleImages.Add(new(cover, url, title));
            }

            return entries.Count == 0 && rows.GetArrayLength() != 0
                ? null
                : new([.. entries], [.. articleImages]);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<LauncherCarouselItem[]?> LoadBannersAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using JsonDocument json = await GetAsync(http, bannerPath, "", token).ConfigureAwait(false);
            if (!TryGetData(json.RootElement, out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<LauncherCarouselItem>();
            foreach (JsonElement banner in data.EnumerateArray())
            {
                string image = Text(banner, bannerImageField);
                string link = Text(banner, "linkUrl");
                if (IsHttps(image))
                    result.Add(new(image, IsHttps(link) ? link : "", Text(banner, "title")));
            }

            return result.Count == 0 && data.GetArrayLength() != 0 ? null : [.. result];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // The public websites send a timestamp and an unkeyed checksum on anonymous editorial requests.
    private async Task<JsonDocument> GetAsync(HttpClient http, string path, string query, CancellationToken token)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(6));
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string checksum = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + query)));
        using var request = new HttpRequestMessage(HttpMethod.Get, apiBase + path + (query.Length > 0 ? "?" + query : ""));
        request.Headers.TryAddWithoutValidation("timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("sign", checksum);
        request.Headers.TryAddWithoutValidation("lang", language);
        request.Headers.TryAddWithoutValidation("sdk-version", "2.4.1");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            requestTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: requestTimeout.Token).ConfigureAwait(false);
    }

    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        data = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out JsonElement code) &&
            code.TryGetInt32(out int value) && value == 0 && root.TryGetProperty("data", out data);
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;

    private sealed record NewsFeed(LauncherNewsItem[] Items, LauncherCarouselItem[] ArticleImages);
}
