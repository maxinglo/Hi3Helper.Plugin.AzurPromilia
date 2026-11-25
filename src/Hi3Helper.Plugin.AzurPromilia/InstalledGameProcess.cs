using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hi3Helper.Plugin.AzurPromilia;

internal sealed partial class InstalledGameProcess(SafeProcessHandle handle, DateTime started) : IDisposable
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x00000102;

    internal DateTime Started { get; } = started;

    internal static InstalledGameProcess? Find(string? executable)
    {
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable))
            return null;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        }
        catch (Win32Exception)
        {
            return null;
        }

        try
        {
            foreach (Process candidate in candidates)
            {
                SafeProcessHandle process = OpenProcess(QueryLimitedInformation | Synchronize, false, candidate.Id);
                if (!process.IsInvalid && HasImage(process, executable) &&
                    GetProcessTimes(process, out long created, out _, out _, out _) &&
                    WaitForSingleObject(process, 0) == WaitTimeout)
                {
                    return new InstalledGameProcess(process, DateTime.FromFileTime(created));
                }

                process.Dispose();
            }

            return null;
        }
        finally
        {
            foreach (Process candidate in candidates)
                candidate.Dispose();
        }
    }

    internal async Task WaitForExitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (WaitForSingleObject(handle, 0) == WaitTimeout)
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
    }

    public void Dispose() => handle.Dispose();

    private static unsafe bool HasImage(SafeProcessHandle process, string expected)
    {
        int length = expected.Length + 1;
        Span<char> buffer = length <= 512 ? stackalloc char[length] : new char[length];
        fixed (char* image = buffer)
        {
            return QueryFullProcessImageName(process, 0, image, ref length) &&
                buffer[..length].Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int id);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* image, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint timeoutMilliseconds);
}
