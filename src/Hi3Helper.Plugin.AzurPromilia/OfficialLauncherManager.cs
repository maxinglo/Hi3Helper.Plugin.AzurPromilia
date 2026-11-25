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

    protected override HttpClient ApiResponseHttpClient { get; set; } = new();
    protected override bool HasPreload => false;
    protected override bool HasUpdate => UsesNativeInstallation && IsInstalled &&
        ApiGameVersion > CurrentGameVersion;

    protected override bool IsInstalled => UsesNativeInstallation || region.ManjuuGameDirectoryName is not null
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
        Version? version = null;
        if (UsesNativeInstallation && !string.IsNullOrWhiteSpace(CurrentGameInstallPath))
        {
            version = VerifiedGameInstaller.ReadInstalledVersion(CurrentGameInstallPath, region.ProfileName);
        }
        else if (FindManjuuInstallation(CurrentGameInstallPath) is { } installed)
        {
            CurrentGameInstallPath = installed.Path;
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
        Task.Run(() =>
        {
            if (UsesNativeInstallation)
                return IsInstalled ? CurrentGameInstallPath : null;
            if (region.ManjuuGameDirectoryName is null)
                return _locator.FindExisting(CurrentGameInstallPath);

            return FindManjuuInstallation(CurrentGameInstallPath)?.Path ??
                FindManjuuInstallation(_locator.FindExisting(GetLauncherSearchPath()))?.Path;
        }, token);

    internal void OpenOfficialLauncher() => Process.Start(_locator.CreateStartInfo(GetLauncherSearchPath()))?.Dispose();

    internal string? GetInstalledExecutablePath() =>
        CurrentGameVersion != GameVersion.Empty && region.NativeGameExecutable is { } executable &&
        CurrentGameInstallPath is { } directory && Path.IsPathFullyQualified(directory)
            ? Path.GetFullPath(Path.Combine(directory, executable))
            : null;

    private (string Path, Version Version)? FindManjuuInstallation(string? candidate)
    {
        if (region.ManjuuGameDirectoryName is not { } folder || region.NativeGameExecutable is not { } executable ||
            region.Launcher is not { } launcher || string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            return null;

        return Read(candidate) ?? Read(Path.Combine(candidate, folder));

        (string Path, Version Version)? Read(string directory)
        {
            string game = Path.Combine(directory, executable);
            string data = Path.Combine(directory, Path.GetFileNameWithoutExtension(executable) + "_Data");
            string index = Path.Combine(directory, "_index");
            if (!File.Exists(game) || !Directory.Exists(data) || !File.Exists(Path.Combine(index, "version")))
                return null;

            try
            {
                using FileStream input = File.OpenRead(Path.Combine(index, "version"));
                using JsonDocument marker = JsonDocument.Parse(input);
                if (marker.RootElement.ValueKind != JsonValueKind.Object ||
                    !marker.RootElement.TryGetProperty("version", out JsonElement entry) || entry.ValueKind != JsonValueKind.String ||
                    !Version.TryParse(entry.GetString(), out Version? version) ||
                    !File.Exists(Path.Combine(index, version.ToString())) || !Authenticode.IsSignedBy(game, launcher.Signer))
                    return null;

                return (directory, version);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    private string? GetLauncherSearchPath()
    {
        string? directory = CurrentGameInstallPath;
        if (directory is not null && region.ManjuuGameDirectoryName is { } folder &&
            string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), folder, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));

        return directory;
    }
}
