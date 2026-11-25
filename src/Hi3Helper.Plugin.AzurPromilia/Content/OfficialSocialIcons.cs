namespace Hi3Helper.Plugin.AzurPromilia.Content;

internal static class OfficialSocialIcons
{
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
