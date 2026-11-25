using Hi3Helper.Plugin.AzurPromilia.Content;

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
    string? KoreanName = null)
{
    internal static AzurPromiliaRegion China { get; } = new(
        "AzurPromiliaCN", "China", "国服", "中国版", "zh-CN", "Manjuu",
        "https://azurpromilia.manjuu.com/home/",
        "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/assets/kv_poster-DmJ_mC8Q.jpg",
        "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/assets/logo-white-D9HvtDCJ.png",
        ChinaLauncherContentSource.LoadAsync,
        new OfficialLauncherIdentity("launcher.exe", "蓝色星原：旅谣", "上海蛮奈网络科技有限公司",
            UninstallKey: "AzurPromilia_35_C000", VersionedDirectory: true),
        TraditionalChineseName: "陸服", KoreanName: "중국");

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
        "https://webstatic.manjuu.com/web-kits/footer/game-logo-zh-tw.4af614f70106.svg",
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
