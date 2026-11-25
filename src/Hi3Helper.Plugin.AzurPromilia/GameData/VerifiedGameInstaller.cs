using System.Globalization;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

// Install only files listed in the official launcher index. The game manages its supplemental resources and shaders.
internal sealed class VerifiedGameInstaller(HttpClient http)
{
    private const string Workspace = ".collapse-azurpromilia";
    private const string PendingMarker = Workspace + "/pending.json";
    private const string InstalledMarker = Workspace + "/installed.json";
    private const string OperationLock = Workspace + "/operation.lock";

    internal async Task<(long Total, long Downloaded)> GetDownloadSizesAsync(string installPath, GameInstallPlan plan,
        CancellationToken token)
    {
        Operation operation = CreateOperation(installPath, plan);
        HashSet<string> owned = ReadOwnedFiles(operation, plan);
        var batch = await PrepareAsync(operation, owned, token).ConfigureAwait(false);
        return (batch.Total, batch.Downloaded);
    }

    internal async Task InstallAsync(string installPath, GameInstallPlan plan, Action<long, long>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Operation operation = CreateOperation(installPath, plan);
        Directory.CreateDirectory(Resolve(operation.Root, Workspace));
        await using var operationLock = new FileStream(Resolve(operation.Root, OperationLock), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        HashSet<string> owned = ReadOwnedFiles(operation, plan);
        var batch = await PrepareAsync(operation, owned, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        EnsureStagingSpace(operation.Root, batch.RemainingStagingBytes);
        await StageAsync(operation, batch, progress, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        WriteMarker(operation.Root, PendingMarker, plan, files: null);
        foreach (string directory in plan.Directories)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Resolve(operation.Root, directory));
        }

        foreach (DownloadJob job in batch.Jobs)
        {
            token.ThrowIfCancellationRequested();
            string destination = Resolve(operation.Root, job.File.Entry.InstallPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destination = Resolve(operation.Root, job.File.Entry.InstallPath);
            string partial = Resolve(operation.Root, job.File.PartialPath);
            bool previouslyOwned = owned.Contains(job.File.Entry.InstallPath);
            File.Move(partial, destination, overwrite: previouslyOwned);
        }

        // Keep the pending marker until every target, including files reused in place, still matches the selected index.
        foreach (FileLocation file in operation.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!await VerifiedGameFileDownloader.VerifyAsync(Resolve(operation.Root, file.Entry.InstallPath), file.Entry, token)
                    .ConfigureAwait(false))
                throw new InvalidDataException("An installed file did not match the selected game index.");
            owned.Add(file.Entry.InstallPath);
        }

        token.ThrowIfCancellationRequested();
        WriteMarker(operation.Root, InstalledMarker, plan, owned);
        File.Delete(Resolve(operation.Root, PendingMarker));
    }

    internal static bool HasPendingInstallation(string installPath)
    {
        try
        {
            return HasPendingMarker(NormalizeRoot(installPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or
                                   InvalidDataException or UnauthorizedAccessException)
        {
            // Do not fall back to the publisher's older version if the plugin's marker cannot be inspected safely.
            return true;
        }
    }

    private static bool HasPendingMarker(string root) => GetAttributes(Resolve(root, PendingMarker)) is not null;

    internal static Version? ReadInstalledVersion(string installPath, string profile)
    {
        try
        {
            string root = NormalizeRoot(installPath);
            if (HasPendingMarker(root))
                return null;
            Marker? installed = ReadMarker(root, InstalledMarker, hasFiles: true);
            return installed is not null && string.Equals(installed.Profile, profile, StringComparison.OrdinalIgnoreCase)
                ? installed.Version
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or InvalidDataException
                                   or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void EnsureStagingSpace(string root, long required)
    {
        if (required == 0)
            return;
        long available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (available < required)
        {
            // Collapse recognizes this diagnostic to display the disk-space dialog. The reserve is zero.
            throw new IOException(FormattableString.Invariant(
                    $"Insufficient remaining disk space. Required {required} additional bytes including reserve; available {available} bytes."),
                unchecked((int)0x80070070));
        }
    }

    private async Task StageAsync(Operation operation, DownloadBatch batch, Action<long, long>? progress,
        CancellationToken token)
    {
        long downloaded = batch.Downloaded;
        progress?.Invoke(downloaded, batch.Total);
        if (batch.Jobs.Count == 0)
            return;

        Directory.CreateDirectory(Resolve(operation.Root, operation.StageDirectory));
        var progressLock = new Lock();
        await Parallel.ForEachAsync(batch.Jobs,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (job, innerToken) =>
            {
                long previous = job.Cached;
                await DownloadFileAsync(operation, job, bytes =>
                    {
                        lock (progressLock)
                        {
                            downloaded = checked(downloaded + (bytes - previous));
                            previous = bytes;
                            progress?.Invoke(downloaded, batch.Total);
                        }
                    }, innerToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    // Give the CDN one clean attempt if the staged prefix fails verification after a resume.
    private async Task DownloadFileAsync(Operation operation, DownloadJob job, Action<long> progress, CancellationToken token)
    {
        int source = 0;
        long staged = job.Cached;
        GameFile file = job.File.Entry;
        void Report(long bytes)
        {
            staged = bytes;
            progress(bytes);
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            Uri root = operation.CdnRoots[source];
            if (!GameCdn.TryResolvePath(root, job.File.EscapedRemotePath, out Uri? url))
                throw new InvalidDataException("A game file path escaped the publisher CDN base URI.");
            string partial = Resolve(operation.Root, job.File.PartialPath);
            bool resumed = staged > 0 && staged < file.Size;
            try
            {
                await VerifiedGameFileDownloader.DownloadAsync(http, url, partial, file, Report, token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (GameCdn.IsTransferFailure(error, token))
            {
                if (resumed && staged == 0 && error is InvalidDataException)
                    continue;
                if (++source == operation.CdnRoots.Count)
                    throw;
            }
        }
    }

    private static async Task<DownloadBatch> PrepareAsync(Operation operation, HashSet<string> owned,
        CancellationToken token)
    {
        var parentDirectories = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string directory in operation.Directories)
        {
            token.ThrowIfCancellationRequested();
            FileAttributes? attributes = Probe(ResolvePath(operation.Root, directory), parentDirectories);
            if (attributes is { } present && (present & FileAttributes.Directory) == 0)
                throw new IOException("An existing file conflicts with a game index directory.");
        }

        var jobs = new List<DownloadJob>();
        long total = 0;
        long downloaded = 0;
        long remainingStagingBytes = 0;
        foreach (FileLocation file in operation.Files)
        {
            token.ThrowIfCancellationRequested();
            string destination = ResolvePath(operation.Root, file.Entry.InstallPath);
            FileAttributes? attributes = Probe(destination, parentDirectories);
            if (attributes is { } present)
            {
                if ((present & FileAttributes.Directory) != 0)
                    throw new IOException("An existing directory conflicts with a game index file.");
                if (await VerifiedGameFileDownloader.VerifyAsync(destination, file.Entry, token).ConfigureAwait(false))
                    continue;
                if (!owned.Contains(file.Entry.InstallPath))
                    throw new IOException("An existing unowned file conflicts with the game index.");
            }

            string partial = ResolvePath(operation.Root, file.PartialPath);
            var (cached, stagingCredit) = await GetCachedSizeAsync(partial, Probe(partial, parentDirectories), file.Entry, token)
                .ConfigureAwait(false);
            total = checked(total + file.Entry.Size);
            downloaded = checked(downloaded + cached);
            remainingStagingBytes = checked(remainingStagingBytes + (file.Entry.Size - stagingCredit));
            jobs.Add(new DownloadJob(file, cached));
        }
        return new DownloadBatch(jobs, total, downloaded, remainingStagingBytes);
    }

    private static async Task<(long Downloaded, long StagingCredit)> GetCachedSizeAsync(string path,
        FileAttributes? attributes, GameFile file, CancellationToken token)
    {
        if (attributes is not { } present)
            return (0, 0);
        if ((present & FileAttributes.Directory) != 0)
            throw new IOException("A directory conflicts with a staged game file.");
        long size = new FileInfo(path).Length;
        if (size < file.Size)
        {
            // A restart might discard the prefix. Logical bytes in a sparse or compressed prefix may free less space.
            long stagingCredit = (present & (FileAttributes.SparseFile | FileAttributes.Compressed)) == 0 ? size : 0;
            return (size, stagingCredit);
        }
        return size == file.Size && await VerifiedGameFileDownloader.VerifyAsync(path, file, token).ConfigureAwait(false)
            ? (size, size)
            : (0, 0);
    }

    private static Operation CreateOperation(string installPath, GameInstallPlan plan)
    {
        string root = NormalizeRoot(installPath);
        if (plan.Profile.Length == 0 || !plan.Profile.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
            !IsHex(plan.IndexSha256, 64) || plan.Files.Count == 0)
            throw new InvalidDataException("The game install plan identity is invalid.");

        var cdnRoots = new List<Uri>(plan.BaseUris.Count);
        foreach (Uri candidate in plan.BaseUris)
        {
            if (!GameCdn.TryNormalizeRoot(candidate, out Uri? cdnRoot))
                throw new InvalidDataException("The game install plan requires clean HTTPS CDN base URIs.");
            if (!cdnRoots.Contains(cdnRoot))
                cdnRoots.Add(cdnRoot);
        }
        if (cdnRoots.Count == 0)
            throw new InvalidDataException("The game install plan does not contain a publisher CDN.");

        string stageDirectory = $"{Workspace}/stage/{plan.Profile}/{plan.Version}/{plan.IndexSha256.ToLowerInvariant()}";
        var files = new FileLocation[plan.Files.Count];
        var pathKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in plan.Directories)
        {
            ValidateRelativePath(path);
            if (!pathKinds.TryAdd(path, true))
                throw new InvalidDataException("The game install plan contains duplicate paths.");
            directories.Add(path);
        }

        for (int index = 0; index < files.Length; index++)
        {
            GameFile entry = plan.Files[index];
            ValidateRelativePath(entry.InstallPath);
            ValidateRelativePath(entry.RemotePath, remote: true);
            if (entry.Size < 0 || !entry.HasValidChecksum || !pathKinds.TryAdd(entry.InstallPath, false))
                throw new InvalidDataException("The game install plan contains an invalid or duplicate file.");

            string remotePath = string.Join('/', entry.RemotePath.Split('/').Select(Uri.EscapeDataString));
            string name = index.ToString("x8", CultureInfo.InvariantCulture) + ".part";
            files[index] = new FileLocation(entry, $"{stageDirectory}/{name}", remotePath);
        }

        foreach (string path in pathKinds.Keys)
        {
            for (int separator = path.LastIndexOf('/'); separator > 0; separator = path.LastIndexOf('/', separator - 1))
            {
                string parent = path[..separator];
                if (pathKinds.TryGetValue(parent, out bool isDirectory) && !isDirectory)
                    throw new InvalidDataException("A game index file is also used as a parent directory.");
                directories.Add(parent);
            }
        }
        return new Operation(root, stageDirectory, cdnRoots, files, directories);
    }

    private static HashSet<string> ReadOwnedFiles(Operation operation, GameInstallPlan plan)
    {
        Marker? pending = ReadMarker(operation.Root, PendingMarker, hasFiles: false);
        // A pending commit grants no file ownership; a later release may proceed only under the existing inventory checks.
        if (pending is not null && (!pending.Profile.Equals(plan.Profile, StringComparison.OrdinalIgnoreCase) ||
            plan.Version < pending.Version))
            throw new InvalidOperationException("The pending game version belongs to another region or is newer than the selected version.");

        Marker? installed = ReadMarker(operation.Root, InstalledMarker, hasFiles: true);
        if (installed is not null && !installed.Profile.Equals(plan.Profile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game directory is managed by a different region.");
        if (installed is not null && plan.Version < installed.Version)
            throw new InvalidOperationException("The selected game version is older than the installed version.");
        return installed?.Files ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Marker? ReadMarker(string root, string relativePath, bool hasFiles)
    {
        string path = Resolve(root, relativePath);
        if (GetAttributes(path) is null)
            return null;

        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !ReadString(data, "profile", out string profile) || profile.Length == 0 ||
            !ReadString(data, "version", out string versionValue) || !Version.TryParse(versionValue, out Version? version) ||
            !ReadString(data, "indexSha256", out string indexHash) || !IsHex(indexHash, 64))
            throw new InvalidDataException("The game install marker is invalid.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasFiles)
        {
            if (!data.TryGetProperty("files", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                throw new InvalidDataException("The installed game marker has no file inventory.");
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.String || row.GetString() is not { } relative || !files.Add(relative))
                    throw new InvalidDataException("The installed game marker file inventory is invalid.");
                ValidateRelativePath(relative);
            }
        }
        return new Marker(profile, version, indexHash, files);
    }

    private static void WriteMarker(string root, string target, GameInstallPlan plan, HashSet<string>? files)
    {
        string temporary = Resolve(root, $"{Workspace}/{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("profile", plan.Profile);
                writer.WriteString("version", plan.Version.ToString());
                writer.WriteString("indexSha256", plan.IndexSha256.ToLowerInvariant());
                if (files is not null)
                {
                    writer.WriteStartArray("files");
                    foreach (string path in files.Order(StringComparer.OrdinalIgnoreCase))
                        writer.WriteStringValue(path);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Resolve(root, target), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The game installation directory must be an absolute path.", nameof(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(root) == root)
            throw new ArgumentException("A volume root cannot be used as the game installation directory.", nameof(path));
        return root;
    }

    private static string Resolve(string root, string relativePath)
    {
        string path = ResolvePath(root, relativePath);
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            _ = GetAttributes(current);
        return path;
    }

    private static string ResolvePath(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A game installation path escaped its root.");
        return path;
    }

    // Reuse parent probes during a read-only scan. Writes always resolve the current path again.
    private static FileAttributes? Probe(string path, Dictionary<string, bool> parentDirectories)
    {
        string parent = Path.GetDirectoryName(path)!;
        if (!parentDirectories.TryGetValue(parent, out bool exists))
        {
            exists = GetAttributes(parent) is { } attributes && (attributes & FileAttributes.Directory) != 0;
            for (string? ancestor = Path.GetDirectoryName(parent); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                _ = GetAttributes(ancestor);
            parentDirectories.Add(parent, exists);
        }
        return exists ? GetAttributes(path) : null;
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A game installation path traverses a symbolic link or directory junction.");
            return attributes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void ValidateRelativePath(string path, bool remote = false)
    {
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => char.IsControl(character) || "\\:*?\"<>|#%".Contains(character)) ||
                (!remote && (segment.Length > 255 || IsWindowsDevice(segment))))
                throw new InvalidDataException("The game install plan contains an unsafe relative path.");
        }
        if (!remote && (path.Equals(Workspace, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Workspace + '/', StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A game install path conflicts with the plugin workspace.");
    }

    private static bool IsWindowsDevice(string segment)
    {
        string stem = segment.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
             stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }

    private static bool ReadString(JsonElement entry, string name, out string value)
    {
        value = entry.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? string.Empty
            : string.Empty;
        return value.Length != 0;
    }

    private static bool IsHex(string value, int size) => value.Length == size && value.All(char.IsAsciiHexDigit);

    private sealed record Operation(string Root, string StageDirectory, List<Uri> CdnRoots, FileLocation[] Files, HashSet<string> Directories);
    private sealed record FileLocation(GameFile Entry, string PartialPath, string EscapedRemotePath);
    private sealed record DownloadJob(FileLocation File, long Cached);
    private sealed record DownloadBatch(List<DownloadJob> Jobs, long Total, long Downloaded, long RemainingStagingBytes);
    private sealed record Marker(string Profile, Version Version, string IndexSha256, HashSet<string> Files);
}
