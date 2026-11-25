using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

[GeneratedComClass]
public sealed partial class AzurPromiliaPlugin : PluginBase
{
    private static DateTime _creationDate = new(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);

    private readonly IPluginPresetConfig[] _presets = AzurPromiliaRegion.All
        .Select(static region => (IPluginPresetConfig)new AzurPromiliaPreset(region))
        .ToArray();

    internal static string Localize(string english, string chinese, string japanese,
        string? traditionalChinese = null, string? korean = null)
    {
        string locale = SharedStatic.PluginLocaleCode?.ToLowerInvariant() ?? string.Empty;
        string language = locale.Split('-')[0];
        return language switch
        {
            "zh" when locale.StartsWith("zh-hant", StringComparison.Ordinal) || locale is "zh-tw" or "zh-hk" or "zh-mo"
                => traditionalChinese ?? chinese,
            "zh" => chinese,
            "ja" => japanese,
            "ko" => korean ?? english,
            _ => english
        };
    }

    internal static string GameDisplayName => Localize(
        "Azur Promilia", "蓝色星原：旅谣", "アズールプロミリア", "藍色星原：旅謠", "아주르 프로밀리아");

    public override void GetPluginName(out string result) => result = GameDisplayName;

    public override void GetPluginDescription(out string result) => result = Localize(
        "Download, update and launch Azur Promilia for China, Japan and Taiwan, with official news and events.",
        "在 Collapse Launcher 下载、更新和启动《蓝色星原：旅谣》国服、日服和台服，查看官方资讯与活动。",
        "Collapse Launcher で『アズールプロミリア』の中国・日本・台湾版をダウンロード、更新、起動し、公式ニュースやイベントを確認できます。",
        "在 Collapse Launcher 下載、更新和啟動《藍色星原：旅謠》國服、日服和台服，查看官方資訊與活動。",
        "Collapse Launcher에서 아주르 프로밀리아 중국·일본·대만 클라이언트를 다운로드, 업데이트 및 실행하고 공식 소식과 이벤트를 확인할 수 있습니다.");

    public override void GetPluginAuthor(out string result) => result = "Maxing";

    public override void GetPluginAppIconUrl(out string result) => result = AzurPromiliaRegion.China.LogoUrl;

    public override void GetNotificationPosterUrl(out string result) => result = AzurPromiliaRegion.China.BackgroundUrl;

    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _creationDate.AsPointer();

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate? result) => result = new AzurPromiliaPluginSelfUpdater();

    public override void GetPresetConfigCount(out int count) => count = _presets.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig result) =>
        result = _presets[index];
}
