using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.AzurPromilia.GameData;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherInstaller : GameInstallerBase
{
    private readonly OfficialLauncherManager _manager;
    private readonly HttpClient? _assetClient;
    private readonly VerifiedGameInstaller? _nativeInstaller;
    private SizeSnapshot? _sizeSnapshot;

    internal OfficialLauncherInstaller(OfficialLauncherManager manager, HttpMessageHandler? assetHandler = null)
        : base(manager)
    {
        _manager = manager;
        if (manager.UsesNativeInstallation)
        {
            _assetClient = new HttpClient(assetHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            _nativeInstaller = new VerifiedGameInstaller(_assetClient);
        }
    }

    protected override Task<int> InitAsync(CancellationToken token) => token.IsCancellationRequested
        ? Task.FromCanceled<int>(token)
        : Task.FromResult(0);

    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_nativeInstaller is null)
            return 0;

        string path = EnsureAndGetGamePath();
        GameInstallPlan plan = _manager.RequireInstallPlan();
        var bytes = await _nativeInstaller.GetDownloadSizesAsync(path, plan, token).ConfigureAwait(false);
        Interlocked.Exchange(ref _sizeSnapshot, new SizeSnapshot(path, plan, bytes.Downloaded));
        return bytes.Total;
    }

    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_nativeInstaller is null)
            return 0;

        string path = EnsureAndGetGamePath();
        GameInstallPlan plan = _manager.RequireInstallPlan();
        SizeSnapshot? snapshot = Interlocked.Exchange(ref _sizeSnapshot, null);
        if (snapshot is not null && ReferenceEquals(snapshot.Plan, plan) &&
            string.Equals(snapshot.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.Downloaded;
        }

        return (await _nativeInstaller.GetDownloadSizesAsync(path, plan, token).ConfigureAwait(false)).Downloaded;
    }

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => InstallAsync(progress, state, token);

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => InstallAsync(progress, state, token);

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => _nativeInstaller is null ? HandOffAsync(token)
        : Task.FromException(new NotSupportedException("The download source has no separate preload release."));

    protected override Task UninstallAsyncInner(CancellationToken token) => Task.FromException(new NotSupportedException(
        _nativeInstaller is null ? "Uninstall Azur Promilia from the official launcher or Windows Installed Apps."
        : "Automatic game removal is unavailable for this installation."));

    public override void Dispose() => _assetClient?.Dispose();

    private async Task InstallAsync(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token)
    {
        if (_nativeInstaller is null)
        {
            await HandOffAsync(token).ConfigureAwait(false);
            return;
        }

        token.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _sizeSnapshot, null);
        string path = EnsureAndGetGamePath();
        GameInstallPlan plan = _manager.RequireInstallPlan();
        state?.Invoke(InstallProgressState.Preparing);
        state?.Invoke(InstallProgressState.Download);
        try
        {
            await _nativeInstaller.InstallAsync(path, plan, progress is null ? null : (downloaded, total) =>
            {
                var value = new InstallProgress { DownloadedBytes = downloaded, TotalBytesToDownload = total };
                progress(in value);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _manager.LoadConfig();
        }

        state?.Invoke(InstallProgressState.Completed);
    }

    // Cancel the Collapse operation after external handoff so it does not report a completed game installation.
    private async Task HandOffAsync(CancellationToken token)
    {
        await Task.Run(_manager.OpenOfficialLauncher, token).ConfigureAwait(false);
        throw new OperationCanceledException("Continue in the official Azur Promilia launcher.");
    }

    private sealed record SizeSnapshot(string Path, GameInstallPlan Plan, long Downloaded);
}
