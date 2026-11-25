using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.AzurPromilia;
using Hi3Helper.Plugin.AzurPromilia.GameData;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;

internal static class GameInstallerHostTests
{
    private const string Executable = "AzurPromilia.exe";

    internal static async Task RunAsync()
    {
        byte[] initialFile = Encoding.UTF8.GetBytes("version one fixture");
        byte[] updatedFile = Encoding.UTF8.GetBytes("version two fixture");
        GameInstallPlan current = CreatePlan("1.0.0.30", initialFile);
        var region = AzurPromiliaRegion.China with
        {
            LoadGameInstallPlan = (_, _) => Task.FromResult(current)
        };

        var manager = new OfficialLauncherManager(region);
        var installer = new OfficialLauncherInstaller(manager, new FixtureHandler(initialFile, updatedFile));
        DirectoryInfo folder = Directory.CreateTempSubdirectory("AzurPromilia-HostInstall-");
        var states = new List<InstallProgressState>();
        try
        {
            Guid request = Guid.CreateVersion7();
            manager.InitAsync(in request, out nint initialized);
            Require(await initialized.AsTask<int>().WaitAsync(TimeSpan.FromSeconds(15)) == 0, "Game manager initialization failed.");
            manager.SetGamePath(folder.FullName);
            manager.GetApiGameVersion(out GameVersion offered);
            Require(offered == new GameVersion("1.0.0.30"), "Collapse must receive the version of the verified release.");
            manager.IsGameInstalled(out bool alreadyInstalled);
            Require(!alreadyInstalled, "An empty directory must not appear installed.");

            request = Guid.CreateVersion7();
            installer.GetGameSizeAsync(GameInstallerKind.Install, in request, out nint total);
            Require(await total.AsTask<long>().WaitAsync(TimeSpan.FromSeconds(15)) == initialFile.Length,
                "Collapse must receive the actual installation size.");

            request = Guid.CreateVersion7();
            installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in request, out nint downloaded);
            Require(await downloaded.AsTask<long>().WaitAsync(TimeSpan.FromSeconds(15)) == 0,
                "A new installation must have no previously downloaded files.");

            long transferred = 0;
            request = Guid.CreateVersion7();
            installer.StartInstallAsync((in InstallProgress progress) => transferred = progress.DownloadedBytes,
                states.Add, in request, out nint installation);
            await installation.AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            manager.IsGameInstalled(out alreadyInstalled);
            Require(alreadyInstalled && transferred == initialFile.Length && states[^1] == InstallProgressState.Completed,
                "The normal Core install path must complete only after verified files and version state are committed.");

            manager.SetCurrentGameVersion(new GameVersion("9.0.0.0"));
            manager.GetCurrentGameVersion(out GameVersion installedVersion);
            Require(installedVersion == new GameVersion("1.0.0.30"), "Host calls cannot set a version that is absent on disk.");

            current = CreatePlan("1.0.0.31", updatedFile);
            request = Guid.CreateVersion7();
            manager.InitAsync(in request, out initialized);
            await initialized.AsTask<int>().WaitAsync(TimeSpan.FromSeconds(15));
            manager.IsGameHasUpdate(out bool hasUpdate);
            Require(hasUpdate, "A different verified version should surface the Update action.");

            request = Guid.CreateVersion7();
            installer.StartUpdateAsync(null, states.Add, in request, out nint update);
            await update.AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            manager.GetCurrentGameVersion(out installedVersion);
            manager.IsGameHasUpdate(out hasUpdate);
            Require(installedVersion == new GameVersion("1.0.0.31") && !hasUpdate &&
                File.ReadAllBytes(Path.Combine(folder.FullName, Executable)).SequenceEqual(updatedFile),
                "The normal Core update action must commit the new content and version.");
        }
        finally
        {
            installer.Dispose();
            manager.Dispose();
            folder.Delete(recursive: true);
        }
    }

    private static GameInstallPlan CreatePlan(string version, byte[] payload)
    {
        return new("AzurPromiliaCN", Version.Parse(version), Convert.ToHexStringLower(SHA256.HashData(payload)),
            new Uri("https://example.invalid/content/"),
            [new GameFile(payload.Length, Convert.ToHexStringLower(MD5.HashData(payload)), Executable, version + "/game.exe")], []);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(byte[] initialFile, byte[] updatedFile) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] payload = request.RequestUri?.AbsolutePath switch
            {
                "/content/1.0.0.30/game.exe" => initialFile,
                "/content/1.0.0.31/game.exe" => updatedFile,
                _ => throw new InvalidOperationException("The installer requested a file outside the verified release.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

}
