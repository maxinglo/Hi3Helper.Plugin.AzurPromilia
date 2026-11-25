using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Hi3Helper.Plugin.AzurPromilia;
using Hi3Helper.Plugin.AzurPromilia.GameData;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;

internal static class ChinaInstallationAcceptance
{
    private const int MiB = 1024 * 1024;
    private const int CancelAfterBytes = 64 * 1024;

    internal static async Task RunAsync(string gameDirectory, bool adopt = false)
    {
        string output = Path.GetFullPath(Path.Combine("artifacts", "acceptance"));
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "china-integrity.json");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var report = new Report(root, adopt);
        var manager = new OfficialLauncherManager(AzurPromiliaRegion.China);
        try
        {
            manager.SetGamePath(root);
            Guid request = Guid.CreateVersion7();
            manager.InitAsync(in request, out nint initialized);
            if (await initialized.AsTask<int>().WaitAsync(TimeSpan.FromMinutes(3)) != 0)
                throw new InvalidDataException("The China game manager could not initialize.");

            GameInstallPlan plan = manager.RequireInstallPlan();
            manager.GetGamePath(out string? activePath);
            if (activePath is null || !string.Equals(root, activePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pass the actual game directory to the China acceptance command.");

            manager.GetApiGameVersion(out GameVersion offered);
            manager.GetCurrentGameVersion(out GameVersion current);
            report.ReleaseVersion = plan.Version.ToString();
            report.ApiVersion = offered.ToString();
            report.InstalledVersionBefore = current.ToString();
            report.IndexSha256 = plan.IndexSha256;
            report.TotalFiles = plan.Files.Count;
            report.TotalBytes = plan.Files.Sum(file => file.Size);

            report.Stage = "integrity";
            await VerifyInstallationAsync(root, plan, report);

            report.Stage = "range-resume";
            GameFile sample = plan.Files.FirstOrDefault(file => file.Size is >= MiB and <= 8 * MiB)
                ?? throw new InvalidDataException("The China index has no sample in the requested size range.");
            var resume = new ResumeCheck(sample);
            report.Resume = resume;
            await VerifyResumeAsync(root, output, plan, resume);

            if (report.Failures.Count != 0)
            {
                report.Stage = "integrity";
                throw new InvalidDataException("The existing China installation did not match the publisher index.");
            }
            if (!resume.Verified)
                throw new InvalidDataException("The China CDN range-resume check failed.");

            if (adopt)
            {
                report.Stage = "adoption";
                if (report.MissingDirectories.Count != 0)
                    throw new InvalidDataException("The index lists missing directories. Adoption cannot proceed without creating them.");
                await AdoptAsync(manager, root, plan, report);
            }

            report.Stage = "complete";
            report.Passed = true;
        }
        catch (Exception error)
        {
            report.ErrorType = error.GetType().Name;
            throw new InvalidOperationException($"China acceptance failed during {report.Stage}. See {reportPath}.");
        }
        finally
        {
            manager.Dispose();
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"China integrity report written to {reportPath}");
        }
    }

