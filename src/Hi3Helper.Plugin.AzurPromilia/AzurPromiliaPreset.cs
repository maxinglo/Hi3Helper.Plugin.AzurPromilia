using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
internal sealed partial class AzurPromiliaPreset : PluginPresetConfigBase
{
    private readonly AzurPromiliaRegion _region;

    internal AzurPromiliaPreset(AzurPromiliaRegion region)
    {
        _region = region;
        var manager = new OfficialLauncherManager(region);
        GameManager = manager;
        GameInstaller = new OfficialLauncherInstaller(manager);
        LauncherApiMedia = new OfficialLauncherMedia(region.BackgroundUrl);
        LauncherApiNews = new OfficialLauncherNews(region.ProfileName, region.LoadContent);
    }

    public override string GameName => AzurPromiliaPlugin.GameDisplayName;
    public override string GameExecutableName => _region.NativeGameExecutable ?? _region.Launcher?.Executable ?? string.Empty;
    public override string GameAppDataPath => string.Empty;
    public override string GameLogFileName => string.Empty;
    public override string GameVendorName => _region.Publisher;
    public override string GameRegistryKeyName => ProfileName;
    public override string ProfileName => _region.ProfileName;
    public override string ZoneName => AzurPromiliaPlugin.Localize(
        _region.EnglishName, _region.ChineseName, _region.JapaneseName, _region.TraditionalChineseName, _region.KoreanName);
    public override string ZoneFullName => $"{GameName} - {ZoneName}";
    public override string ZoneDescription => AzurPromiliaPlugin.Localize(
        $"{ZoneName} official site and community", $"{ZoneName}官方网站及社区", $"{ZoneName}公式サイト・コミュニティ",
        $"{ZoneName}官方網站及社群", $"{ZoneName} 공식 웹사이트 및 커뮤니티");
    public override string ZoneLogoUrl => _region.LogoUrl;
    public override string ZonePosterUrl => _region.BackgroundUrl;
    public override string ZoneHomePageUrl => _region.Website;
    public override GameReleaseChannel ReleaseChannel => GameReleaseChannel.Public;
    public override string GameMainLanguage => _region.Language;
    public override string LauncherGameDirectoryName => _region.ProfileName;
    public override List<string> SupportedLanguages => [_region.Language];

    public override IGameManager? GameManager { get; set; }
    public override IGameInstaller? GameInstaller { get; set; }
    public override ILauncherApiMedia? LauncherApiMedia { get; set; }
    public override ILauncherApiNews? LauncherApiNews { get; set; }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public override void Dispose()
    {
        GameInstaller?.Free();
        GameInstaller = null;
        base.Dispose();
    }
}
