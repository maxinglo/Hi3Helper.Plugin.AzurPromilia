using System.Text.Json;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class OfficialGameInventory
{
    private const string JapanConfig = "game-launcher-config.json";
    private const string JapanManifest = "manifest.json";

    internal static Version? ReadManjuuVersion(string directory)
    {
        string index = Path.Combine(directory, "_index");
        string versionMarker = Path.Combine(index, "version");
        if (!File.Exists(versionMarker))
            return null;

        using FileStream marker = File.OpenRead(versionMarker);
        using JsonDocument installed = JsonDocument.Parse(marker);
        return Version.TryParse(Text(installed.RootElement, "version"), out Version? version) &&
            File.Exists(Path.Combine(index, version.ToString())) ? version : null;
    }

    internal static IReadOnlyList<GameFile> ReadManjuu(string directory, string executable, Version version,
        ReadOnlyMemory<byte> formatKey, ReadOnlyMemory<byte> iv)
    {
        if (ReadManjuuVersion(directory) != version)
            throw new InvalidDataException("The publisher's game version changed while its inventory was being read.");

        using FileStream inventory = File.OpenRead(Path.Combine(directory, "_index", version.ToString()));
        IReadOnlyList<GameFile> files = ManjuuGameIndex.ReadEncrypted(inventory, formatKey.Span, iv.Span).Files;
        if (!files.Any(file => file.Size > 0 && file.InstallPath.Equals(executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The publisher's game inventory does not contain its executable.");

        return files;
    }

    internal static Version? ReadJapanVersion(string directory, string executable)
    {
        if (!File.Exists(Path.Combine(directory, JapanConfig)) || !File.Exists(Path.Combine(directory, JapanManifest)))
            return null;

        using FileStream marker = File.OpenRead(Path.Combine(directory, JapanConfig));
        using JsonDocument installed = JsonDocument.Parse(marker);
        JsonElement configuration = installed.RootElement;
        if (Text(configuration, "tag") != JapanGameReleaseSource.GameTag ||
            !string.Equals(Text(configuration, "name"), Path.GetFileNameWithoutExtension(executable),
                StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(Text(configuration, "version"), out Version? version))
            return null;

        return version;
    }

    internal static IReadOnlyList<GameFile> ReadJapan(string directory, string executable, Version version)
    {
        if (ReadJapanVersion(directory, executable) != version)
            throw new InvalidDataException("The publisher's Japanese game version changed while its inventory was being read.");

        using FileStream inventory = File.OpenRead(Path.Combine(directory, JapanManifest));
        using JsonDocument manifest = JsonDocument.Parse(inventory);
        JsonElement data = manifest.RootElement;
        if (Text(data, "name") != JapanGameReleaseSource.GameTag || !HasVersion(data, version) ||
            !data.TryGetProperty("files", out JsonElement files))
            throw new InvalidDataException("The publisher's Japanese game inventory does not match the installation.");

        return JapanGameReleaseSource.ReadFiles(files, executable);
    }

    private static bool HasVersion(JsonElement data, Version version) =>
        Version.TryParse(Text(data, "version"), out Version? installed) && installed == version;

    private static string? Text(JsonElement data, string field) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
