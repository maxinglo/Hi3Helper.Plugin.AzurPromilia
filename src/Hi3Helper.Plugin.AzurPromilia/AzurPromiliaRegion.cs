using Hi3Helper.Plugin.AzurPromilia.Content;
using Hi3Helper.Plugin.AzurPromilia.GameData;

namespace Hi3Helper.Plugin.AzurPromilia;

internal sealed record OfficialLauncherIdentity(
    string Executable,
    string ProductName,
    string Signer,
    string? UninstallKey = null,
    bool VersionedDirectory = false);

internal sealed record AzurPromiliaRegion(
    string ProfileName,
    string EnglishName,
    string ChineseName,
    string JapaneseName,
    string Language,
    string Publisher,
    string Website,
    string BackgroundUrl,
    string LogoUrl,
    Func<HttpClient, CancellationToken, Task<LauncherContentSnapshot>> LoadContent,
    OfficialLauncherIdentity? Launcher = null,
    string? TraditionalChineseName = null,
    string? KoreanName = null,
    string? NativeGameExecutable = null,
    string? ManjuuGameDirectoryName = null,
    Func<HttpClient, CancellationToken, Task<GameInstallPlan>>? LoadGameInstallPlan = null,
    string? BackgroundVideoUrl = null)
{
    internal static AzurPromiliaRegion China { get; } = new(
        "AzurPromiliaCN", "China", "国服", "中国版", "zh-CN", "Manjuu",
        "https://azurpromilia.manjuu.com/home/",
        "https://l1-all-plat-server.manjuu.com/azurpromilia/img/93cdd43e096046138053253dc56d3562/1795598820281_WBTIUZFKFK（无字）OB启动器静态图延展雪地大奇波kv.png",
        "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/assets/logo-white-D9HvtDCJ.png",
        ChinaLauncherContentSource.LoadAsync,
        new OfficialLauncherIdentity("launcher.exe", "蓝色星原：旅谣", "上海蛮奈网络科技有限公司",
            UninstallKey: "AzurPromilia_35_C000", VersionedDirectory: true),
        TraditionalChineseName: "陸服", KoreanName: "중국",
        NativeGameExecutable: "AzurPromilia.exe", ManjuuGameDirectoryName: "AzurPromilia_Game",
        BackgroundVideoUrl: "https://l1-all-plat-server.manjuu.com/azurpromilia/video/93cdd43e096046138053253dc56d3562/1795598820421_ECAYRBMANXOB启动器动态图片适配与延展.mp4");

    internal static AzurPromiliaRegion Japan { get; } = new(
        "AzurPromiliaJP", "Japan", "日服", "日本版", "ja-JP", "Yostar",
        "https://azurpromilia.jp/",
        "https://webusstatic.yo-star.com/azurpromilia-jp/azurpromilia-jp-official-2026/prod/azurpromiliajpofficial2026/assets/poster-DGAu94FG.jpg",
        "https://webusstatic.yo-star.com/azurpromilia-jp/azurpromilia-jp-official-2026/prod/azurpromiliajpofficial2026/azurpromilia-bannerRGB.png",
        JapanLauncherContentSource.LoadAsync,
        new OfficialLauncherIdentity("AZUPRO_JP_Gamelauncher.exe", "AZUPRO_JP_Gamelauncher", "Yostar, Inc."),
        KoreanName: "일본");

    internal static AzurPromiliaRegion Taiwan { get; } = new(
        "AzurPromiliaTW", "Taiwan", "台服", "台湾版", "zh-TW", "JIMI",
        "https://azurpromilia.jimi-global.com/home/",
        "https://webstatic.jimi-global.com/web/static/azurpromilia.jimi-global.com/home/assets/poster-C-PmzHX_.jpg",
        "https://plat-cdn.manjuu.com/p3-SocialMedia/20240314-104900.png",
        TaiwanLauncherContentSource.LoadAsync,
        KoreanName: "대만");

    internal static AzurPromiliaRegion Korea { get; } = new(
        "AzurPromiliaKR", "Korea", "韩服", "韓国版", "ko-KR", "Nexon",
        "https://azurpromilia.nexon.com/main",
        "https://azurpromilia-web.dn.nexoncdn.co.kr/home_73685DA/main/bg_poster.jpg",
        "https://azurpromilia-web.dn.nexoncdn.co.kr/home_73685DA/common/img_logo_white.png",
        KoreaLauncherContentSource.LoadAsync,
        TraditionalChineseName: "韓服", KoreanName: "한국");

    internal static IReadOnlyList<AzurPromiliaRegion> All { get; } = [China, Japan, Taiwan, Korea];
}
