using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherManager(AzurPromiliaRegion region) : GameManagerBase
{
    private readonly OfficialLauncherLocator _locator = new(region);

    protected override HttpClient ApiResponseHttpClient { get; set; } = new();
    protected override bool HasPreload => false;
    protected override bool HasUpdate => false;

    // Collapse's installed state represents an attached regional launcher. The game itself is managed there.
    protected override bool IsInstalled => _locator.FindCandidate(CurrentGameInstallPath) is not null;

    public override void LoadConfig() { }
    public override void SaveConfig() { }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion) => CurrentGameVersion = GameVersion.Empty;
    protected override void SetGamePathInner(string gamePath) => CurrentGameInstallPath = _locator.GetInstallDirectory(gamePath);

    protected override Task<int> InitAsync(CancellationToken token) => token.IsCancellationRequested
        ? Task.FromCanceled<int>(token)
        : Task.FromResult(0);

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token) =>
        Task.Run(() => _locator.FindExisting(CurrentGameInstallPath), token);

    internal void OpenOfficialLauncher() => Process.Start(_locator.CreateStartInfo(CurrentGameInstallPath))?.Dispose();
}
