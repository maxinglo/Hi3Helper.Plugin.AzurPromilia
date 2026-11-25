using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class ChinaLauncherContentCache
{
    private const int IndexHeaderSize = 368;
    private const int BlockHeaderSize = 8192;
    private const int EntryHeaderSize = 96;
    private const int DefaultBucketCount = 65536;
    private const int MaxBucketCount = 1024 * 1024;
    private const int MaxEntries = 16384;
    private const int MaxBodySize = 128 * 1024;
    private const int LauncherIdLength = 32;

    private static ReadOnlySpan<byte> PrimaryKey => "1/0/https://api-grp.manjuu.com/pc-launcher/client/launcher/contentconfig?launcherId="u8;
    private static ReadOnlySpan<byte> SecondaryKey => "1/0/https://api-grp-l2.manjuu.com/pc-launcher/client/launcher/contentconfig?launcherId="u8;

    // Read the installed China launcher's public display response from its cache and use the website if it is unavailable.
    internal static LauncherContentSnapshot? Read(Func<string?> findVerifiedLauncherDirectory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (findVerifiedLauncherDirectory() is not { } directory || !Path.IsPathFullyQualified(directory))
                return null;

            string cacheDirectory = Path.Combine(directory, "AppData", "Cache", "Cache_Data");
            using FileStream? index = Open(Path.Combine(cacheDirectory, "index"));
            Span<byte> header = stackalloc byte[IndexHeaderSize];
            if (index is null || !ReadAt(index, 0, header) || ReadUInt(header, 0) != 0xc103cac3 ||
                ReadUInt(header, 4) != 0x30000 || ReadUInt(header, 56) != 0)
                return null;

            uint bucketCount = ReadUInt(header, 28);
            bucketCount = bucketCount == 0 ? DefaultBucketCount : bucketCount;
            if (bucketCount > MaxBucketCount || (bucketCount & (bucketCount - 1)) != 0)
                return null;

            byte[] table = new byte[bucketCount * sizeof(uint)];
            if (!ReadAt(index, IndexHeaderSize, table))
                return null;

            using var blocks = new CacheBlocks(cacheDirectory);
            return FindLatestContent(table, bucketCount - 1, blocks, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static LauncherContentSnapshot? FindLatestContent(ReadOnlySpan<byte> table, uint bucketMask, CacheBlocks blocks, CancellationToken token)
    {
        var visited = new HashSet<uint>();
        LauncherContentSnapshot? latest = null;
        long latestCreation = 0;
        Span<byte> entry = stackalloc byte[EntryHeaderSize];
        Span<byte> confirmedEntry = stackalloc byte[EntryHeaderSize];
        for (int offset = 0; offset < table.Length; offset += sizeof(uint))
        {
            if ((offset & 0x3fff) == 0)
                token.ThrowIfCancellationRequested();

            uint address = ReadUInt(table, offset);
            while (address != 0 && visited.Add(address))
            {
                token.ThrowIfCancellationRequested();
                if (visited.Count > MaxEntries)
                    return null;
                if ((address >> 28) != 0xa ||
                    !blocks.TryLocate(address, out FileStream? entryFile, out int position, out int capacity) ||
                    !ReadAt(entryFile!, position, entry))
                    break;

                address = ReadUInt(entry, 4);
                long creation = BinaryPrimitives.ReadInt64LittleEndian(entry[24..]);
                if (creation <= latestCreation || (ReadUInt(entry, 0) & bucketMask) != (uint)(offset / sizeof(uint)) ||
                    ReadUInt(entry, 20) != 0 || ReadUInt(entry, 36) != 0 || ReadUInt(entry, 72) != 0 ||
                    !IsContentKey(entryFile!, position + EntryHeaderSize, ReadUInt(entry, 32), capacity - EntryHeaderSize))
                    continue;

                // Chromium's stream 1 is the response body. Stream 0 can contain HTTP headers.
                uint size = ReadUInt(entry, 44);
                uint bodyAddress = ReadUInt(entry, 60);
                if (size == 0 || size > MaxBodySize || blocks.ReadBody(bodyAddress, (int)size) is not { } body ||
                    !ReadAt(entryFile!, position, confirmedEntry) || !entry.SequenceEqual(confirmedEntry))
                    continue;

                try
                {
                    if (ChinaLauncherContentParser.Parse(body) is { } snapshot)
                    {
                        latest = snapshot;
                        latestCreation = creation;
                    }
                }
                catch (JsonException) { }
            }
        }

        return latest;
    }

    private static bool IsContentKey(FileStream file, int position, uint length, int capacity)
    {
        ReadOnlySpan<byte> prefix = length == PrimaryKey.Length + LauncherIdLength ? PrimaryKey
            : length == SecondaryKey.Length + LauncherIdLength ? SecondaryKey : [];
        if (prefix.IsEmpty || length >= capacity)
            return false;
        Span<byte> candidate = stackalloc byte[prefix.Length];
        if (!ReadAt(file, position, candidate) || !candidate.SequenceEqual(prefix))
            return false;

        Span<byte> launcherId = stackalloc byte[LauncherIdLength + 1];
        if (!ReadAt(file, position + prefix.Length, launcherId) || launcherId[^1] != 0)
            return false;
        foreach (byte value in launcherId[..LauncherIdLength])
            if (value is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F'))
                return false;
        return true;
    }

    private static FileStream? Open(string path) => LauncherBackgroundCache.IsLocalFile(path)
        ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess)
        : null;

    private static bool ReadAt(FileStream file, int position, Span<byte> bytes)
    {
        if ((long)position + bytes.Length > file.Length)
            return false;
        file.Position = position;
        return file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) == bytes.Length;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private sealed class CacheBlocks(string directory) : IDisposable
    {
        private const int AllocationMapOffset = 80;
        private readonly Dictionary<int, BlockFile?> _files = [];

        internal byte[]? ReadBody(uint address, int length)
        {
            if ((address >> 28) == 8)
            {
                string fileName = "f_" + (address & 0x0fffffff).ToString("x6", CultureInfo.InvariantCulture);
                using FileStream? external = Open(Path.Combine(directory, fileName));
                return external?.Length == length ? ReadBytes(external, 0, length) : null;
            }

            return TryLocate(address, out FileStream? file, out int position, out int capacity) && length <= capacity
                ? ReadBytes(file!, position, length)
                : null;
        }

        internal bool TryLocate(uint address, out FileStream? file, out int position, out int capacity)
        {
            (file, position, capacity) = (null, 0, 0);
            int blockSize = (address >> 28) switch { 0xa => 256, 0xb => 1024, 0xc => 4096, _ => 0 };
            if (blockSize == 0 || (address & 0x0c000000) != 0)
                return false;

            int fileNumber = (int)(address >> 16) & 0xff;
            if (!_files.TryGetValue(fileNumber, out BlockFile? blockFile))
                _files[fileNumber] = blockFile = OpenBlockFile(fileNumber);

            int start = (int)(address & 0xffff);
            int count = (int)(address >> 24 & 3) + 1;
            if (blockFile is null || blockSize != blockFile.BlockSize || start + count > blockFile.BlockCount)
                return false;

            // Verify the allocation map before using a block address from an index or entry.
            int mapByte = start / 8;
            Span<byte> allocated = stackalloc byte[(start + count - 1) / 8 - mapByte + 1];
            if (!ReadAt(blockFile.Stream, AllocationMapOffset + mapByte, allocated))
                return false;
            for (int block = start; block < start + count; block++)
                if ((allocated[block / 8 - mapByte] & (1 << (block % 8))) == 0)
                    return false;

            capacity = blockSize * count;
            position = BlockHeaderSize + blockSize * start;
            file = blockFile.Stream;
            return (long)position + capacity <= file.Length;
        }

        private BlockFile? OpenBlockFile(int number)
        {
            string path = Path.Combine(directory, "data_" + number.ToString(CultureInfo.InvariantCulture));
            FileStream? stream = Open(path);
            if (stream is null)
                return null;

            try
            {
                Span<byte> header = stackalloc byte[24];
                if (!ReadAt(stream, 0, header) || ReadUInt(header, 0) != 0xc104cac3 || ReadUInt(header, 4) != 0x20000 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != number ||
                    ReadUInt(header, 20) is 0 or > (BlockHeaderSize - AllocationMapOffset) * 8)
                    return null;

                var result = new BlockFile(stream, (int)ReadUInt(header, 12), (int)ReadUInt(header, 20));
                stream = null;
                return result;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private static byte[]? ReadBytes(FileStream file, int position, int length)
        {
            byte[] bytes = new byte[length];
            return ReadAt(file, position, bytes) ? bytes : null;
        }

        public void Dispose()
        {
            foreach (BlockFile? file in _files.Values)
                file?.Stream.Dispose();
        }

        private sealed record BlockFile(FileStream Stream, int BlockSize, int BlockCount);
    }
}
