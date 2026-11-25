using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class GameInstallTests
{
    private static readonly Uri BaseUri = new("https://example.invalid/publisher/assets");

    internal static async Task RunAsync()
    {
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("AzurPromiliaInstallTests-");
        try
        {
            await TestResumeAndInstallAsync(temporary.FullName);
            await TestUnmanagedConflictAsync(Path.Combine(temporary.FullName, "conflict"));
            await TestInsufficientSpaceAsync(Path.Combine(temporary.FullName, "full-disk"));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static async Task TestResumeAndInstallAsync(string root)
    {
        byte[] existing = [11, 12];
        byte[] contents = [1, 2, 3, 4, 5];
        GameFile existingFile = FileEntry("game/existing.bin", "unchanged.bin", existing);
        GameFile gameFile = FileEntry("game/file with space.bin", "payload/file with space.bin", contents);
        GameInstallPlan plan = Plan(new Version(1, 2, 3), 'a', [existingFile, gameFile]);
        Directory.CreateDirectory(Path.Combine(root, "game"));
        await File.WriteAllBytesAsync(Path.Combine(root, "game", "existing.bin"), existing);
        string unrelated = Path.Combine(root, "game", "local-save.txt");
        await File.WriteAllTextAsync(unrelated, "Keep this file.");

        using var cancellation = new CancellationTokenSource();
        using (var http = Client(request =>
               {
                   Require(request.RequestUri?.AbsoluteUri == "https://example.invalid/publisher/assets/payload/file%20with%20space.bin",
                       "The remote path must be escaped relative to the publisher CDN base.");
                   Require(request.Headers.Range is null, "The initial transfer must start without a Range.");
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ShortReadStream(contents)) };
               }))
        {
            var installer = new VerifiedGameInstaller(http);
            Require(await installer.GetDownloadSizesAsync(root, plan, CancellationToken.None) == (5L, 0L),
                "Previously matching destination files must not count as required download bytes.");
            try
            {
                await installer.InstallAsync(root, plan, (downloaded, _) =>
                {
                    if (downloaded == 2) cancellation.Cancel();
                }, cancellation.Token);
                throw new InvalidOperationException("Canceled staging unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

            Require(!File.Exists(Path.Combine(root, "game", "file with space.bin")) &&
                    VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) is null,
                "Staging must never publish a destination or installed version.");
            Require(await installer.GetDownloadSizesAsync(root, plan, CancellationToken.None) == (5L, 2L),
                "Canceling must preserve the partial for the same plan.");
            GameInstallPlan otherIndex = plan with { IndexSha256 = new string('b', 64) };
            Require(await installer.GetDownloadSizesAsync(root, otherIndex, CancellationToken.None) == (5L, 0L),
                "A different index must never resume a previous plan's partial.");
        }

        using (var http = Client(request =>
               {
                   Require(request.Headers.Range?.ToString() == "bytes=2-4", "Retry did not resume the partial.");
                   var result = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(contents[2..]) };
                   result.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, contents.Length);
                   return result;
               }))
        {
            var installer = new VerifiedGameInstaller(http);
            var progress = new List<(long Downloaded, long Total)>();
            await installer.InstallAsync(root, plan, (downloaded, total) => progress.Add((downloaded, total)), CancellationToken.None);
            Require(progress[0] == (2L, 5L) && progress[^1] == (5L, 5L),
                "Install progress must count the resumed bytes once.");
            Require(await installer.GetDownloadSizesAsync(root, plan, CancellationToken.None) == (0L, 0L),
                "Every installed file should verify after commit.");
            Require(VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version &&
                    VerifiedGameInstaller.ReadInstalledVersion(root, "AzurPromiliaTW") is null &&
                    Directory.Exists(Path.Combine(root, "game", "empty")) &&
                    await File.ReadAllTextAsync(unrelated) == "Keep this file.",
                "The installation must persist its region and version, create explicit directories, and preserve local files.");
        }

        byte[] replacement = [9, 8, 7];
        GameInstallPlan update = Plan(new Version(1, 2, 4), 'c', [existingFile,
            FileEntry(gameFile.InstallPath, gameFile.RemotePath, replacement)]);
        string destination = Path.Combine(root, "game", "file with space.bin");
        using (var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(replacement) }))
        {
            var installer = new VerifiedGameInstaller(http);
            using (var inUse = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    await installer.InstallAsync(root, update, null, CancellationToken.None);
                    throw new InvalidOperationException("Replacing a locked Windows file unexpectedly succeeded.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Require(VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) is null,
                    "A failed commit must leave a pending marker and hide the installed version.");
                Require(await installer.GetDownloadSizesAsync(root, update, CancellationToken.None) == (3L, 3L),
                    "A failed commit must keep verified staging available to the same plan.");
            }
        }

        using (var http = Client(_ => throw new InvalidOperationException("A verified staged file was downloaded again.")))
        {
            var installer = new VerifiedGameInstaller(http);
            await installer.InstallAsync(root, update, null, CancellationToken.None);
            Require(VerifiedGameInstaller.ReadInstalledVersion(root, update.Profile) == update.Version &&
                    (await File.ReadAllBytesAsync(destination)).SequenceEqual(replacement),
                "Retrying an interrupted commit must apply the staged bytes and publish the new version.");

            try
            {
                await installer.InstallAsync(root, plan, null, CancellationToken.None);
                throw new InvalidOperationException("An older installation plan unexpectedly succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("older than the installed version", StringComparison.Ordinal)) { }
            Require(VerifiedGameInstaller.ReadInstalledVersion(root, update.Profile) == update.Version &&
                    (await File.ReadAllBytesAsync(destination)).SequenceEqual(replacement),
                "Rejecting an older plan must preserve the verified installed version and files.");
        }
    }

    private static async Task TestUnmanagedConflictAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string conflict = Path.Combine(root, "game", "conflict.bin");
        await File.WriteAllTextAsync(conflict, "Unrelated file.");
        GameInstallPlan plan = Plan(new Version(1, 0), 'd', [FileEntry("game/conflict.bin", "conflict.bin", [1, 2])]);
        using var http = Client(_ => throw new InvalidOperationException("Conflicting plans must be rejected before download."));
        var installer = new VerifiedGameInstaller(http);
        try
        {
            await installer.InstallAsync(root, plan, null, CancellationToken.None);
            throw new InvalidOperationException("An unowned conflicting file was overwritten.");
        }
        catch (IOException) { }
        Require(await File.ReadAllTextAsync(conflict) == "Unrelated file." &&
                VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) is null,
            "An unrelated file conflict must not modify that file or publish metadata.");
    }

    private static async Task TestInsufficientSpaceAsync(string root)
    {
        Directory.CreateDirectory(root);
        string unrelated = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "Unrelated file.");
        GameFile hugeFile = FileEntry("game/file.bin", "file.bin", [1]) with { Size = long.MaxValue };
        GameInstallPlan plan = Plan(new Version(1, 0), 'e', [hugeFile]);
        using var http = Client(_ => throw new InvalidOperationException("Space must be checked before contacting the CDN."));
        var installer = new VerifiedGameInstaller(http);
        try
        {
            await installer.InstallAsync(root, plan, null, CancellationToken.None);
            throw new InvalidOperationException("An installation larger than the selected volume unexpectedly succeeded.");
        }
        catch (IOException ex) when (ex.Message.StartsWith("Insufficient remaining disk space.", StringComparison.Ordinal)) { }

        string workspace = Path.Combine(root, ".collapse-azurpromilia");
        Require(!Directory.Exists(Path.Combine(workspace, "stage")) && !File.Exists(Path.Combine(workspace, "pending.json")) &&
                !Directory.Exists(Path.Combine(root, "game")) && await File.ReadAllTextAsync(unrelated) == "Unrelated file.",
            "Insufficient space must not allocate staging payloads, start a pending install, or alter local files.");
    }

    private static GameInstallPlan Plan(Version version, char fingerprint, GameFile[] files) =>
        new("AzurPromiliaCN", version, new string(fingerprint, 64), BaseUri, files, ["game/empty"]);

    private static GameFile FileEntry(string installPath, string remotePath, byte[] contents) =>
        new(contents.Length, Convert.ToHexStringLower(MD5.HashData(contents)), installPath, remotePath);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new StubHandler(respond));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }
}
