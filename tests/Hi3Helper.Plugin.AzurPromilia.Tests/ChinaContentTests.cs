using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.AzurPromilia.Content;
using Hi3Helper.Plugin.Core.Management;

internal static class ChinaContentTests
{
    internal static async Task RunAsync()
    {
        using var http = new HttpClient(new FixtureHandler());
        LauncherContentSnapshot snapshot = await ChinaLauncherContentSource.LoadAsync(http, CancellationToken.None);
        Require(snapshot.News.Length == 1 && snapshot.News[0].Type == LauncherNewsEntryType.Notice &&
                snapshot.News[0].Date == "11/24" && snapshot.News[0].Url.EndsWith("/912088789906755584/"),
            "China notice data should map to an official link and China-local date.");
        Require(snapshot.Carousel.Length == 1 && snapshot.Carousel[0].Url == "", "Banners without a link should remain visible.");
        Require(snapshot.Social.Length == 5, "Official China social links should be present.");

        using var partialHttp = new HttpClient(new FixtureHandler(failNews: true));
        snapshot = await ChinaLauncherContentSource.LoadAsync(partialHttp, CancellationToken.None);
        Require(!snapshot.NewsLoaded && snapshot.CarouselLoaded && snapshot.News.Length == 0 &&
                snapshot.Carousel.Length == 1 && snapshot.Social.Length == 5,
            "A news outage should not hide banners or social links.");

        using var malformedHttp = new HttpClient(new FixtureHandler(newsJson: """{"code":0,"data":{"rows":[{}]}}"""));
        snapshot = await ChinaLauncherContentSource.LoadAsync(malformedHttp, CancellationToken.None);
        Require(!snapshot.NewsLoaded && snapshot.CarouselLoaded, "Malformed items must not replace the last good news.");

        using var emptyHttp = new HttpClient(new FixtureHandler(newsJson: """{"code":0,"data":{"rows":[]}}"""));
        snapshot = await ChinaLauncherContentSource.LoadAsync(emptyHttp, CancellationToken.None);
        Require(snapshot.NewsLoaded && snapshot.News.Length == 0, "An empty official feed must clear old news.");

        var website = new LauncherContentSnapshot(
            [new("Website event", "https://azurpromilia.manjuu.com/home/news/detail/1/", "11/26", LauncherNewsEntryType.Event),
             new("Website notice", "https://azurpromilia.manjuu.com/home/news/detail/2/", "11/26", LauncherNewsEntryType.Notice)],
            [], []);
        var cached = new LauncherContentSnapshot(
            [new("Official site", "https://azurpromilia.manjuu.com/home/", "11/24", LauncherNewsEntryType.Info)],
            [new("https://l1-all-plat-server.manjuu.com/azurpromilia/img/banner.png", "", "Launcher banner")], []);
        snapshot = ChinaLauncherContentSource.Combine(website, cached);
        Require(snapshot.NewsLoaded && snapshot.News.SequenceEqual(website.News.Concat(cached.News)) &&
                snapshot.Carousel.SequenceEqual(cached.Carousel),
            "Launcher cache with only Info must preserve the website's Events and Notices alongside launcher artwork.");
        snapshot = ChinaLauncherContentSource.Combine(website with { News = [], NewsLoaded = false }, cached);
        Require(!snapshot.NewsLoaded && snapshot.CarouselLoaded,
            "A website outage must retain the previously displayed Events and Notices instead of replacing them with cached Info.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(bool failNews = false, string? newsJson = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string timestamp = request.Headers.GetValues("timestamp").Single();
            string query = request.RequestUri!.Query.TrimStart('?');
            string expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + query)));
            Require(request.Headers.GetValues("sign").Single() == expected, "Website request signature must match the public format.");
            bool isNews = request.RequestUri.AbsolutePath.EndsWith("pagelatestlist", StringComparison.Ordinal);
            string json = isNews
                ? newsJson ?? """{"code":0,"data":{"rows":[{"id":"912088789906755584","title":"Notice","type":"notice","startTime":1795503600000}]}}"""
                : """{"code":0,"data":[{"imageUrl":"https://example.invalid/banner.png"}]}""";
            return Task.FromResult(isNews && failNews
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
