using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
            await TestMirrorFailoverAsync(Path.Combine(temporary.FullName, "mirror"));
            await TestSingleCdnRestartAsync(Path.Combine(temporary.FullName, "single-cdn"));
            await TestCrc64InstallationAsync(Path.Combine(temporary.FullName, "crc64"));
            await TestOfficialUpdateAsync(Path.Combine(temporary.FullName, "official-update"));
            await TestPendingReleaseChangeAsync(Path.Combine(temporary.FullName, "pending-release-change"));
            TestJapanInventory(Path.Combine(temporary.FullName, "japan-inventory"));
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
                GameInstallPlan mirrored = plan with { BaseUris = [BaseUri, new Uri("https://mirror.example/assets/")] };
                await installer.InstallAsync(root, mirrored, (downloaded, _) =>
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
                Require(VerifiedGameInstaller.HasPendingInstallation(root) &&
                        VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) is null,
                    "A failed commit must leave a pending marker and hide the installed version.");
                Require(await installer.GetDownloadSizesAsync(root, update, CancellationToken.None) == (3L, 3L),
                    "A failed commit must keep verified staging available to the same plan.");
            }
        }

        using (var http = Client(_ => throw new InvalidOperationException("A verified staged file was downloaded again.")))
        {
            var installer = new VerifiedGameInstaller(http);
            await installer.InstallAsync(root, update, null, CancellationToken.None);
            Require(!VerifiedGameInstaller.HasPendingInstallation(root) &&
                    VerifiedGameInstaller.ReadInstalledVersion(root, update.Profile) == update.Version &&
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

    private static async Task TestMirrorFailoverAsync(string root)
    {
        byte[] oldBytes = [6, 7];
        byte[] newBytes = [1, 2, 3, 4, 5];
        const string local = "game/asset.bin";
        const string remote = "objects/asset.bin";
        string destination = Path.Combine(root, local);
        GameInstallPlan installed = Plan(new Version(1, 0), 'f', [FileEntry(local, remote, oldBytes)]);
        using (var first = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oldBytes) }))
            await new VerifiedGameInstaller(first).InstallAsync(root, installed, null, CancellationToken.None);

        Uri mirror = new("https://mirror.example/assets/");
        GameInstallPlan update = Plan(new Version(1, 1), 'b', [FileEntry(local, remote, newBytes)]) with { BaseUris = [BaseUri, mirror] };
        var requests = new List<(string? Url, string? Range)>();
        using var http = Client(request =>
        {
            Require(VerifiedGameInstaller.ReadInstalledVersion(root, installed.Profile) == installed.Version &&
                    File.ReadAllBytes(destination).SequenceEqual(oldBytes), "Source retries must leave the installed version and file intact.");
            requests.Add((request.RequestUri?.AbsoluteUri, request.Headers.Range?.ToString()));
            if (requests.Count == 1)
            {
                var interrupted = new StreamContent(new MemoryStream([9, 9]));
                interrupted.Headers.ContentLength = newBytes.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = interrupted };
            }
            if (requests.Count == 2)
            {
                var remainder = new ByteArrayContent(newBytes[2..]);
                remainder.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, newBytes.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = remainder };
            }
            Require(requests.Count == 3, "Source recovery must attempt a clean transfer only once.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newBytes) };
        });

        var progress = new List<(long Downloaded, long Total)>();
        await new VerifiedGameInstaller(http).InstallAsync(root, update, (done, total) => progress.Add((done, total)), CancellationToken.None);
        Require(requests.SequenceEqual([
            ("https://example.invalid/publisher/assets/objects/asset.bin", null),
            ("https://mirror.example/assets/objects/asset.bin", "bytes=2-4"),
            ("https://mirror.example/assets/objects/asset.bin", null)]),
            "The last mirror must resume the interrupted transfer and then restart when the old prefix fails verification.");
        Require(progress[0] == (0L, 5L) && progress.Contains((2L, 5L)) && progress.Count(value => value == (0L, 5L)) >= 2 &&
                progress[^1] == (5L, 5L) && progress.All(value => value.Downloaded >= 0 && value.Downloaded <= value.Total),
            "Progress must remove invalid staged bytes before counting the clean download.");
        Require(VerifiedGameInstaller.ReadInstalledVersion(root, update.Profile) == update.Version &&
                (await File.ReadAllBytesAsync(destination)).SequenceEqual(newBytes), "Only verified staging can replace the installed file.");
    }

    private static async Task TestSingleCdnRestartAsync(string root)
    {
        byte[] contents = [1, 2, 3, 4, 5];
        GameFile file = FileEntry("game/asset.bin", "asset.bin", contents);
        GameInstallPlan plan = Plan(new Version(1, 0), 'a', [file]);
        string staging = Path.Combine(root, ".collapse-azurpromilia", "stage", plan.Profile,
            plan.Version.ToString(), plan.IndexSha256);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "00000000.part"), [9, 9]);

        var ranges = new List<string?>();
        using var http = Client(request =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            if (request.Headers.Range is null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) };

            var remainder = new ByteArrayContent(contents[2..]);
            remainder.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, contents.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = remainder };
        });

        var installer = new VerifiedGameInstaller(http);
        await installer.InstallAsync(root, plan, null, CancellationToken.None);
        Require(ranges.SequenceEqual(["bytes=2-4", null]) &&
                (await File.ReadAllBytesAsync(Path.Combine(root, file.InstallPath))).SequenceEqual(contents) &&
                VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version,
            "A CDN must get a clean transfer after a corrupted resume even if there are no mirrors.");
    }

    private static async Task TestCrc64InstallationAsync(string root)
    {
        // This four-byte resource and its CRC come from Yostar's live JP 1.0.2 manifest and CDN.
        const string path = "AzurPromiliaJP_Data/StreamingAssets/.res/raw_streaming_package/06ujhj4pltkkp3tyowbnng.r";
        const ulong publishedCrc64 = 17628643879108190027UL;
        byte[] contents = [0, 0, 0, 0];
        GameFile file = GameFile.WithCrc64(contents.Length, publishedCrc64, path, path);
        var plan = new GameInstallPlan("AzurPromiliaJP", new Version(1, 0, 2), new string('a', 64), BaseUri, [file], []);

        string staging = Path.Combine(root, ".collapse-azurpromilia", "stage", plan.Profile,
            plan.Version.ToString(), plan.IndexSha256);
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "00000000.part"), contents[..2]);
        using (var http = Client(request =>
               {
                   Require(request.Headers.Range?.ToString() == "bytes=2-3", "The CRC64 file must resume its staged prefix.");
                   var remainder = new ByteArrayContent(contents[2..]);
                   remainder.Headers.ContentRange = new ContentRangeHeaderValue(2, 3, contents.Length);
                   return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = remainder };
               }))
        {
            var installer = new VerifiedGameInstaller(http);
            await installer.InstallAsync(root, plan, null, CancellationToken.None);
            Require(VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version,
                "A correctly resumed CRC64 file must install successfully.");
        }

        GameInstallPlan update = plan with
        {
            Version = new Version(1, 0, 3),
            IndexSha256 = new string('b', 64),
            Files = [GameFile.WithCrc64(contents.Length, publishedCrc64 ^ 1, path, path)]
        };
        using var wrong = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) });
        var rejecting = new VerifiedGameInstaller(wrong);
        try
        {
            await rejecting.InstallAsync(root, update, null, CancellationToken.None);
            throw new InvalidOperationException("A payload with the wrong CRC64 was accepted.");
        }
        catch (InvalidDataException) { }

        Require(await rejecting.GetDownloadSizesAsync(root, update, CancellationToken.None) == (4L, 0L) &&
                VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version &&
                (await File.ReadAllBytesAsync(Path.Combine(root, path))).SequenceEqual(contents),
            "A rejected CRC64 transfer must discard the bad staging and retain the installed version.");
    }

    private static async Task TestOfficialUpdateAsync(string root)
    {
        const string executable = "AzurPromilia.exe";
        byte[] older = [1, 2, 3];
        byte[] newer = [3, 2, 1];
        var oldVersion = new Version(1, 0);
        GameFile oldFile = FileEntry(executable, executable, older);
        GameInstallPlan plan = Plan(new Version(1, 1), 'b', [FileEntry(executable, executable, newer)]);
        string index = Path.Combine(root, "_index");
        Directory.CreateDirectory(index);
        await File.WriteAllTextAsync(Path.Combine(index, "version"), """{"version":"1.0"}""");
        using Aes cipher = Aes.Create();
        cipher.KeySize = 192;
        string listing = $"1,1,0\n0,{oldFile.Size},{oldFile.Md5},{executable},{executable}\n";
        byte[] encoded = cipher.EncryptCbc(Encoding.UTF8.GetBytes(listing), cipher.IV);
        await File.WriteAllTextAsync(Path.Combine(index, oldVersion.ToString()), Convert.ToBase64String(encoded));
        string destination = Path.Combine(root, executable);
        await File.WriteAllBytesAsync(destination, [9, 9, 9]);
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newer) });
        var installer = new VerifiedGameInstaller(http, (directory, _) =>
            OfficialGameInventory.ReadManjuu(directory, executable, oldVersion, cipher.Key, cipher.IV));

        try
        {
            await installer.GetDownloadSizesAsync(root, plan, CancellationToken.None);
            throw new InvalidOperationException("An official inventory granted ownership without checking the existing bytes.");
        }
        catch (IOException) { }

        await File.WriteAllBytesAsync(destination, older);
        Require(await installer.GetDownloadSizesAsync(root, plan, CancellationToken.None) == (newer.Length, 0L),
            "An official old client must be eligible for updates without an existing Collapse marker.");
        await installer.InstallAsync(root, plan, null, CancellationToken.None);
        Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(newer) &&
            VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version &&
            OfficialGameInventory.ReadManjuuVersion(root) == oldVersion,
            "Updating an official install must preserve its metadata and record the new Collapse version.");
    }

    private static async Task TestPendingReleaseChangeAsync(string root)
    {
        Directory.CreateDirectory(root);
        byte[] md5Payload = [1, 2, 3];
        byte[] crcPayload = "123456789"u8.ToArray();
        byte[] payload = [7, 7, 7];
        string conflict = Path.Combine(root, "conflict.bin");
        GameInstallPlan first = Plan(new Version(1, 0), 'b',
        [
            FileEntry("md5.bin", "md5.bin", md5Payload),
            GameFile.WithCrc64(crcPayload.Length, 0x995DC9BBDF1939FA, "crc64.bin", "crc64.bin"),
            FileEntry("conflict.bin", "conflict.bin", payload)
        ]);

        using var firstClient = Client(request =>
        {
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name == "conflict.bin")
                File.WriteAllText(conflict, "External file");
            byte[] body = name switch
            {
                "md5.bin" => md5Payload,
                "crc64.bin" => crcPayload,
                _ => payload
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });
        try
        {
            await new VerifiedGameInstaller(firstClient).InstallAsync(root, first, null, CancellationToken.None);
            throw new InvalidOperationException("A competing file must interrupt the commit.");
        }
        catch (IOException) { }

        Require(VerifiedGameInstaller.HasPendingInstallation(root) &&
            File.Exists(Path.Combine(root, "md5.bin")) && File.Exists(Path.Combine(root, "crc64.bin")),
            "Both verified files must be committed before the conflicting file interrupts installation.");
        GameInstallPlan next = Plan(new Version(1, 1), 'c', first.Files
            .Select(file => FileEntry(file.InstallPath, file.RemotePath, payload)).ToArray());
        using var nextClient = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var installer = new VerifiedGameInstaller(nextClient);
        try
        {
            await installer.GetDownloadSizesAsync(root, next, CancellationToken.None);
            throw new InvalidOperationException("The pending journal granted ownership of an unrelated conflicting file.");
        }
        catch (IOException) { }

        Require(await File.ReadAllTextAsync(conflict) == "External file", "The unrelated file must remain intact.");
        File.Delete(conflict);
        await installer.InstallAsync(root, next, null, CancellationToken.None);
        Require(!VerifiedGameInstaller.HasPendingInstallation(root) &&
            VerifiedGameInstaller.ReadInstalledVersion(root, next.Profile) == next.Version &&
            next.Files.All(file => File.ReadAllBytes(Path.Combine(root, file.InstallPath)).SequenceEqual(payload)),
            "The next release must safely replace both MD5 and CRC64 files from the interrupted installation.");
    }

    private static void TestJapanInventory(string root)
    {
        const string executable = "AzurPromiliaJP.exe";
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "game-launcher-config.json"),
            """{"tag":"AZUPRO_JP","name":"AzurPromiliaJP","params":["-start=azurpromilia_launcher"],"version":"1.0.2","vc":"fixture"}""");
        File.WriteAllText(Path.Combine(root, "manifest.json"),
            """{"name":"AZUPRO_JP","version":"1.0.2","basis":"prod/game.zip","vc":"fixture","files":[{"path":"/AzurPromiliaJP.exe","size":"9","hash":"11051210869376104954","vc":"fixture"}]}""");
        var version = new Version(1, 0, 2);
        Require(OfficialGameInventory.ReadJapanVersion(root, executable) == version &&
            OfficialGameInventory.ReadJapan(root, executable, version)
                .SequenceEqual([GameFile.WithCrc64(9, 0x995DC9BBDF1939FA, executable, executable)]),
            "Read local Japanese launcher metadata and its CRC64 file inventory together.");
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
