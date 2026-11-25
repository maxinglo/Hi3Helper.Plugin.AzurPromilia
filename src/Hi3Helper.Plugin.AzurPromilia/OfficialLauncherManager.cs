using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.AzurPromilia.GameData;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class OfficialLauncherManager(AzurPromiliaRegion region) : GameManagerBase
{
    private readonly OfficialLauncherLocator _locator = new(region);
    private GameInstallPlan? _installPlan;

    protected override HttpClient ApiResponseHttpClient { get; set; } = new();
    protected override bool HasPreload => false;
    protected override bool HasUpdate => UsesNativeInstallation && IsInstalled &&
        ApiGameVersion > CurrentGameVersion;

    protected override bool IsInstalled => UsesNativeInstallation
        ? CurrentGameVersion != GameVersion.Empty &&
          CurrentGameInstallPath is { Length: > 0 } directory && region.NativeGameExecutable is { } executable &&
          File.Exists(Path.Combine(directory, executable))
        : _locator.FindCandidate(CurrentGameInstallPath) is not null;

    internal bool UsesNativeInstallation => region.LoadGameInstallPlan is not null;

    // A native installation only consumes a verified release for the chosen server.
    internal GameInstallPlan RequireInstallPlan() => Volatile.Read(ref _installPlan)
        ?? throw new InvalidOperationException("The publisher's game release information is unavailable.");

    public override void LoadConfig()
    {
        Version? version = UsesNativeInstallation && !string.IsNullOrWhiteSpace(CurrentGameInstallPath)
            ? VerifiedGameInstaller.ReadInstalledVersion(CurrentGameInstallPath, region.ProfileName)
            : null;
        CurrentGameVersion = version is null ? GameVersion.Empty : new GameVersion(version.ToString());
    }

    public override void SaveConfig() { }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion) => LoadConfig();

    protected override void SetGamePathInner(string gamePath)
    {
        CurrentGameInstallPath = UsesNativeInstallation ? gamePath : _locator.GetInstallDirectory(gamePath);
        LoadConfig();
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (region.LoadGameInstallPlan is not { } load)
            return 0;

        try
        {
            GameInstallPlan plan = await load(ApiResponseHttpClient, token).ConfigureAwait(false);
            if (!string.Equals(plan.Profile, region.ProfileName, StringComparison.Ordinal) ||
                region.NativeGameExecutable is not { } executable ||
                !plan.Files.Any(file => string.Equals(file.InstallPath, executable, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The game release does not match this server and its executable.");
            }

            ApiGameVersion = new GameVersion(plan.Version.ToString());
            Volatile.Write(ref _installPlan, plan);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException ||
                                      error is OperationCanceledException && !token.IsCancellationRequested)
        {
            SharedStatic.InstanceLogger.LogWarning(error, "Could not refresh the game release for {Profile}.", region.ProfileName);
        }

        token.ThrowIfCancellationRequested();
        LoadConfig();
        return 0;
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token) =>
        Task.Run(() => UsesNativeInstallation ? IsInstalled ? CurrentGameInstallPath : null
            : _locator.FindExisting(CurrentGameInstallPath), token);

    internal void OpenOfficialLauncher() => Process.Start(_locator.CreateStartInfo(CurrentGameInstallPath))?.Dispose();
}
