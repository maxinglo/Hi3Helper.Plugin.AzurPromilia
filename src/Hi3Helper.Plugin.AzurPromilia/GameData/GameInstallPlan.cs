namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal sealed record GameInstallPlan(
    string Profile,
    Version Version,
    string IndexSha256,
    Uri BaseUri,
    IReadOnlyList<GameFile> Files,
    IReadOnlyList<string> Directories);
