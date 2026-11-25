using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class GameFileDownloadTests
{
    internal static async Task RunAsync()
    {
        byte[] expected = [1, 2, 3, 4, 5];
        var file = new GameFile(expected.Length, Convert.ToHexStringLower(MD5.HashData(expected)), "data/file", "file");
        var uri = new Uri("https://example.invalid/file");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("AzurPromiliaFileDownloadTests-");
        string partial = Path.Combine(directory.FullName, "fixture.part");
        try
        {
            using (var http = Client(request =>
                   {
                       Require(request.Headers.Range is null, "A fresh transfer must not send Range.");
                       return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) };
                   }))
            {
                await VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, null, CancellationToken.None);
                Require(await VerifiedGameFileDownloader.VerifyAsync(partial, file, CancellationToken.None),
                    "A valid full response failed verification.");
            }

            await File.WriteAllBytesAsync(partial, expected[..2]);
            var progress = new List<long>();
            using (var http = Client(request =>
                   {
                       Require(request.Headers.Range?.ToString() == "bytes=2-4", "The existing partial must be resumed.");
                       return RangeResponse(expected[2..], 2, 4, 5);
                   }))
            {
                await VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, progress.Add, CancellationToken.None);
                Require(progress[0] == 2 && progress[^1] == file.Size &&
                    await VerifiedGameFileDownloader.VerifyAsync(partial, file, CancellationToken.None),
                    "A valid partial response was not appended and verified.");
            }

            await File.WriteAllBytesAsync(partial, expected[..2]);
            using (var http = Client(_ => RangeResponse(expected[1..], 1, 4, 5)))
            {
                await RequireInvalidDataAsync(() => VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, null, CancellationToken.None));
                Require((await File.ReadAllBytesAsync(partial)).SequenceEqual(expected[..2]),
                    "An invalid Content-Range changed the existing partial.");
            }

            int attempts = 0;
            using (var http = Client(_ => ++attempts == 1
                   ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) }
                   : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            {
                try
                {
                    await VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, null, CancellationToken.None);
                    throw new InvalidOperationException("An unavailable replacement response was accepted.");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    Require((await File.ReadAllBytesAsync(partial)).SequenceEqual(expected[..2]),
                        "An ignored Range followed by an HTTP failure must not discard the existing prefix.");
                }
            }

            attempts = 0;
            progress.Clear();
            using (var http = Client(request =>
                   {
                       attempts++;
                       Require(attempts == 1 ? request.Headers.Range?.ToString() == "bytes=2-4" : request.Headers.Range is null,
                           "An ignored Range must be retried once from zero.");
                       return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) };
                   }))
            {
                await VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, progress.Add, CancellationToken.None);
                Require(attempts == 2 && progress.Contains(0L) &&
                    await VerifiedGameFileDownloader.VerifyAsync(partial, file, CancellationToken.None),
                    "Restarting after an ignored Range did not produce a verified file.");
            }

            File.Delete(partial);
            progress.Clear();
            using (var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new ByteArrayContent([5, 4, 3, 2, 1])
                   }))
            {
                await RequireInvalidDataAsync(() => VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, progress.Add, CancellationToken.None));
                Require(progress[^1] == 0 && !await VerifiedGameFileDownloader.VerifyAsync(partial, file, CancellationToken.None),
                    "A size-matching response with the wrong MD5 was accepted or retained.");
            }

            using var cancellation = new CancellationTokenSource();
            using (var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StreamContent(new ShortReadStream(expected))
                   }))
            {
                try
                {
                    await VerifiedGameFileDownloader.DownloadAsync(http, uri, partial, file, bytes =>
                    {
                        if (bytes == 2)
                            cancellation.Cancel();
                    }, cancellation.Token);
                    throw new InvalidOperationException("The transfer ignored cancellation.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Require((await File.ReadAllBytesAsync(partial)).SequenceEqual(expected[..2]),
                        "Canceling a transfer must retain previously written bytes for resume.");
                }
            }
        }
        finally
        {
            File.Delete(partial);
            directory.Delete();
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new StubHandler(respond));

    private static HttpResponseMessage RangeResponse(byte[] body, long start, long end, long size)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, size);
        return response;
    }

    private static async Task RequireInvalidDataAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("The transfer should have rejected invalid data.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

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
