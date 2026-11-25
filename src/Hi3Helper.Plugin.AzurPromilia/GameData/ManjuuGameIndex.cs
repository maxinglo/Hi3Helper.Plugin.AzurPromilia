using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal sealed record GameFile(long Size, string Md5, string InstallPath, string RemotePath);

internal sealed record ManjuuGameIndex(
    IReadOnlyList<GameFile> Files, IReadOnlyList<string> Directories, long TotalBytes)
{
    private const int MaxPathLength = 32767;
    private const string EmptyDigest = "00000000000000000000000000000000";

    internal static ManjuuGameIndex ReadEncrypted(Stream source, ReadOnlySpan<byte> formatKey, ReadOnlySpan<byte> iv)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (formatKey.Length != 24 || iv.Length != 16)
            throw new ArgumentException("The game index decoder requires a 24-byte format key and a 16-byte initialization vector.");

        byte[] keyBytes = formatKey.ToArray();
        byte[] ivBytes = iv.ToArray();
        try
        {
            using var base64Transform = new FromBase64Transform(FromBase64TransformMode.IgnoreWhiteSpaces);
            using var binary = new CryptoStream(source, base64Transform, CryptoStreamMode.Read, leaveOpen: true);
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using ICryptoTransform decryptor = aes.CreateDecryptor(keyBytes, ivBytes);
            using var decrypted = new CryptoStream(binary, decryptor, CryptoStreamMode.Read);
            using var reader = new StreamReader(decrypted, new UTF8Encoding(false, true));
            return Read(reader);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("The game index could not be decoded.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(ivBytes);
        }
    }

    // The mainland China and Taiwan clients use the same index layout, with separate release feeds.
    internal static ManjuuGameIndex Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string[] header = ReadFields(reader, "index header");
        if (header.Length != 3 || !TryReadCount(header[0], out int recordCount) ||
            !TryReadCount(header[1], out int fileCount) || !TryReadCount(header[2], out int directoryCount) ||
            recordCount != (long)fileCount + directoryCount)
        {
            throw new InvalidDataException("Invalid game index counts.");
        }

        var files = new List<GameFile>(Math.Min(fileCount, 32768));
        var directories = new List<string>(Math.Min(directoryCount, 32768));
        var pathKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        for (int index = 0; index < recordCount; index++)
        {
            string[] fields = ReadFields(reader, $"item {index}");
            bool isDirectory = fields.Length == 4;
            if (fields.Length is not (4 or 5) || !TryReadCount(fields[0], out int ordinal) || ordinal != index ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                !IsMd5(fields[2]))
            {
                throw new InvalidDataException($"Invalid game index item {index}.");
            }

            string installPath = ReadPath(fields[3], isRemote: false);
            if (!pathKinds.TryAdd(installPath, isDirectory))
                throw new InvalidDataException($"Duplicate game index path at item {index}.");

            if (isDirectory)
            {
                if (size != 0 || fields[2] != EmptyDigest || directories.Count == directoryCount)
                    throw new InvalidDataException($"Invalid game directory at item {index}.");
                directories.Add(installPath);
                continue;
            }

            if (files.Count == fileCount || totalBytes > long.MaxValue - size)
                throw new InvalidDataException($"Game index size or file count is invalid at item {index}.");
            string remotePath = ReadPath(fields[4], isRemote: true);
            files.Add(new(size, fields[2].ToLowerInvariant(), installPath, remotePath));
            totalBytes += size;
        }

        if (files.Count != fileCount || directories.Count != directoryCount || reader.ReadLine() is not null)
            throw new InvalidDataException("The game index record count does not match its header.");

        foreach (string path in pathKinds.Keys)
        {
            for (int separator = path.LastIndexOf('/'); separator >= 0;
                 separator = separator == 0 ? -1 : path.LastIndexOf('/', separator - 1))
            {
                if (pathKinds.TryGetValue(path[..separator], out bool isDirectory) && !isDirectory)
                    throw new InvalidDataException("A game index file is also used as a parent directory.");
            }
        }

        return new(files.ToArray(), directories.ToArray(), totalBytes);
    }

    private static string[] ReadFields(TextReader reader, string context)
    {
        string line = reader.ReadLine() ?? throw new InvalidDataException($"Missing {context}.");
        if (line.Length > MaxPathLength * 2 + 96)
            throw new InvalidDataException($"Too many characters in {context}.");

        if (line.EndsWith(','))
            line = line[..^1];
        return line.Split(',');
    }

    private static bool TryReadCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count);

    private static bool IsMd5(string value) =>
        value.Length == 32 && value.All(static character => char.IsAsciiHexDigit(character));

    private static string ReadPath(string value, bool isRemote)
    {
        if (value.Length is 0 or > MaxPathLength || (isRemote && value.Contains('\\')))
            throw new InvalidDataException("The game index contains an invalid path.");

        string path = value.Replace('\\', '/');
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(static character => char.IsControl(character) || character is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '#' or '%') ||
                (!isRemote && (segment.Length > 255 || IsWindowsDevice(segment))))
            {
                throw new InvalidDataException("The game index contains an unsafe path.");
            }
        }

        if (!isRemote && (path.Equals(".collapse-azurpromilia", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(".collapse-azurpromilia/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A game file uses the plugin's working directory.");
        }

        return path;
    }

    private static bool IsWindowsDevice(string segment)
    {
        string stem = segment.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
             stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }
}
