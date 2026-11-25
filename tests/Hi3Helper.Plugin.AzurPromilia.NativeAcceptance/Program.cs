using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;

internal static class Program
{
    // Only --launch starts the game; cancellation checks leave the game running and report independently.
    private static async Task<int> Main(string[] args)
    {
        string? reportPath = null;
        var report = new Dictionary<string, object?>();
        try
        {
            var options = Parse(args);
            reportPath = options.Report;
            string gameExe = Path.Combine(options.Game, "AzurPromilia.exe");
            string sdk = Path.Combine(options.Game, "AzurPromilia_Data", "Plugins", "mj_sdk", "union_pcsdk.dll");
            Require(File.Exists(gameExe) && File.Exists(sdk), "Game executable or SDK DLL not found.");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.Package, "manifest.json")));
            JsonElement root = manifest.RootElement;
            string library = Path.Combine(options.Package, root.GetProperty("MainLibraryName").GetString()!);
            nint module = NativeLibrary.Load(library);
            Version version = ReadVersion(module, "GetPluginVersion");
            Version abi = ReadVersion(module, "GetPluginStandardVersion");
            Version expectedVersion = Version.Parse(root.GetProperty("PluginVersion").GetString()!);
            expectedVersion = new(expectedVersion.Major, expectedVersion.Minor, expectedVersion.Build, Math.Max(0, expectedVersion.Revision));
            Require(version == expectedVersion, "Plugin version mismatch.");
            Require(abi == Version.Parse(root.GetProperty("PluginStandardVersion").GetString()!), "Plugin ABI mismatch.");
            report["pluginVersion"] = version.Revision == 0 ? version.ToString(3) : version.ToString();
            report["pluginAbi"] = abi.ToString();
            report["pluginPath"] = library;
            report["pluginSha256"] = Hash(library);
            report["sdkSha256"] = Hash(sdk);
            report["gameExecutable"] = gameExe;
            report["launchRequested"] = options.Launch;

            IPlugin plugin = GetPlugin(module);
            plugin.SetPluginLocaleId("zh-CN");
            plugin.GetPresetConfigCount(out int count);
            IPluginPresetConfig? preset = null;
            for (int i = 0; i < count; i++)
            {
                plugin.GetPresetConfig(i, out var candidate);
                candidate.comGet_ProfileName(out string profile);
                if (profile == "AzurPromiliaCN") { preset = candidate; break; }
            }
            Require(preset is not null, "CN preset not found.");
            Guid init = Guid.CreateVersion7();
            preset!.InitAsync(in init, out nint initResult);
            Require(await initResult.AsTask<int>().WaitAsync(TimeSpan.FromSeconds(30)) == 0, "Preset initialization failed.");
            preset.comGet_GameManager(out IGameManager? manager);
            ArgumentNullException.ThrowIfNull(manager);
            manager.SetGamePath(options.Game);
            manager.LoadConfig();
            manager.GetCurrentGameVersion(out GameVersion installedVersion);
            report["installedVersion"] = installedVersion.ToVersion().ToString();
            var context = new GameManagerExtension.RunGameFromGameManagerContext
            {
                Plugin = plugin, PresetConfig = preset, GameManager = manager, PluginHandle = module, PrintGameLogCallback = null
            };
            Require(context.IsFeatureAvailable, "Native Play API unavailable.");
            List<ProcessEvidence> initial = Snapshot(gameExe);
            report["initialGames"] = initial;
            Require(context.IsGameRunning(out bool running, out DateTime started, out Exception? queryError), $"IsRunning failed: {queryError}");
            Require(running == (initial.Count > 0), "IsRunning disagrees with exact-path process snapshot.");
            if (running)
                Require(initial.Any(p => Math.Abs((p.StartedUtc - started.ToUniversalTime()).TotalSeconds) < 2), "IsRunning start time does not match game process.");
            report["initialIsRunning"] = running;
            report["initialStarted"] = running ? started : null;

            string launcherRoot = Path.GetDirectoryName(options.Game)!;
            var launchersBefore = Launchers(launcherRoot);
            report["launchersBefore"] = launchersBefore;
            ProcessEvidence? observed = initial.FirstOrDefault();
            if (options.Launch)
            {
                Require(initial.Count == 0, "Refusing --launch while the game is already running.");
                using var cancel = new CancellationTokenSource();
                DateTime invoked = DateTime.UtcNow;
                Task<(bool IsSuccess, Exception? Error)> launchTask = context.RunGameFromGameManagerAsync(options.Arguments, token: cancel.Token);
                observed = await WaitForGame(gameExe, invoked);
                report["game"] = observed;
                Require(!launchTask.IsCompleted, "Launch callback completed before cancellation while game is running.");
                report["launchCallbackWaitsForGame"] = true;
                if (options.TestCancellation)
                {
                    cancel.Cancel();
                    report["launchCancellationObserved"] = await CheckCancellationAsync(launchTask, report);
                }
            }
            else if (options.TestCancellation)
            {
                Require(initial.Count > 0, "No existing exact-path game to test wait cancellation.");
                observed = initial[0];
                report["game"] = observed;
                using var cancel = new CancellationTokenSource();
                Task<(bool IsSuccess, Exception? Error)> waiting = context.WaitRunningGameAsync(cancel.Token);
                await Task.Delay(500);
                Require(!waiting.IsCompleted, "Wait callback completed before cancellation while game is running.");
                cancel.Cancel();
                report["waitCancellationObserved"] = await CheckCancellationAsync(waiting, report);
            }
            if (observed is not null)
            {
                Require(Snapshot(gameExe).Any(p => p.ProcessId == observed.ProcessId && p.StartedUtc == observed.StartedUtc), "Game did not remain alive during acceptance.");
                Require(context.IsGameRunning(out running, out started, out queryError) && running, $"IsRunning did not find the game: {queryError}");
                observed = Snapshot(gameExe).Single(p => p.ProcessId == observed.ProcessId && p.StartedUtc == observed.StartedUtc);
                report["game"] = observed;
                report["gameRemainsAlive"] = true;
            }
            var launchersAfter = Launchers(launcherRoot);
            Require(!launchersAfter.Any(p => !launchersBefore.Any(old => old.ProcessId == p.ProcessId && old.StartedUtc == p.StartedUtc)), "A new official launcher process appeared.");
            report["launchersAfter"] = launchersAfter;
            bool passed = !report.ContainsKey("cancellationFailure");
            report["success"] = passed;
            Console.WriteLine(observed is null ? "Verified idle game state." : $"Verified PID {observed.ProcessId}; game remains running.");
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            if (report.TryGetValue("gameExecutable", out object? path) && path is string executable &&
                report.TryGetValue("game", out object? value) && value is ProcessEvidence game)
            {
                try { report["gameAliveAfterFailure"] = Snapshot(executable).Any(p => p.ProcessId == game.ProcessId && p.StartedUtc == game.StartedUtc); }
                catch (Exception snapshotError) { report["postFailureSnapshotError"] = snapshotError.Message; }
            }
            report["success"] = false;
            report["error"] = error.ToString();
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (reportPath is not null)
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Options Parse(string[] args)
    {
        string? package = null, game = null, report = null, arguments = null;
        bool launch = false, testCancellation = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--launch": launch = true; break;
                case "--test-cancellation": testCancellation = true; break;
                case "--arguments" when ++i < args.Length: arguments = args[i]; break;
                case "--package" when ++i < args.Length: package = args[i]; break;
                case "--game" when ++i < args.Length: game = args[i]; break;
                case "--report" when ++i < args.Length: report = args[i]; break;
                default: throw new ArgumentException("Usage: --package <expanded dir> --game <game dir> --report <json> [--launch] [--arguments <game arguments>] [--test-cancellation]");
            }
        }
        Require(package is not null && game is not null && report is not null, "Missing required arguments.");
        return new(Path.GetFullPath(package!), Path.GetFullPath(game!), Path.GetFullPath(report!), launch, testCancellation, arguments);
    }

    private static async Task<bool> CheckCancellationAsync(Task<(bool IsSuccess, Exception? Error)> task, Dictionary<string, object?> report)
    {
        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            if (result.IsSuccess && result.Error is OperationCanceledException)
                return true;
            report["cancellationFailure"] = result.Error?.ToString() ?? "The callback did not report cancellation.";
        }
        catch (TimeoutException)
        {
            report["cancellationFailure"] = "Core did not propagate the non-null ABI cancellation handle within ten seconds.";
        }
        return false;
    }

    private static async Task<ProcessEvidence> WaitForGame(string exe, DateTime invoked)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            ProcessEvidence? process = Snapshot(exe).FirstOrDefault(p => p.StartedUtc >= invoked.AddSeconds(-1));
            if (process is not null) return process;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("No new game process appeared within 10 seconds.");
    }

    private static List<ProcessEvidence> Snapshot(string exe) => Processes(Path.GetFileNameWithoutExtension(exe))
        .Where(p => string.Equals(p.Path, exe, StringComparison.OrdinalIgnoreCase)).ToList();

    private static List<ProcessEvidence> Launchers(string root) => Processes("launcher")
        .Where(p => p.Path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();

    private static List<ProcessEvidence> Processes(string name)
    {
        var result = new List<ProcessEvidence>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    string path = process.MainModule?.FileName ?? throw new InvalidOperationException("Process path unavailable.");
                    result.Add(new(process.Id, path, process.StartTime.ToUniversalTime(), process.MainWindowHandle != 0, process.MainWindowTitle));
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
        }
        return result;
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static unsafe IPlugin GetPlugin(nint module)
    {
        Require(module.TryGetExport("GetPlugin", out PointerExport export), "GetPlugin missing.");
        return ComInterfaceMarshaller<IPlugin>.ConvertToManaged(export()) ?? throw new InvalidOperationException("GetPlugin returned null.");
    }
    private static unsafe Version ReadVersion(nint module, string name)
    {
        Require(module.TryGetExport(name, out PointerExport export), $"{name} missing.");
        return ((GameVersion*)export())->ToVersion();
    }
    private unsafe delegate void* PointerExport();
    private sealed record Options(string Package, string Game, string Report, bool Launch, bool TestCancellation, string? Arguments);
    private sealed record ProcessEvidence(int ProcessId, string Path, DateTime StartedUtc, bool HasWindow, string WindowTitle);
}
