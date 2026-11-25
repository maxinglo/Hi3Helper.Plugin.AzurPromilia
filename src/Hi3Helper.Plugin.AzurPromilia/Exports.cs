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

    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, string? arguments, bool boosted,
        ProcessPriorityClass priority, CancellationToken token) => (true,
        ((OfficialLauncherManager)context.GameManager).LaunchGameAsync(arguments, boosted, priority, token));

    // Collapse checks this callback to decide whether to use the plugin's Play action.
    protected override bool IsGameRunningCore(GameManagerExtension.RunGameFromGameManagerContext context,
        out bool running, out DateTime started)
    {
        using InstalledGameProcess? game = InstalledGameProcess.Find(((OfficialLauncherManager)context.GameManager).GetInstalledExecutablePath());
        running = game is not null;
        started = game?.Started ?? default;
        return true;
    }

    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context, CancellationToken token) =>
        (true, WaitForGameAsync(((OfficialLauncherManager)context.GameManager).GetInstalledExecutablePath(), token));

    private static async Task<bool> WaitForGameAsync(string? executable, CancellationToken token)
    {
        using InstalledGameProcess? game = InstalledGameProcess.Find(executable);
        if (game is not null)
            await game.WaitForExitAsync(token).ConfigureAwait(false);
        return true;
    }
}
