using Hi3Helper.Plugin.AzurPromilia;

if (args.Length is not (0 or 2))
    throw new ArgumentException("Pass --china-launcher followed by a directory, both launcher directories, or no arguments.");

bool verifyChinaOnly = args is ["--china-launcher", _];

IReadOnlyList<AzurPromiliaRegion> regions = AzurPromiliaRegion.All;
string[] profiles = ["AzurPromiliaCN", "AzurPromiliaJP", "AzurPromiliaTW", "AzurPromiliaKR"];
string[] sites = ["https://azurpromilia.manjuu.com/home/", "https://azurpromilia.jp/",
    "https://azurpromilia.jimi-global.com/home/", "https://azurpromilia.nexon.com/main"];

var plugin = new AzurPromiliaPlugin();
plugin.GetPresetConfigCount(out int count);
Require(count == profiles.Length && count == regions.Count, "The plugin must expose every configured region.");

DirectoryInfo temp = Directory.CreateTempSubdirectory("AzurPromiliaTests-");
try
{
    for (int index = 0; index < regions.Count; index++)
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
        if (region.Launcher is { } launcherIdentity)
        {
            string lookalike = Path.Combine(temp.FullName, launcherIdentity.Executable);
            File.WriteAllText(lookalike, "This test fixture is an unsigned lookalike.");
            Require(!Authenticode.IsSignedBy(lookalike, launcherIdentity.Signer), "A malformed signature was accepted.");
        }
        Require(locator.FindCandidate(temp.FullName) is null, "An unsigned lookalike was recognized as a launcher.");
        Require(locator.FindCandidate("relative") is null, "A relative launcher directory was accepted.");

        var fallback = new OfficialLauncherLocator(region with { Launcher = null })
            .CreateStartInfo(temp.FullName);
        Require(fallback.FileName == sites[index] && fallback.Arguments.Length == 0 && fallback.UseShellExecute,
            $"Unexpected website fallback for {profile}.");

        bool verifyLocal = index < 2 && args.Length == 2 && (!verifyChinaOnly || index == 0);
        if (verifyLocal && region.Launcher is { } identity)
        {
            string officialDirectory = Path.GetFullPath(verifyChinaOnly ? args[1] : args[index]);
            string officialLauncher = locator.FindCandidate(officialDirectory)
                ?? throw new InvalidOperationException($"Cannot find a launcher in {officialDirectory}.");
            if (!verifyChinaOnly)
            {
                string otherDirectory = Path.GetFullPath(args[1 - index]);
                Require(locator.FindCandidate(otherDirectory) is null, "A launcher from the other region was accepted.");
                Require(!Authenticode.IsSignedBy(officialLauncher, regions[1 - index].Launcher!.Signer),
                    "The signature accepted the other region's publisher.");
            }
            Require(Authenticode.IsSignedBy(officialLauncher, identity.Signer), $"Signature did not match for {profile}.");
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
    {
        if (region.Launcher is { } identity)
            File.Delete(Path.Combine(temp.FullName, identity.Executable));
    }
    temp.Delete();
}

await LauncherContentTests.RunAsync();
await ChinaContentTests.RunAsync();
Console.WriteLine("PASS: Region presets, publisher websites, and official launcher identity checks.");

static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
