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
        "Browse official Azur Promilia news, events, and community links for each region.",
        "在 Collapse Launcher 查看《蓝色星原：旅谣》各区服的官方资讯、活动与社区。",
        "Collapse Launcher で『アズールプロミリア』各地域の公式ニュース、イベント、コミュニティを確認できます。",
        "在 Collapse Launcher 查看《藍色星原：旅謠》各區服的官方資訊、活動與社群。",
        "Collapse Launcher에서 아주르 프로밀리아의 지역별 공식 소식, 이벤트, 커뮤니티를 확인할 수 있습니다.");

    public override void GetPluginAuthor(out string result) => result = "Maxing";

    public override void GetPluginAppIconUrl(out string result) => result = AzurPromiliaRegion.China.LogoUrl;

    public override void GetNotificationPosterUrl(out string result) => result = AzurPromiliaRegion.China.BackgroundUrl;

    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _creationDate.AsPointer();

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate? result) => result = new AzurPromiliaPluginSelfUpdater();

    public override void GetPresetConfigCount(out int count) => count = _presets.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig result) =>
        result = _presets[index];
}
