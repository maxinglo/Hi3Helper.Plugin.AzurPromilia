using System.Diagnostics;
using Hi3Helper.Plugin.AzurPromilia;

internal static class GameLaunchTests
{
    internal static async Task RunAsync()
    {
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("AzurPromilia Launch Tests ");
        try
        {
            string executable = Path.Combine(temporary.FullName, "Azur Promilia.exe");
            File.WriteAllBytes(executable, []);

            VerifyRelativePathRejected(temporary.FullName, Path.GetFileName(executable));
            string missing = Path.Combine(temporary.FullName, "Missing.exe");
            RequireUnavailable(missing);

            ProcessStartInfo start = InstalledGameLauncher.CreateStartInfo(executable, "-force-d3d12 -logFile \"player log.txt\"");
            Require(start.FileName == executable && start.WorkingDirectory == temporary.FullName,
                "The launch must use the full executable path and the game working directory, including spaces.");
            Require(start.Arguments == "-start=azurpromilia_launcher -force-d3d12 -logFile \"player log.txt\"",
                "The launch must include the official marker and preserve Collapse's arguments and quoting.");
            Require(InstalledGameLauncher.CreateStartInfo(executable, null).Arguments == "-start=azurpromilia_launcher",
                "The launch must supply the official marker when Collapse sends no arguments.");

            var canceled = new CancellationToken(canceled: true);
            try
            {
                await InstalledGameLauncher.RunAsync(missing, null, false, ProcessPriorityClass.Normal, canceled);
                throw new InvalidOperationException("A pre-canceled launch unexpectedly completed.");
            }
            catch (OperationCanceledException error) when (error.CancellationToken == canceled) { }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void VerifyRelativePathRejected(string directory, string relative)
    {
        string original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(directory);
            Require(File.Exists(relative), "Expected an existing file for the relative launch path test.");
            RequireUnavailable(relative);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    private static void RequireUnavailable(string path)
    {
        try
        {
            _ = InstalledGameLauncher.CreateStartInfo(path, null);
            throw new InvalidOperationException("An unavailable or relative game executable was accepted.");
        }
        catch (FileNotFoundException error) when (error.FileName == path) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
