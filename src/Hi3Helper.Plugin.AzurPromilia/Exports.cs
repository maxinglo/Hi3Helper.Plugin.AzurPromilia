using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;

namespace Hi3Helper.Plugin.AzurPromilia;

public sealed class Exports : SharedStaticV1Ext<Exports>
{
    static Exports() => Load<AzurPromiliaPlugin>(PluginVersion.Current);

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* name, void** result) => TryGetApiExportPointer(name, result);

    // Collapse's custom game arguments and priority settings do not apply to the publisher's launcher process.
    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? arguments, bool boosted,
        ProcessPriorityClass priority, CancellationToken token) => (true, Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            ((OfficialLauncherManager)context.GameManager).OpenOfficialLauncher();
            return true;
        }, token));

    // Collapse checks this callback to decide whether to use the plugin's Play action.
    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool running, out DateTime started)
    {
        running = false;
        started = default;
        return true;
    }

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token) => (true, Task.FromResult(true));
}