    private static async Task VerifyInstallationAsync(string root, GameInstallPlan plan, Report report)
    {
        long lastUpdate = Stopwatch.GetTimestamp();
        foreach (GameFile file in plan.Files)
        {
            string path = Path.Combine(root, file.InstallPath);
            try
            {
                if (await VerifiedGameFileDownloader.VerifyAsync(path, file, CancellationToken.None))
                {
                    report.VerifiedFiles++;
                    report.VerifiedBytes += file.Size;
                }
                else
                {
                    report.Failures.Add(new(file.InstallPath, File.Exists(path) ? "SizeOrChecksumMismatch" : "MissingFile"));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add(new(file.InstallPath, error.GetType().Name));
            }

            report.CheckedFiles++;
            if (report.CheckedFiles % 1000 == 0 || report.CheckedFiles == report.TotalFiles ||
                Stopwatch.GetElapsedTime(lastUpdate) >= TimeSpan.FromSeconds(2))
            {
                Console.WriteLine($"China integrity {report.CheckedFiles:N0}/{report.TotalFiles:N0} files, " +
                                  $"{report.VerifiedBytes:N0}/{report.TotalBytes:N0} bytes verified, {report.Failures.Count:N0} failures");
                lastUpdate = Stopwatch.GetTimestamp();
            }
        }

        foreach (string directory in plan.Directories)
        {
            if (!Directory.Exists(Path.Combine(root, directory)))
                report.MissingDirectories.Add(directory);
        }
    }

    private static async Task VerifyResumeAsync(string root, string output, GameInstallPlan plan, ResumeCheck check)
    {
        GameFile sample = check.Sample;
        string remotePath = string.Join('/', sample.RemotePath.Split('/').Select(Uri.EscapeDataString));
        if (!GameCdn.TryNormalizeRoot(plan.BaseUris[0], out Uri? baseUri) ||
            !GameCdn.TryResolvePath(baseUri, remotePath, out Uri? download))
            throw new InvalidDataException("The China sample could not be resolved on its publisher CDN.");

        var recording = new RangeRecorder(check.Requests);
        using var http = new HttpClient(recording);
        string partial = Path.Combine(output, $"china-resume-{Guid.NewGuid():N}.part");
        try
        {
            using var first = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool canceledOnProgress = false;
            try
            {
                await VerifiedGameFileDownloader.DownloadAsync(http, download, partial, sample, bytes =>
                {
                    if (bytes > CancelAfterBytes && bytes < sample.Size)
                    {
                        canceledOnProgress = true;
                        first.Cancel();
                    }
                }, first.Token);
            }
            catch (OperationCanceledException) when (canceledOnProgress) { }

            check.PartialBytes = new FileInfo(partial).Length;
            if (!canceledOnProgress || check.PartialBytes <= CancelAfterBytes || check.PartialBytes >= sample.Size)
                throw new InvalidDataException("Cancellation did not leave the expected on-disk prefix.");

            using var second = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await VerifiedGameFileDownloader.DownloadAsync(http, download, partial, sample, null, second.Token);
            check.FinalBytes = new FileInfo(partial).Length;
            check.Verified = check.Requests is
                [{ Range: null, Status: HttpStatusCode.OK }, { Status: HttpStatusCode.PartialContent } resumed] &&
                resumed.Range == $"bytes={check.PartialBytes}-{sample.Size - 1}" && resumed.From == check.PartialBytes &&
                check.FinalBytes == sample.Size &&
                await VerifiedGameFileDownloader.VerifyAsync(partial, sample, second.Token) &&
                await FilesMatchAsync(partial, Path.Combine(root, sample.InstallPath), second.Token);
            if (!check.Verified)
                check.Failure = "The CDN range response, checksum, size or bytes differed from the installed sample.";
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or
                                      UnauthorizedAccessException or OperationCanceledException)
        {
            check.Failure = error is InvalidDataException ? error.Message : error.GetType().Name;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private static async Task<bool> FilesMatchAsync(string partial, string existing, CancellationToken token)
    {
        byte[] downloaded = await File.ReadAllBytesAsync(partial, token);
        byte[] installed = await File.ReadAllBytesAsync(existing, token);
        return downloaded.AsSpan().SequenceEqual(installed);
    }

    private static async Task AdoptAsync(OfficialLauncherManager manager, string root, GameInstallPlan plan, Report report)
    {
        var installer = new OfficialLauncherInstaller(manager, new RejectDownloadHandler());
        try
        {
            Guid request = Guid.CreateVersion7();
            installer.StartInstallAsync((in InstallProgress progress) =>
            {
                if (progress.TotalBytesToDownload != 0)
                    throw new InvalidOperationException("Adoption requires every game file to be present and valid.");
            }, state => Console.WriteLine($"China adoption {state}"), in request, out nint installation);
            await installation.AsTask();

            manager.GetCurrentGameVersion(out GameVersion installed);
            manager.GetApiGameVersion(out GameVersion offered);
            report.InstalledVersionAfter = installed.ToString();
            report.Adopted = offered == installed && VerifiedGameInstaller.ReadInstalledVersion(root, plan.Profile) == plan.Version;
            if (!report.Adopted)
                throw new InvalidDataException("The installed version and the publisher release differ after adoption.");
        }
        finally
        {
            installer.Dispose();
        }
    }

    private sealed class RangeRecorder(List<RangeResponse> requests) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpResponseMessage response = await base.SendAsync(request, token);
            requests.Add(new(request.Headers.Range?.ToString(), response.Content.Headers.ContentRange?.From, response.StatusCode));
            return response;
        }
    }

    private sealed class RejectDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Adoption cannot download or replace game files.");
    }

    private sealed record FileFailure(string Path, string Reason);
    private sealed record RangeResponse(string? Range, long? From, HttpStatusCode Status);

    private sealed class ResumeCheck(GameFile sample)
    {
        internal GameFile Sample { get; } = sample;
        public string Path => Sample.InstallPath;
        public long ExpectedBytes => Sample.Size;
        public string? ExpectedMd5 => Sample.Md5;
        public long PartialBytes { get; set; }
        public long FinalBytes { get; set; }
        public List<RangeResponse> Requests { get; } = [];
        public bool Verified { get; set; }
        public string? Failure { get; set; }
    }

    private sealed class Report(string root, bool adopt)
    {
        public string GameDirectory { get; } = root;
        public bool AdoptRequested { get; } = adopt;
        public bool Adopted { get; set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public string Stage { get; set; } = "release";
        public string? ErrorType { get; set; }
        public bool Passed { get; set; }
        public string? ReleaseVersion { get; set; }
        public string? ApiVersion { get; set; }
        public string? InstalledVersionBefore { get; set; }
        public string? InstalledVersionAfter { get; set; }
        public string? IndexSha256 { get; set; }
        public int TotalFiles { get; set; }
        public long TotalBytes { get; set; }
        public int CheckedFiles { get; set; }
        public int VerifiedFiles { get; set; }
        public long VerifiedBytes { get; set; }
        public List<FileFailure> Failures { get; } = [];
        public List<string> MissingDirectories { get; } = [];
        public ResumeCheck? Resume { get; set; }
    }
}
