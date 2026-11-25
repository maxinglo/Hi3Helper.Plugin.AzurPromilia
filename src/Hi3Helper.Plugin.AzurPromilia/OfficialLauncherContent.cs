using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using Hi3Helper.Plugin.AzurPromilia.Content;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherMedia(string backgroundUrl, string? backgroundVideoUrl,
    Func<string?>? findVerifiedLauncherDirectory = null, HttpClient? http = null, string? cacheDirectory = null) : LauncherApiMediaBase
{
    private readonly LauncherBackground _fallback = new(backgroundUrl, backgroundVideoUrl);
    private LauncherBackground? _background;
    private LauncherBackground? _preparedBackground;

    internal string PosterUrl => Background.StillUrl;

    private LauncherBackground Background
    {
        get
        {
            using (ThisInstanceLock.EnterScope())
                return _background ??= ReadBackground();
        }
    }

    protected override HttpClient ApiResponseHttpClient { get; set; } = http ?? new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

    // Keep the China backgrounds visible by caching both assets under filenames Collapse can round-trip through a file URI.
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (findVerifiedLauncherDirectory is null)
            return 0;

        LauncherBackground background = ReadBackground();
        string directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CollapseLauncher", "Plugins", "AzurPromilia", "Media");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string> still = PrepareAssetAsync(directory, background.StillUrl, request.Token);
        Task<string>? video = background.VideoUrl is { } videoUrl ? PrepareAssetAsync(directory, videoUrl, request.Token) : null;
        var prepared = new LauncherBackground(await still.ConfigureAwait(false), video is null ? null : await video.ConfigureAwait(false));

        using (ThisInstanceLock.EnterScope())
        {
            token.ThrowIfCancellationRequested();
            _background = background;
            _preparedBackground = prepared;
        }
        return 0;
    }

    private async Task<string> PrepareAssetAsync(string directory, string url, CancellationToken token)
    {
        try
        {
            return await LauncherBackgroundCache.GetOrDownloadAsync(ApiResponseHttpClient, directory, url, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
                                      UnauthorizedAccessException or OperationCanceledException)
        {
            SharedStatic.InstanceLogger.LogWarning(error, "Could not cache official launcher background {Url}.", url);
            return url;
        }
    }

    private LauncherBackground ReadBackground() => findVerifiedLauncherDirectory is null
        ? _fallback
        : LauncherBackgroundCache.Read(findVerifiedLauncherDirectory) ?? _fallback;

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        LauncherBackground background = Background;
        result = background.VideoUrl is not null ? LauncherBackgroundFlag.TypeIsVideo | LauncherBackgroundFlag.IsSourceFile
            : background.StillUrl.Length != 0 ? LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile
            : LauncherBackgroundFlag.None;
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        LauncherBackground background;
        using (ThisInstanceLock.EnterScope())
            background = _preparedBackground ?? (_background ??= ReadBackground());
        string[] paths = background.VideoUrl is { } video
            ? Path.IsPathFullyQualified(background.StillUrl) && !Path.IsPathFullyQualified(video)
                ? [background.StillUrl, video] : [video, background.StillUrl]
            : background.StillUrl.Length != 0 ? [background.StillUrl] : [];
        if (paths.Length == 0)
        {
            (handle, count, isDisposable, isAllocated) = (0, 0, false, false);
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemory<LauncherPathEntry>.Alloc(paths.Length);
        for (int index = 0; index < paths.Length; index++)
            entries[index].Write(paths[index], Span<byte>.Empty);
        (handle, count, isDisposable, isAllocated) = (entries.AsSafePointer(), entries.Length, entries.IsDisposable == 1, true);
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated) =>
        (handle, count, isDisposable, isAllocated) = (0, 0, false, false);
}

