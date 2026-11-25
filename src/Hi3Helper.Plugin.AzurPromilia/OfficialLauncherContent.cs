using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherMedia(string backgroundUrl) : LauncherApiMediaBase
{
    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    protected override Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result) => result = backgroundUrl.Length == 0
        ? LauncherBackgroundFlag.None
        : LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile;

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (backgroundUrl.Length == 0)
        {
            (handle, count, isDisposable, isAllocated) = (0, 0, false, false);
            return;
        }

        PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemory<LauncherPathEntry>.Alloc();
        entries[0].Write(backgroundUrl, Span<byte>.Empty);
        (handle, count, isDisposable, isAllocated) = (entries.AsSafePointer(), 1, entries.IsDisposable == 1, true);
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated) =>
        (handle, count, isDisposable, isAllocated) = (0, 0, false, false);
}

[GeneratedComClass]
internal sealed partial class OfficialLauncherNews : LauncherApiNewsBase
{
    protected override HttpClient ApiResponseHttpClient { get; set; } = new();

    protected override Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated) =>
        (handle, count, isDisposable, isAllocated) = (0, 0, false, false);

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated) =>
        (handle, count, isDisposable, isAllocated) = (0, 0, false, false);

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated) =>
        (handle, count, isDisposable, isAllocated) = (0, 0, false, false);
}
