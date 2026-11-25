using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class ChinaLauncherBootstrap
{
    private const string Publisher = "上海蛮奈网络科技有限公司";
    private const int MaxInstallerBytes = 320 * 1024 * 1024;
    private const int CertificateTrailerLimit = 1024 * 1024;
    private const int DirectoryEndLength = 22;
    private const string CachePrefix = "china-launcher-";
    private static readonly Uri[] DownloadFeeds =
    [
        new("https://api-grp.manjuu.com/activity/web/download/info?gameId=35"),
        new("https://api-grp-l2.manjuu.com/activity/web/download/info?gameId=35")
    ];
    private static readonly SemaphoreSlim CacheLock = new(1);
    private static readonly string DeviceId = Guid.NewGuid().ToString();

    // The signed installer provides guest distribution configuration on a fresh machine.
    internal static async Task<ChinaGameReleaseSource.LauncherConfiguration> LoadAsync(HttpClient http,
        CancellationToken token, string? cacheDirectory = null)
    {
        Uri download = await FindDownloadAsync(http, token).ConfigureAwait(false);
        return await LoadAsync(http, download, CachePrefix, Publisher, ChinaGameReleaseSource.IsChinaConfiguration,
            token, cacheDirectory).ConfigureAwait(false);
    }

    internal static async Task<ChinaGameReleaseSource.LauncherConfiguration> LoadAsync(HttpClient http, Uri download,
        string cachePrefix, string publisher, Func<ChinaGameReleaseSource.LauncherConfiguration, bool> matchesChannel,
        CancellationToken token, string? cacheDirectory = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(cacheDirectory) && string.IsNullOrEmpty(localAppData))
            throw new InvalidDataException("The publisher launcher cache directory is unavailable.");
        string cache = Path.GetFullPath(cacheDirectory ?? Path.Combine(localAppData, "CollapseLauncher", "AzurPromilia", "PublisherLauncher"));
        await CacheLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cache);
            string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(download.AbsolutePath)));
            string current = Path.Combine(cache, $"{cachePrefix}{identity}.exe");
            ChinaGameReleaseSource.LauncherConfiguration configuration;
            if (File.Exists(current))
            {
                try
                {
                    configuration = RequireConfiguration(current, publisher, matchesChannel);
                    token.ThrowIfCancellationRequested();
                    PruneOldInstallers(cache, current, cachePrefix);
                    return configuration;
                }
                catch (InvalidDataException)
                {
                    // Replace the cached file only after a fresh download passes publisher verification.
                }
            }

            string temporary = Path.Combine(cache, $".{Guid.NewGuid():N}.partial.exe");
            try
            {
                await DownloadAsync(http, download, temporary, token).ConfigureAwait(false);
                configuration = RequireConfiguration(temporary, publisher, matchesChannel);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, current, overwrite: true);
                PruneOldInstallers(cache, current, cachePrefix);
                return configuration;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }

    internal static async Task<Uri> FindDownloadAsync(HttpClient http, CancellationToken token)
    {
        Exception? lastFailure = null;
        foreach (Uri feed in DownloadFeeds)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, feed);
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                request.Headers.Add("timestamp", timestamp);
                request.Headers.Add("sign", Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "gameId=35"))));
                request.Headers.Add("grp-app-id", "350010");
                request.Headers.Add("grp-channel-code", "C000");
                request.Headers.Add("grp-sub-channel-code", "SC000");
                request.Headers.Add("device-id", DeviceId);
                request.Headers.Add("Origin", "https://azurpromilia.manjuu.com");
                request.Headers.Referrer = new Uri("https://azurpromilia.manjuu.com/home/");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using JsonDocument json = await JsonDocument.ParseAsync(content, cancellationToken: token).ConfigureAwait(false);
                JsonElement value = json.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out JsonElement code) &&
                    code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int status) && status == 0 &&
                    value.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("nativeLauncherDownloadUrl", out JsonElement url) && url.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? download) && IsOfficialDownload(download))
                    return download;

                throw new InvalidDataException("The publisher did not provide a mainland China launcher download.");
            }
            catch (Exception error) when (GameCdn.IsTransferFailure(error, token) || error is JsonException)
            {
                lastFailure = error;
            }
        }

        throw new InvalidDataException("The mainland China publisher launcher download is unavailable.", lastFailure);
    }

    private static bool IsOfficialDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.IdnHost is "syncstation.manjuu.com" or "l2-syncstation.manjuu.com" && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsolutePath.StartsWith("/azurpromilia/launcher/c000/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static async Task DownloadAsync(HttpClient http, Uri download, string destination, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, download);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (response.StatusCode != System.Net.HttpStatusCode.OK || length is <= 0 or > MaxInstallerBytes ||
            response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The publisher installer response is invalid.");

        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        int total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > MaxInstallerBytes)
                throw new InvalidDataException("The publisher installer exceeds its transfer size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (total == 0 || (length.HasValue && total != length.Value))
            throw new InvalidDataException("The publisher installer download is incomplete.");
    }

    private static ChinaGameReleaseSource.LauncherConfiguration RequireConfiguration(string installer, string publisher,
        Func<ChinaGameReleaseSource.LauncherConfiguration, bool> matchesChannel)
    {
        ChinaGameReleaseSource.LauncherConfiguration configuration = ReadInstallerConfiguration(installer, publisher);
        if (!matchesChannel(configuration))
            throw new InvalidDataException("The publisher installer configuration is for a different channel.");
        return configuration;
    }

    internal static ChinaGameReleaseSource.LauncherConfiguration ReadInstallerConfiguration(string installer, string publisher)
    {
        using FileStream source = File.OpenRead(installer);
        if (source.Length <= 0 || source.Length > MaxInstallerBytes || !Authenticode.IsSignedBy(installer, publisher))
            throw new InvalidDataException("The launcher installer failed publisher signature verification.");

        using var executable = new PEReader(source, PEStreamOptions.LeaveOpen);
        DirectoryEntry certificate = executable.PEHeaders.PEHeader?.CertificateTableDirectory ?? default;
        if (certificate.RelativeVirtualAddress <= 0 || certificate.Size <= 0 ||
            (long)certificate.RelativeVirtualAddress + certificate.Size != source.Length)
            throw new InvalidDataException("The launcher installer has an unsupported signature layout.");

        var (start, length) = LocateEmbeddedArchive(source, certificate.RelativeVirtualAddress);
        using var mapping = MemoryMappedFile.CreateFromFile(source, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        using MemoryMappedViewStream view = mapping.CreateViewStream(start, length, MemoryMappedFileAccess.Read);
        using var zip = new ZipArchive(view, ZipArchiveMode.Read);
        using MemoryStream archive = ReadEntry(zip, ChinaGameReleaseSource.ArchivePath, 64 * 1024 * 1024);
        using MemoryStream native = ReadEntry(zip, ChinaGameReleaseSource.NativeLibraryPath, 32 * 1024 * 1024);
        using MemoryStream configuration = ReadEntry(zip, ChinaGameReleaseSource.ApplicationConfigPath, 16 * 1024);
        return ChinaGameReleaseSource.ReadLauncherConfiguration(archive, native, configuration);
    }

    private static MemoryStream ReadEntry(ZipArchive archive, string path, int maxBytes)
    {
        ZipArchiveEntry? entry = archive.GetEntry(path);
        if (entry is null || entry.Length <= 0 || entry.Length > maxBytes)
            throw new InvalidDataException("The launcher installer has an invalid configuration entry.");

        using Stream input = entry.Open();
        var bytes = new byte[(int)entry.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw new InvalidDataException("The launcher installer configuration exceeds its declared size.");
        return new MemoryStream(bytes, writable: false);
    }

    internal static (long Start, long Length) LocateEmbeddedArchive(Stream installer, long payloadLength)
    {
        if (payloadLength <= 0 || payloadLength > installer.Length)
            throw new InvalidDataException("The launcher installer payload length is invalid.");
        int scanLength = (int)Math.Min(payloadLength, CertificateTrailerLimit + ushort.MaxValue + DirectoryEndLength);
        long scanStart = payloadLength - scanLength;
        byte[] tail = new byte[scanLength];
        installer.Position = scanStart;
        installer.ReadExactly(tail);
        Span<byte> signature = stackalloc byte[4];

        // The embedded ZIP must end inside the Authenticode-verified payload.
        for (int end = tail.Length - DirectoryEndLength; end >= 0; end--)
        {
            ReadOnlySpan<byte> directoryEnd = tail.AsSpan(end, DirectoryEndLength);
            if (!directoryEnd[..4].SequenceEqual("PK\u0005\u0006"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(directoryEnd[4..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(directoryEnd[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(directoryEnd[10..]))
                continue;

            long centralDirectory = scanStart + end - BinaryPrimitives.ReadUInt32LittleEndian(directoryEnd[12..]);
            long start = centralDirectory - BinaryPrimitives.ReadUInt32LittleEndian(directoryEnd[16..]);
            long archiveEnd = scanStart + end + DirectoryEndLength + BinaryPrimitives.ReadUInt16LittleEndian(directoryEnd[20..]);
            if (start < 0 || centralDirectory < start + 4 || archiveEnd > payloadLength)
                continue;

            installer.Position = start;
            installer.ReadExactly(signature);
            if (!signature.SequenceEqual("PK\u0003\u0004"u8))
                continue;
            installer.Position = centralDirectory;
            installer.ReadExactly(signature);
            if (signature.SequenceEqual("PK\u0001\u0002"u8))
                return (start, archiveEnd - start);
        }

        throw new InvalidDataException("The launcher installer does not contain a supported archive.");
    }

    private static void PruneOldInstallers(string cache, string current, string cachePrefix)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(cache, $"{cachePrefix}*.exe")
                         .Where(path => !path.Equals(current, StringComparison.OrdinalIgnoreCase) && IsCacheEntry(path, cachePrefix))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(1))
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A cached installer may still be in use in another launcher process.
        }
    }

    private static bool IsCacheEntry(string path, string cachePrefix)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == cachePrefix.Length + 64 && name[cachePrefix.Length..].All(char.IsAsciiHexDigit);
    }
}
