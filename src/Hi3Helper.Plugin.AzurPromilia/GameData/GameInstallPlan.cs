using System.Diagnostics.CodeAnalysis;

namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal sealed record GameInstallPlan(
    string Profile,
    Version Version,
    string IndexSha256,
    IReadOnlyList<Uri> BaseUris,
    IReadOnlyList<GameFile> Files,
    IReadOnlyList<string> Directories)
{
    internal GameInstallPlan(string profile, Version version, string indexSha256, Uri baseUri,
        IReadOnlyList<GameFile> files, IReadOnlyList<string> directories)
        : this(profile, version, indexSha256, [baseUri], files, directories) { }
}

internal static class GameCdn
{
    internal static bool TryNormalizeRoot(Uri? candidate, [NotNullWhen(true)] out Uri? root)
    {
        root = null;
        if (candidate is null || !candidate.IsAbsoluteUri || candidate.Scheme != Uri.UriSchemeHttps ||
            candidate.UserInfo.Length != 0 || candidate.Query.Length != 0 || candidate.Fragment.Length != 0)
            return false;

        root = candidate.AbsolutePath.EndsWith('/') ? candidate : new Uri(candidate.AbsoluteUri + '/');
        return true;
    }

    internal static bool TryResolvePath(Uri root, string relativePath, [NotNullWhen(true)] out Uri? url) =>
        Uri.TryCreate(root, relativePath, out url) && url.Scheme == Uri.UriSchemeHttps && url.Authority == root.Authority &&
        url.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal);

    internal static bool IsTransferFailure(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        error is HttpRequestException or HttpIOException or EndOfStreamException or InvalidDataException or
            OperationCanceledException { InnerException: TimeoutException };
}
