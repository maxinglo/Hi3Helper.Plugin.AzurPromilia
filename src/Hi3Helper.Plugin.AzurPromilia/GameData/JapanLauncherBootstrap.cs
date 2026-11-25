using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class JapanLauncherBootstrap
{
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxInstallerBytes = 320 * 1024 * 1024;
    private const int MaxArchiveBytes = 128 * 1024 * 1024;
    private const int SevenZipHeaderSize = 32;
    private const string CachePrefix = "japan-launcher-";
    private static readonly Uri DownloadRoot = new("https://launcher-pkg-azupro-jp.yo-star.com/install_pkg/game_launcher/AZUPRO_JP/");
    private static readonly SemaphoreSlim CacheLock = new(1);

    internal static async Task<JapanGameReleaseSource.LauncherConfiguration> LoadAsync(HttpClient http,
        CancellationToken token, string? cacheDirectory = null)
    {
        InstallerRelease release = await FindInstallerAsync(http, token).ConfigureAwait(false);
        string cache = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CollapseLauncher", "AzurPromilia", "PublisherLauncher");
        await CacheLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cache);
            string current = Path.Combine(cache, GetCacheName(release));
            if (File.Exists(current))
            {
                try
                {
                    await VerifyInstallerAsync(current, release, token).ConfigureAwait(false);
                    return await ReadInstallerConfigurationAsync(current, token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // Keep the previous cache entry until its replacement has passed validation.
                }
            }

            string temporary = Path.Combine(cache, $".{Guid.NewGuid():N}.partial.exe");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, release.Download);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != System.Net.HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength is long expected && expected != release.Size ||
                    response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Yostar's Japanese launcher installer response is invalid.");

                await using (Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await CopyBoundedAsync(input, output, release.Size, token).ConfigureAwait(false);

                await VerifyInstallerAsync(temporary, release, token).ConfigureAwait(false);
                var configuration = await ReadInstallerConfigurationAsync(temporary, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, current, overwrite: true);
                PruneCache(cache, current);
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

    internal static string GetCacheName(InstallerRelease release) =>
        $"{CachePrefix}{Convert.ToHexStringLower(release.Sha512)}.exe";

    internal static async Task<InstallerRelease> FindInstallerAsync(HttpClient http, CancellationToken token)
    {
        using HttpResponseMessage response = await http.GetAsync(new Uri(DownloadRoot, "latest.yml"),
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxMetadataBytes, token).ConfigureAwait(false);
        return ParseInstallerRelease(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }

    internal static InstallerRelease ParseInstallerRelease(string metadata)
    {
        string[] lines = metadata.Split('\n', StringSplitOptions.TrimEntries);
        string versionText = ReadField(lines, "version: ");
        string path = ReadField(lines, "path: ");
        string checksum = ReadField(metadata.Split('\n').Select(line => line.TrimEnd()), "sha512: ");
        string sizeText = ReadField(lines, "size: ");
        byte[] sha512 = new byte[64];
        if (!Version.TryParse(versionText, out Version? version) || path != $"AZUPRO_JP_Gamelauncher-{version}-setup.exe" ||
            !Convert.TryFromBase64String(checksum, sha512, out int digestLength) || digestLength != sha512.Length ||
            !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
            size is <= 0 or > MaxInstallerBytes)
            throw new InvalidDataException("Yostar's Japanese launcher installer metadata is invalid.");
        return new InstallerRelease(new Uri(DownloadRoot, path), sha512, size);
    }

    private static string ReadField(IEnumerable<string> lines, string prefix)
    {
        string[] matching = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1)
            throw new InvalidDataException("Yostar's Japanese launcher installer metadata is ambiguous.");
        return matching[0][prefix.Length..];
    }

    private static async Task VerifyInstallerAsync(string path, InstallerRelease release, CancellationToken token)
    {
        await using FileStream installer = File.OpenRead(path);
        if (installer.Length != release.Size ||
            !CryptographicOperations.FixedTimeEquals(await SHA512.HashDataAsync(installer, token).ConfigureAwait(false), release.Sha512))
            throw new InvalidDataException("Yostar's Japanese launcher installer failed its published size or checksum check.");
    }

    // Read only the launcher configuration from the publisher's signed archive. Do not start the installer.
    internal static async Task<JapanGameReleaseSource.LauncherConfiguration> ReadInstallerConfigurationAsync(string installer,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using FileStream image = File.OpenRead(installer);
        if (image.Length is <= 0 or > MaxInstallerBytes || !Authenticode.IsSignedBy(installer, "Yostar, Inc."))
            throw new InvalidDataException("The Japanese publisher launcher installer failed signature verification.");

        using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
        PEHeader header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Yostar's installer has no PE header.");
        long overlayStart = Math.Max(header.SizeOfHeaders,
            pe.PEHeaders.SectionHeaders.Max(section => (long)section.PointerToRawData + section.SizeOfRawData));
        DirectoryEntry certificate = header.CertificateTableDirectory;
        long overlayLength = certificate.RelativeVirtualAddress - overlayStart;
        if (overlayStart < 0 || overlayLength < SevenZipHeaderSize || certificate.Size <= 0 ||
            (long)certificate.RelativeVirtualAddress + certificate.Size > image.Length)
            throw new InvalidDataException("Yostar's installer has no signed archive payload.");

        using var mapping = MemoryMappedFile.CreateFromFile(image, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        (long offset, long length) = LocateArchive(mapping, overlayStart, overlayLength);
        string temporary = Path.GetTempFileName();
        try
        {
            using (MemoryMappedViewStream archive = mapping.CreateViewStream(offset, length, MemoryMappedFileAccess.Read))
            await using (FileStream output = File.Open(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None))
                await archive.CopyToAsync(output, token).ConfigureAwait(false);

            return await ReadArchiveConfigurationAsync(temporary, token).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static unsafe (long Offset, long Length) LocateArchive(MemoryMappedFile mapping, long start, long length)
    {
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor(start, length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            var payload = new ReadOnlySpan<byte>(pointer + view.PointerOffset, checked((int)length));
            (int offset, int size) = LocateArchive(payload);
            return (start + offset, size);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    internal static (int Offset, int Length) LocateArchive(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> marker = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0x00, 0x04];
        (int Offset, int Length)? result = null;
        int offset = 0;
        while (payload.Length - offset >= SevenZipHeaderSize)
        {
            int markerOffset = payload[offset..].IndexOf(marker);
            if (markerOffset < 0)
                break;
            offset += markerOffset;
            ReadOnlySpan<byte> archive = payload[offset..];
            if (archive.Length < SevenZipHeaderSize)
                break;
            ulong nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(archive[12..]);
            ulong nextLength = BinaryPrimitives.ReadUInt64LittleEndian(archive[20..]);
            if (nextOffset <= (ulong)archive.Length - SevenZipHeaderSize &&
                nextLength is > 0 and <= 1024 * 1024 && nextLength <= (ulong)archive.Length - SevenZipHeaderSize - nextOffset &&
                Crc32(archive.Slice(12, 20)) == BinaryPrimitives.ReadUInt32LittleEndian(archive[8..]))
            {
                int headerOffset = SevenZipHeaderSize + (int)nextOffset;
                if (Crc32(archive.Slice(headerOffset, (int)nextLength)) == BinaryPrimitives.ReadUInt32LittleEndian(archive[28..]))
                {
                    if (result.HasValue)
                        throw new InvalidDataException("Yostar's Japanese launcher installer has multiple archive payloads.");
                    result = (offset, headerOffset + (int)nextLength);
                }
            }
            offset++;
        }
        return result ?? throw new InvalidDataException("Yostar's Japanese launcher installer archive format is unsupported.");
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }

    private static async Task<JapanGameReleaseSource.LauncherConfiguration> ReadArchiveConfigurationAsync(string archive,
        CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-xOf");
        start.ArgumentList.Add(archive);
        start.ArgumentList.Add(ChinaGameReleaseSource.ArchivePath);
        using Process reader = Process.Start(start) ?? throw new InvalidDataException("The Windows archive reader could not start.");
        using var output = new MemoryStream();
        Task drainError = reader.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await CopyBoundedAsync(reader.StandardOutput.BaseStream, output, MaxArchiveBytes, token).ConfigureAwait(false);
            await reader.WaitForExitAsync(token).ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            if (reader.ExitCode != 0 || output.Length == 0)
                throw new InvalidDataException("Yostar's Japanese launcher archive did not contain a readable configuration.");
            output.Position = 0;
            return JapanGameReleaseSource.ReadLauncherConfiguration(output);
        }
        finally
        {
            if (!reader.HasExited)
            {
                try { reader.Kill(); }
                catch (InvalidOperationException) { }
                await reader.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > maximum)
                throw new InvalidDataException("The publisher launcher payload exceeds the expected transfer size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static void PruneCache(string cache, string current)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(cache, $"{CachePrefix}*.exe")
                         .Where(path => !path.Equals(current, StringComparison.OrdinalIgnoreCase) && IsCacheEntry(path))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(1))
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another launcher process may still be reading an older version.
        }
    }

    private static bool IsCacheEntry(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == CachePrefix.Length + 128 &&
            name.AsSpan(CachePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    internal sealed record InstallerRelease(Uri Download, byte[] Sha512, long Size);
}
