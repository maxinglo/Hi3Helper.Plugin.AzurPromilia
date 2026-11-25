using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class VerifiedGameFileDownloader
{
    private const int BufferSize = 128 * 1024;

    internal static async Task DownloadAsync(HttpClient http, Uri downloadUri, string partialPath, GameFile file,
        Action<long>? reportDownloadedSize, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.Size < 0)
            throw new InvalidDataException("The manifest contains a negative game file size.");

        await using var partial = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (partial.Length > file.Size)
            partial.SetLength(0);
        if (partial.Length == file.Size)
        {
            bool valid = await VerifyStreamAsync(partial, file, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (valid)
            {
                reportDownloadedSize?.Invoke(file.Size);
                return;
            }
            if (file.Size == 0)
                throw new InvalidDataException("The expected checksum for an empty game file does not match.");
            partial.SetLength(0);
        }

        long offset = partial.Length;
        reportDownloadedSize?.Invoke(offset);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0)
                request.Headers.Range = new RangeHeaderValue(offset, file.Size - 1);

            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                // Do not discard the partial until the replacement full response has been validated.
                offset = 0;
                continue;
            }

            ValidateResponse(response, offset, file.Size);
            token.ThrowIfCancellationRequested();
            if (partial.Length != offset)
            {
                partial.SetLength(offset);
                reportDownloadedSize?.Invoke(offset);
            }
            await CopyBodyAsync(response.Content, partial, offset, file.Size, reportDownloadedSize, token)
                .ConfigureAwait(false);
            break;
        }

        if (!await VerifyStreamAsync(partial, file, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            partial.SetLength(0);
            reportDownloadedSize?.Invoke(0);
            throw new InvalidDataException("The downloaded game file does not match the manifest size and MD5.");
        }
    }

    internal static async Task<bool> VerifyAsync(string path, GameFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await VerifyStreamAsync(input, file, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ValidateResponse(HttpResponseMessage response, long offset, long size)
    {
        HttpContentHeaders headers = response.Content.Headers;
        if (offset == 0)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("The CDN did not return a full game file.");
        }
        else
        {
            ContentRangeHeaderValue? range = headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null ||
                !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != offset ||
                range.To != size - 1 || range.Length != size)
                throw new InvalidDataException("The CDN did not return the requested game file byte range.");
        }

        if ((headers.ContentLength is long length && length != size - offset) ||
            headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The CDN response body does not match the expected game file transfer.");
    }

    private static async Task CopyBodyAsync(HttpContent content, FileStream partial, long offset, long size,
        Action<long>? reportDownloadedSize, CancellationToken token)
    {
        await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        partial.Position = offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long written = offset;
            while (written < size)
            {
                int count = (int)Math.Min(size - written, BufferSize);
                int read = await input.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The game file transfer ended before the expected size.");

                await partial.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                written += read;
                reportDownloadedSize?.Invoke(written);
            }

            if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            {
                partial.SetLength(offset);
                reportDownloadedSize?.Invoke(offset);
                throw new InvalidDataException("The game file transfer exceeds the expected size.");
            }

            await partial.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> VerifyStreamAsync(FileStream stream, GameFile file, CancellationToken token)
    {
        if (stream.Length != file.Size)
            return false;

        stream.Position = 0;
        byte[] digest = await MD5.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(digest).Equals(file.Md5, StringComparison.OrdinalIgnoreCase);
    }
}