[GeneratedComClass]
internal sealed partial class OfficialLauncherNews(
    string profile, Func<HttpClient, CancellationToken, Task<LauncherContentSnapshot>> loadContent) : LauncherApiNewsBase
{
    private LauncherContentSnapshot _snapshot = LauncherContentSnapshot.Empty;

    protected override HttpClient ApiResponseHttpClient { get; set; } = new PluginHttpClientBuilder().AllowRedirections().Create();

    // Fetch the selected region's live public publisher content without delaying the game page on a feed outage.
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        request.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            LauncherContentSnapshot snapshot = await loadContent(ApiResponseHttpClient, request.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using (ThisInstanceLock.EnterScope())
            {
                LauncherContentSnapshot previous = Volatile.Read(ref _snapshot);
                LauncherNewsItem[] news = snapshot.NewsLoaded
                    ? snapshot.News.Where(item => !string.IsNullOrWhiteSpace(item.Title) && IsHttps(item.Url))
                        .DistinctBy(item => item.Url).ToArray()
                    : previous.News;
                LauncherCarouselItem[] carousel = snapshot.CarouselLoaded
                    ? snapshot.Carousel.Where(item => IsHttps(item.ImageUrl) && (string.IsNullOrWhiteSpace(item.Url) || IsHttps(item.Url)))
                        .DistinctBy(item => item.ImageUrl).ToArray()
                    : previous.Carousel;
                LauncherSocialItem[] social = snapshot.Social.Where(item => !string.IsNullOrWhiteSpace(item.Name) && IsHttps(item.IconUrl)
                    && (IsHttps(item.Url) || IsHttps(item.QrImageUrl))).ToArray();
                Volatile.Write(ref _snapshot, new LauncherContentSnapshot(news, carousel, social));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            SharedStatic.InstanceLogger.LogWarning("Official launcher content timed out for {Profile}.", profile);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Could not refresh public launcher content for {Profile}.", profile);
        }

        token.ThrowIfCancellationRequested();
        return 0;
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        LauncherNewsItem[] items = Volatile.Read(ref _snapshot).News;
        var entries = items.Length == 0
            ? PluginDisposableMemory<LauncherNewsEntry>.Empty
            : PluginDisposableMemory<LauncherNewsEntry>.Alloc(items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            LauncherNewsItem item = items[index];
            entries[index].Write(item.Title, item.Description, item.Url, item.Date, item.Type);
        }
        (handle, count, isDisposable, isAllocated) = (entries.AsSafePointer(), items.Length, entries.IsDisposable == 1, items.Length != 0);
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        LauncherCarouselItem[] items = Volatile.Read(ref _snapshot).Carousel;
        var entries = items.Length == 0
            ? PluginDisposableMemory<LauncherCarouselEntry>.Empty
            : PluginDisposableMemory<LauncherCarouselEntry>.Alloc(items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            LauncherCarouselItem item = items[index];
            // Collapse's carousel click handler requires a target. Open the publisher's artwork when no link was supplied.
            entries[index].Write(item.Description, item.ImageUrl, IsHttps(item.Url) ? item.Url : item.ImageUrl);
        }
        (handle, count, isDisposable, isAllocated) = (entries.AsSafePointer(), items.Length, entries.IsDisposable == 1, items.Length != 0);
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        LauncherSocialItem[] items = Volatile.Read(ref _snapshot).Social;
        var entries = items.Length == 0
            ? PluginDisposableMemory<LauncherSocialMediaEntry>.Empty
            : PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            LauncherSocialItem item = items[index];
            ref LauncherSocialMediaEntry entry = ref entries[index];
            entry.WriteDescription(item.Name);
            entry.WriteClickUrl(IsHttps(item.Url) ? item.Url : null);
            entry.WriteIcon(item.IconUrl);
            var flags = LauncherSocialMediaEntryFlag.IconIsPath;
            if (IsHttps(item.HoverIconUrl))
            {
                entry.WriteIconHover(item.HoverIconUrl);
                flags |= LauncherSocialMediaEntryFlag.IconHoverIsPath;
            }
            if (IsHttps(item.QrImageUrl))
            {
                entry.WriteQrImage(item.QrImageUrl);
                entry.WriteQrImageDescription(item.Name);
                flags |= LauncherSocialMediaEntryFlag.QrImageIsPath;
            }
            entry.Flags = flags;
        }
        (handle, count, isDisposable, isAllocated) = (entries.AsSafePointer(), items.Length, entries.IsDisposable == 1, items.Length != 0);
    }

    private static bool IsHttps(string? input) => Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
}
