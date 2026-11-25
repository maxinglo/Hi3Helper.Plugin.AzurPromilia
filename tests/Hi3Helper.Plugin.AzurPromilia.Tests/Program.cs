using Hi3Helper.Plugin.AzurPromilia;

if (args.Length is not (0 or 2))
    throw new ArgumentException("Pass the directories of the China and Japan launchers, or omit both.");

AzurPromiliaRegion[] regions = [AzurPromiliaRegion.China, AzurPromiliaRegion.Japan];
string[] profiles = ["AzurPromiliaCN", "AzurPromiliaJP"];
string[] sites = ["https://azurpromilia.manjuu.com/home/", "https://azurpromilia.jp/"];

var plugin = new AzurPromiliaPlugin();
plugin.GetPresetConfigCount(out int count);
Require(count == regions.Length, "The plugin must expose exactly two regions.");

DirectoryInfo temp = Directory.CreateTempSubdirectory("AzurPromiliaTests-");
try
{
    for (int index = 0; index < regions.Length; index++)
    {
        AzurPromiliaRegion region = regions[index];
        plugin.GetPresetConfig(index, out var preset);
        preset.comGet_ProfileName(out string profile);
        preset.comGet_ZoneHomePageUrl(out string website);
        preset.comGet_ZonePosterUrl(out string poster);
        preset.comGet_ZoneLogoUrl(out string logo);
        Require(profile == profiles[index] && website == sites[index], "Unexpected preset identity.");
        Require(IsHttps(poster) && IsHttps(logo), $"Collapse artwork is missing for {profile}.");

        preset.comGet_GameManager(out var manager);
        preset.comGet_GameInstaller(out var installer);
        preset.comGet_LauncherApiMedia(out var media);
        preset.comGet_LauncherApiNews(out var news);
        Require(manager is not null && installer is not null && media is not null && news is not null,
            $"A required Collapse interface is missing for {profile}.");

        var locator = new OfficialLauncherLocator(region);
        string lookalike = Path.Combine(temp.FullName, region.LauncherExecutable);
        File.WriteAllText(lookalike, "This test fixture is an unsigned lookalike.");
        Require(locator.FindCandidate(temp.FullName) is null, "An unsigned lookalike was recognized as a launcher.");
        Require(locator.FindCandidate("relative") is null, "A relative launcher directory was accepted.");
        Require(!Authenticode.IsSignedBy(lookalike, region.LauncherSigner), "A malformed signature was accepted.");

        var fallback = new OfficialLauncherLocator(region with { UninstallKey = null })
            .CreateStartInfo(temp.FullName);
        Require(fallback.FileName == sites[index] && fallback.Arguments.Length == 0 && fallback.UseShellExecute,
            $"Unexpected website fallback for {profile}.");

        if (args.Length == 2)
        {
            string officialDirectory = Path.GetFullPath(args[index]);
            string otherDirectory = Path.GetFullPath(args[1 - index]);
            string officialLauncher = locator.FindCandidate(officialDirectory)
                ?? throw new InvalidOperationException($"Cannot find a launcher in {officialDirectory}.");
            Require(locator.FindCandidate(otherDirectory) is null, "A launcher from the other region was accepted.");
            Require(Authenticode.IsSignedBy(officialLauncher, region.LauncherSigner), $"Signature did not match for {profile}.");
            Require(!Authenticode.IsSignedBy(officialLauncher, regions[1 - index].LauncherSigner),
                "The signature accepted the other region's publisher.");
            Require(locator.FindExisting(officialDirectory) == officialDirectory, "The signed launcher was not located.");
            var launch = locator.CreateStartInfo(officialDirectory);
            Require(launch.FileName == officialLauncher && launch.WorkingDirectory == Path.GetDirectoryName(officialLauncher)
                && launch.Arguments.Length == 0 && launch.UseShellExecute, $"Unsafe launch request for {profile}.");
        }
    }
}
finally
{
    foreach (AzurPromiliaRegion region in regions)
        File.Delete(Path.Combine(temp.FullName, region.LauncherExecutable));
    temp.Delete();
}

Console.WriteLine(args.Length == 2
    ? "PASS: Two region presets, official fallback URLs, and both signed launcher binaries."
    : "PASS: Two region presets, official fallback URLs, and rejection of launcher lookalikes.");

static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
