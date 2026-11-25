using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hi3Helper.Plugin.AzurPromilia;

internal static partial class Authenticode
{
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static unsafe bool IsSignedBy(string path, string publisher)
    {
        fixed (char* pathPointer = path)
        {
            var file = new WinTrustFile { Size = sizeof(WinTrustFile), Path = pathPointer };
            var data = new WinTrustData
            {
                Size = sizeof(WinTrustData),
                UiChoice = 2, // WTD_UI_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = &file,
                StateAction = 1 // WTD_STATEACTION_VERIFY
            };
            Guid action = VerifyV2;
            try
            {
                if (WinVerifyTrust(-1, ref action, ref data) != 0)
                    return false;

                nint provider = WTHelperProvDataFromStateData(data.StateData);
                nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                ProviderCertificate* entry = signer == 0 ? null : WTHelperGetProvCertFromChain(signer, 0);
                if (entry is null || entry->Context is null)
                    return false;

                CertificateContext* context = entry->Context;
                using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(
                    new ReadOnlySpan<byte>(context->Encoded, context->Length));
                return string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), publisher, StringComparison.Ordinal);
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                _ = WinVerifyTrust(-1, ref action, ref data);
            }
        }
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint state);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint provider, int signerIndex, int counterSigner, int counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static unsafe partial ProviderCertificate* WTHelperGetProvCertFromChain(nint signer, int certificateIndex);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProviderCertificate
    {
        internal int Size;
        internal CertificateContext* Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CertificateContext
    {
        internal int Encoding;
        internal byte* Encoded;
        internal int Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustFile
    {
        internal int Size;
        internal char* Path;
        internal nint Handle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WinTrustData
    {
        internal int Size;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal int UiChoice;
        internal int RevocationChecks;
        internal int UnionChoice;
        internal WinTrustFile* File;
        internal int StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal int ProviderFlags;
        internal int UiContext;
        internal nint SignatureSettings;
    }
}
