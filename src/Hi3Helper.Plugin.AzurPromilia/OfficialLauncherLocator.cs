using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Hi3Helper.Plugin.AzurPromilia;

internal sealed class OfficialLauncherLocator(AzurPromiliaRegion region)
{
    private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    internal string? FindCandidate(string? directory) => GetCandidatePaths(directory).FirstOrDefault(MatchesProduct);

    internal string? FindExisting(string? configuredDirectory) =>
        FindLauncher(configuredDirectory) is { } launcher ? GetInstallDirectory(Path.GetDirectoryName(launcher)) : null;

    internal string? GetInstallDirectory(string? directory)
    {
        if (region.VersionedLauncherDirectory && !string.IsNullOrEmpty(directory)
            && Path.IsPathFullyQualified(directory)
            && Version.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), out _)
            && MatchesProduct(Path.Combine(directory, region.LauncherExecutable)))
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));

        return directory;
    }

    // Open the official launcher as installed, keeping account and game management in the publisher's UI.
    internal ProcessStartInfo CreateStartInfo(string? configuredDirectory)
    {
        string? launcher = FindLauncher(configuredDirectory);
        return new ProcessStartInfo
        {
            FileName = launcher ?? region.Website,
            WorkingDirectory = launcher is null ? string.Empty : Path.GetDirectoryName(launcher)!,
            UseShellExecute = true
        };
    }

    private string? FindLauncher(string? configuredDirectory)
    {
        return GetInstalledDirectories().Prepend(configuredDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(GetCandidatePaths)
            .FirstOrDefault(path => MatchesProduct(path) && Authenticode.IsSignedBy(path, region.LauncherSigner));
    }

    private IEnumerable<string> GetCandidatePaths(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            yield break;

        yield return Path.GetFullPath(Path.Combine(directory, region.LauncherExecutable));
        if (!region.VersionedLauncherDirectory)
            yield break;

        // Manjuu updates its launcher inside a versioned directory under the permanent install location.
        foreach (string versionedDirectory in GetSubdirectories(directory)
                     .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out Version? version) ? version : null))
                     .Where(entry => entry.Version is not null)
                     .OrderByDescending(entry => entry.Version)
                     .Select(entry => entry.Path))
        {
            yield return Path.GetFullPath(Path.Combine(versionedDirectory, region.LauncherExecutable));
        }
    }

    private bool MatchesProduct(string candidate)
    {
        if (!File.Exists(candidate))
            return false;

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(candidate);
            return version.ProductName == region.LauncherProductName && version.CompanyName == region.LauncherSigner;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static string[] GetSubdirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<string> GetInstalledDirectories()
    {
        if (region.UninstallKey is null)
            yield break;

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                if (ReadRegisteredDirectory(hive, view) is { } directory)
                    yield return directory;
            }
        }
    }

    private string? ReadRegisteredDirectory(RegistryHive hive, RegistryView view)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? app = root.OpenSubKey(UninstallRegistryPath + region.UninstallKey);
            return app?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
