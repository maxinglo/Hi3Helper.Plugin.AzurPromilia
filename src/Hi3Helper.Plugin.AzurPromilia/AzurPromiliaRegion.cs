namespace Hi3Helper.Plugin.AzurPromilia;

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
    string LauncherExecutable,
    string LauncherProductName,
    string LauncherSigner,
    string? UninstallKey = null,
    bool VersionedLauncherDirectory = false)
{
    internal static AzurPromiliaRegion China { get; } = new(
        "AzurPromiliaCN", "China", "国服", "中国版", "zh-CN", "Manjuu",
        "https://azurpromilia.manjuu.com/home/",
        "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/assets/kv_poster-DmJ_mC8Q.jpg",
        "https://webstatic.manjuu.com/web/static/azurpromilia.manjuu.com/assets/logo-white-D9HvtDCJ.png",
        "launcher.exe", "蓝色星原：旅谣", "上海蛮奈网络科技有限公司",
        UninstallKey: "AzurPromilia_35_C000", VersionedLauncherDirectory: true);

    internal static AzurPromiliaRegion Japan { get; } = new(
        "AzurPromiliaJP", "Japan", "日服", "日本版", "ja-JP", "Yostar",
        "https://azurpromilia.jp/",
        "https://webusstatic.yo-star.com/azurpromilia-jp/azurpromilia-jp-official-2026/prod/azurpromiliajpofficial2026/assets/poster-DGAu94FG.jpg",
        "https://webusstatic.yo-star.com/azurpromilia-jp/azurpromilia-jp-official-2026/prod/azurpromiliajpofficial2026/azurpromilia-bannerRGB.png",
        "AZUPRO_JP_Gamelauncher.exe", "AZUPRO_JP_Gamelauncher", "Yostar, Inc.");
}
