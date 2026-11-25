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

    private readonly IPluginPresetConfig[] _presets =
    [
        new AzurPromiliaPreset(AzurPromiliaRegion.China),
        new AzurPromiliaPreset(AzurPromiliaRegion.Japan)
    ];

    internal static string Localize(string english, string chinese, string japanese) =>
        SharedStatic.PluginLocaleCode?.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" => chinese,
            "ja" => japanese,
            _ => english
        };

    public override void GetPluginName(out string result) =>
        result = Localize("Azur Promilia", "蓝色星原：旅谣", "アズールプロミリア");

    public override void GetPluginDescription(out string result) => result = Localize(
        "Open the official Azur Promilia launchers for China and Japan from Collapse Launcher.",
        "从 Collapse Launcher 打开《蓝色星原：旅谣》国服或日服官方启动器。",
        "Collapse Launcher から『アズールプロミリア』の中国版・日本版公式ランチャーを起動します。");

    public override void GetPluginAuthor(out string result) => result = "Maxing";

    public override void GetPluginAppIconUrl(out string result) => result = AzurPromiliaRegion.China.LogoUrl;

    public override void GetNotificationPosterUrl(out string result) => result = AzurPromiliaRegion.China.BackgroundUrl;

    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _creationDate.AsPointer();

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate? result) => result = new AzurPromiliaPluginSelfUpdater();

    public override void GetPresetConfigCount(out int count) => count = _presets.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig result) =>
        result = _presets[index];
}
