using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherInstaller(OfficialLauncherManager manager) : GameInstallerBase(manager)
{
    protected override Task<int> InitAsync(CancellationToken token) => token.IsCancellationRequested
        ? Task.FromCanceled<int>(token)
        : Task.FromResult(0);

    protected override Task<long> GetGameSizeAsyncInner(GameInstallerKind kind, CancellationToken token) =>
        GetGameDownloadedSizeAsyncInner(kind, token);

    protected override Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind kind, CancellationToken token) =>
        token.IsCancellationRequested ? Task.FromCanceled<long>(token) : Task.FromResult(0L);

    protected override Task StartInstallAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => HandOffAsync(token);

    protected override Task StartUpdateAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => HandOffAsync(token);

    protected override Task StartPreloadAsyncInner(InstallProgressDelegate? progress, InstallProgressStateDelegate? state,
        CancellationToken token) => HandOffAsync(token);

    protected override Task UninstallAsyncInner(CancellationToken token) => Task.FromException(new NotSupportedException(
        "Uninstall Azur Promilia from the official launcher or Windows Installed Apps."));

    public override void Dispose() { }

    // Cancel the Collapse operation after external handoff so it does not report a completed game installation.
    private async Task HandOffAsync(CancellationToken token)
    {
        await Task.Run(manager.OpenOfficialLauncher, token).ConfigureAwait(false);
        throw new OperationCanceledException("Continue in the official Azur Promilia launcher.");
    }
}
