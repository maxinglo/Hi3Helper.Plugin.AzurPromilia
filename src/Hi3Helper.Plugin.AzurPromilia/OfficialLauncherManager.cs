using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
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
    private GameReleaseRequest? _releaseRequest;

    protected override HttpClient ApiResponseHttpClient { get; set; } = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    protected override bool HasPreload => false;
    protected override bool HasUpdate => UsesNativeInstallation && IsInstalled &&
        ApiGameVersion > CurrentGameVersion;

    protected override bool IsInstalled => UsesNativeInstallation || OfficialGameDirectoryName is not null
        ? CurrentGameVersion != GameVersion.Empty &&
          CurrentGameInstallPath is { Length: > 0 } directory && region.NativeGameExecutable is { } executable &&
          File.Exists(Path.Combine(directory, executable))
        : _locator.FindCandidate(CurrentGameInstallPath) is not null;

    internal bool UsesNativeInstallation => region.LoadGameInstallPlan is not null;

    private string? OfficialGameDirectoryName => region.ManjuuGameDirectoryName ??
        (region.ProfileName == AzurPromiliaRegion.Japan.ProfileName ? JapanGameReleaseSource.GameTag : null);

    // A native installation only consumes a verified release for the chosen server.
    internal GameInstallPlan RequireInstallPlan() => Volatile.Read(ref _installPlan)
        ?? throw new InvalidOperationException("The publisher's game release information is unavailable.");

    public override void LoadConfig()
    {
        Version? version = null;
        if (UsesNativeInstallation && !string.IsNullOrWhiteSpace(CurrentGameInstallPath))
        {
            version = VerifiedGameInstaller.ReadInstalledVersion(CurrentGameInstallPath, region.ProfileName);
        }
        if (version is null && (!UsesNativeInstallation || string.IsNullOrWhiteSpace(CurrentGameInstallPath) ||
                !VerifiedGameInstaller.HasPendingInstallation(CurrentGameInstallPath)) &&
            FindOfficialInstallation(CurrentGameInstallPath) is { } installed)
        {
            CurrentGameInstallPath = installed.Path;
            if (!UsesNativeInstallation || !VerifiedGameInstaller.HasPendingInstallation(installed.Path))
                version = installed.Version;
        }
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
            GameReleaseRequest request;
            using (ThisInstanceLock.EnterScope())
                request = _releaseRequest ??= new GameReleaseRequest(inner =>
                    load(ApiResponseHttpClient, inner, FindLocalOfficialLauncherVersionDirectory));
            GameInstallPlan plan = await request.GetAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
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
        Task.Run(() =>
        {
            if (UsesNativeInstallation && IsInstalled)
                return CurrentGameInstallPath;
            if (OfficialGameDirectoryName is null)
                return UsesNativeInstallation ? null : _locator.FindExisting(CurrentGameInstallPath);

            return FindOfficialInstallation(CurrentGameInstallPath)?.Path ??
                FindOfficialInstallation(_locator.FindExisting(GetLauncherSearchPath()))?.Path;
        }, token);

    internal void OpenOfficialLauncher() => Process.Start(_locator.CreateStartInfo(GetLauncherSearchPath()))?.Dispose();

    internal Task<bool> LaunchGameAsync(string? arguments, bool boosted, ProcessPriorityClass priority, CancellationToken token)
    {
        if (region.NativeGameExecutable is not null)
        {
            string executable = GetInstalledExecutablePath()
                ?? throw new InvalidOperationException("Select the installed game directory before starting the game.");
            return InstalledGameLauncher.RunAsync(executable, arguments, boosted, priority, token);
        }

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            OpenOfficialLauncher();
            return true;
        }, token);
    }

    internal string? FindLocalOfficialLauncherDirectory() => _locator.FindExisting(GetLauncherSearchPath(), requireLocalFile: true);

    private string? FindLocalOfficialLauncherVersionDirectory() =>
        FindLocalOfficialLauncherDirectory() is { } root && _locator.FindCandidate(root) is { } launcher
            ? Path.GetDirectoryName(launcher) : null;

    internal string? GetInstalledExecutablePath() =>
        CurrentGameVersion != GameVersion.Empty && region.NativeGameExecutable is { } executable &&
        CurrentGameInstallPath is { } directory && Path.IsPathFullyQualified(directory)
            ? Path.GetFullPath(Path.Combine(directory, executable))
            : null;

    public override void Dispose()
    {
        using (ThisInstanceLock.EnterScope())
        {
            _releaseRequest?.Dispose();
            _releaseRequest = null;
        }
        base.Dispose();
    }

    internal IReadOnlyList<GameFile> ReadOfficialInventory(string directory, GameInstallPlan plan)
    {
        if (plan.Profile != region.ProfileName || region.NativeGameExecutable is not { } executable ||
            ReadOfficialVersion(directory) is not { } version || version > plan.Version)
            return [];

        try
        {
            if (region.ManjuuGameDirectoryName is null)
                return OfficialGameInventory.ReadJapan(directory, executable, version);

            return plan.OfficialIndexFormat is { } format
                ? OfficialGameInventory.ReadManjuu(directory, executable, version, format.Key, format.Iv)
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SharedStatic.InstanceLogger.LogWarning(error, "Could not read the installed {Profile} publisher inventory.", region.ProfileName);
            return [];
        }
    }

    private (string Path, Version Version)? FindOfficialInstallation(string? candidate)
    {
        if (OfficialGameDirectoryName is not { } folder || string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            return null;

        if (ReadOfficialVersion(candidate) is { } version)
            return (candidate, version);

        string child = Path.Combine(candidate, folder);
        return ReadOfficialVersion(child) is { } childVersion ? (child, childVersion) : null;
    }

    private Version? ReadOfficialVersion(string directory)
    {
        if (OfficialGameDirectoryName is null || region.NativeGameExecutable is not { } executable || region.Launcher is not { } launcher)
            return null;

        string game = Path.Combine(directory, executable);
        string data = Path.Combine(directory, Path.GetFileNameWithoutExtension(executable) + "_Data");
        if (!File.Exists(game) || !Directory.Exists(data))
            return null;

        try
        {
            Version? version = region.ManjuuGameDirectoryName is null
                ? OfficialGameInventory.ReadJapanVersion(directory, executable)
                : OfficialGameInventory.ReadManjuuVersion(directory);
            return version is not null && Authenticode.IsSignedBy(game, launcher.Signer) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string? GetLauncherSearchPath()
    {
        string? directory = CurrentGameInstallPath;
        if (directory is not null && OfficialGameDirectoryName is { } folder &&
            string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), folder, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));

        return directory;
    }
}
