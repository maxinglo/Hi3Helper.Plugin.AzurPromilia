namespace Hi3Helper.Plugin.AzurPromilia.Content;

// Keep Japan and Taiwan social icons white, transparent and visually consistent in size.
internal static class OfficialSocialIcons
{
    internal static byte[] X { get; } = Read("x.png");
    internal static byte[] YouTube { get; } = Read("youtube.png");
    internal static byte[] Facebook { get; } = Read("facebook.png");
    internal static byte[] Instagram { get; } = Read("instagram.png");
    internal static byte[] Discord { get; } = Read("discord.png");
    internal static byte[] JapanSupport { get; } = Read("japan-support.png");

    private static byte[] Read(string name)
    {
        using Stream input = typeof(OfficialSocialIcons).Assembly
            .GetManifestResourceStream("AzurPromilia.Social." + name)
            ?? throw new InvalidDataException("An official social icon is missing from the plugin.");
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
