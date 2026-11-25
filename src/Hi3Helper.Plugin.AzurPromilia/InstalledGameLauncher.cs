using System.ComponentModel;
using System.Diagnostics;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia;

// Launch the installed China game with the launcher marker, preserving Collapse arguments such as -force-d3d12.
internal static class InstalledGameLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string executable, string? arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The installed game executable is unavailable.", executable);

        return new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = string.IsNullOrWhiteSpace(arguments)
                ? "-start=azurpromilia_launcher"
                : "-start=azurpromilia_launcher " + arguments.Trim(),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    internal static async Task<bool> RunAsync(string executable, string? arguments, bool boosted,
        ProcessPriorityClass priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (InstalledGameProcess? running = InstalledGameProcess.Find(executable))
        {
            if (running is not null)
            {
                await running.WaitForExitAsync(token).ConfigureAwait(false);
                return true;
            }
        }

        using Process process = Process.Start(CreateStartInfo(executable, arguments))
            ?? throw new InvalidOperationException("Windows did not create the game process.");
        try
        {
            process.PriorityClass = priority;
            process.PriorityBoostEnabled = boosted;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            SharedStatic.InstanceLogger.LogWarning(error, "Could not apply Collapse's game process priority.");
        }

        // Cancelling the launch task releases the wait without terminating the game.
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Azur Promilia exited with code {process.ExitCode}.");
        return true;
    }
}
