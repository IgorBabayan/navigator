using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Navigator.Core.Platform;
using Navigator.Core.Signatures;

namespace Navigator.Platform.ItemProperties;

/// <summary>Signature types shared by the verifiers.</summary>
internal static class SignedTypes
{
    /// <summary>PE images: their Authenticode signature can be decoded on every OS.</summary>
    public static readonly FrozenSet<string> PortableExecutables = new[]
    {
        ".exe", ".dll", ".sys", ".ocx", ".cpl", ".scr", ".drv", ".efi", ".mui", ".winmd", ".node",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Other types Windows signs through subject interface packages (installers, packages, scripts, catalogs).</summary>
    public static readonly FrozenSet<string> WindowsOther = new[]
    {
        ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle", ".cab", ".cat", ".ps1", ".psm1", ".psd1", ".vbs", ".js", ".wsf",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Signature decoded from the file itself, without a trust decision.</summary>
    public static SignatureVerification DecodeOnly(string path)
    {
        try
        {
            return AuthenticodeReader.ReadEmbedded(path) is { } signature
                ? new SignatureVerification(SignatureStatus.NotVerified, SignatureSource.Embedded, signature)
                : new SignatureVerification(SignatureStatus.NotSigned, SignatureSource.Embedded, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return new SignatureVerification(SignatureStatus.NotSigned, SignatureSource.Embedded, null);
        }
    }
}

/// <summary>Linux and others: Authenticode signatures of Windows programs are shown, but trust cannot be decided.</summary>
public sealed class DecodingSignatureVerifier : ISignatureVerifier
{
    public bool CanVerify(string path) => SignedTypes.PortableExecutables.Contains(Path.GetExtension(path));

    public Task<SignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => SignedTypes.DecodeOnly(path), cancellationToken);
}

/// <summary>
/// Windows: WinVerifyTrust (the check Explorer's "Digital Signatures" tab and SmartScreen use), offline — revocation is
/// not checked and no URL is fetched, so the tab never waits on the network. Files without an embedded signature are
/// looked up in the system's security catalogs (most Windows components are catalog-signed).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsSignatureVerifier : ISignatureVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustESubjectNotTrusted = unchecked((int)0x800B0004);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int CertERevoked = unchecked((int)0x800B010C);
    private const int CryptEFileError = unchecked((int)0x80092003);

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private const uint DisableMd2Md4 = 0x00002000;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public bool CanVerify(string path)
    {
        var extension = Path.GetExtension(path);
        return SignedTypes.PortableExecutables.Contains(extension) || SignedTypes.WindowsOther.Contains(extension);
    }

    public Task<SignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Verify(path), cancellationToken);

    private static SignatureVerification Verify(string path)
    {
        var result = VerifyFile(path);
        var isPe = SignedTypes.PortableExecutables.Contains(Path.GetExtension(path));
        if (result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
        {
            if (VerifyCatalog(path) is { } catalog)
                return catalog;
            return new SignatureVerification(SignatureStatus.NotSigned, SignatureSource.Embedded, null);
        }

        EmbeddedSignature? signature = null;
        if (isPe)
        {
            try
            {
                signature = AuthenticodeReader.ReadEmbedded(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                // The trust result is still shown.
            }
        }

        return new SignatureVerification(MapStatus(result), SignatureSource.Embedded, signature);
    }

    private static SignatureStatus MapStatus(int result) => result switch
    {
        0 => SignatureStatus.Valid,
        TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureStatus.NotSigned,
        TrustEBadDigest or CryptEFileError => SignatureStatus.Invalid,
        CertEExpired => SignatureStatus.Expired,
        TrustESubjectNotTrusted or TrustEExplicitDistrust or CertEUntrustedRoot or CertEChaining or CertERevoked => SignatureStatus.Untrusted,
        _ => SignatureStatus.Invalid,
    };

    private static int VerifyFile(string path)
    {
        fixed (char* file = path)
        {
            var info = new FileInfoData { Size = (uint)sizeof(FileInfoData), FilePath = file };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                Union = &info,
                StateAction = StateActionVerify,
                ProviderFlags = CacheOnlyUrlRetrieval | DisableMd2Md4,
            };
            return Run(&data);
        }
    }

    private static int Run(TrustData* data)
    {
        var action = GenericVerifyV2;
        var result = WinVerifyTrust(-1, &action, data);
        data->StateAction = StateActionClose;
        _ = WinVerifyTrust(-1, &action, data);
        return result;
    }

    /// <summary>Finds a catalog that lists the file's hash and verifies the file against it.</summary>
    private static SignatureVerification? VerifyCatalog(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hash = stackalloc byte[64];
        using (handle)
        {
            foreach (var algorithm in new[] { "SHA256", "SHA1" })
            {
                nint admin = 0;
                if (!CryptCATAdminAcquireContext2(&admin, null, algorithm, 0, 0) || admin == 0)
                    continue;
                try
                {
                    uint hashLength = 0;
                    _ = CryptCATAdminCalcHashFromFileHandle2(admin, handle.DangerousGetHandle(), &hashLength, null, 0);
                    if (hashLength is 0 or > 64)
                        continue;
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle.DangerousGetHandle(), &hashLength, hash, 0))
                        continue;
                    var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLength, 0, null);
                    if (catalog == 0)
                        continue;
                    try
                    {
                        CatalogInfo info = default;
                        info.Size = (uint)sizeof(CatalogInfo);
                        if (!CryptCATCatalogInfoFromContext(catalog, &info, 0))
                            continue;
                        var catalogPath = new string(info.File);
                        var tag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)hashLength));
                        int result;
                        fixed (char* catalogFile = catalogPath)
                        fixed (char* member = tag)
                        fixed (char* memberPath = path)
                        {
                            var catalogData = new CatalogData
                            {
                                Size = (uint)sizeof(CatalogData),
                                CatalogFilePath = catalogFile,
                                MemberTag = member,
                                MemberFilePath = memberPath,
                                MemberFile = handle.DangerousGetHandle(),
                                CalculatedFileHash = hash,
                                CalculatedFileHashLength = hashLength,
                                CatalogAdmin = admin,
                            };
                            var data = new TrustData
                            {
                                Size = (uint)sizeof(TrustData),
                                UiChoice = UiNone,
                                RevocationChecks = RevokeNone,
                                UnionChoice = ChoiceCatalog,
                                Union = &catalogData,
                                StateAction = StateActionVerify,
                                ProviderFlags = CacheOnlyUrlRetrieval | DisableMd2Md4,
                            };
                            result = Run(&data);
                        }

                        EmbeddedSignature? signature = null;
                        try
                        {
                            signature = AuthenticodeReader.Decode(File.ReadAllBytes(catalogPath));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // The status is still shown without the signer.
                        }

                        return new SignatureVerification(MapStatus(result), SignatureSource.Catalog, signature, catalogPath);
                    }
                    finally
                    {
                        _ = CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    }
                }
                finally
                {
                    _ = CryptCATAdminReleaseContext(admin, 0);
                }
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoData
    {
        public uint Size;
        public char* FilePath;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CatalogData
    {
        public uint Size;
        public uint CatalogVersion;
        public char* CatalogFilePath;
        public char* MemberTag;
        public char* MemberFilePath;
        public nint MemberFile;
        public byte* CalculatedFileHash;
        public uint CalculatedFileHashLength;
        public nint CatalogContext;
        public nint CatalogAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public void* Union;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CatalogInfo
    {
        public uint Size;
        public fixed char File[260];
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, Guid* action, TrustData* data);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(nint* admin, Guid* subsystem, string hashAlgorithm, nint strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(nint admin, nint file, uint* hashLength, byte* hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial nint CryptCATAdminEnumCatalogFromHash(nint admin, byte* hash, uint hashLength, uint flags, nint* previous);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(nint catalog, CatalogInfo* info, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(nint admin, nint catalog, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(nint admin, uint flags);
}

/// <summary>macOS: codesign for applications, bundles and libraries; pkgutil for installer packages.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSignatureVerifier : ISignatureVerifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly FrozenSet<string> CodeSigned = new[] { ".app", ".dylib", ".framework", ".bundle", ".kext", ".appex", ".plugin", ".xpc", ".dmg" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool CanVerify(string path)
    {
        var extension = Path.GetExtension(PathTrim(path));
        return CodeSigned.Contains(extension) || extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase) || SignedTypes.PortableExecutables.Contains(extension);
    }

    public async Task<SignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathTrim(path);
        var extension = Path.GetExtension(path);
        if (SignedTypes.PortableExecutables.Contains(extension))
            return await Task.Run(() => SignedTypes.DecodeOnly(path), cancellationToken).ConfigureAwait(false);

        if (extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            var package = await ProcessRunner.RunCapturingAsync("/usr/sbin/pkgutil", ["--check-signature", path], Timeout, cancellationToken).ConfigureAwait(false);
            if (package is not { } output)
                return new SignatureVerification(SignatureStatus.NotVerified, SignatureSource.CodeSign, null);
            var text = output.Output;
            var status = text.Contains("Status: no signature", StringComparison.OrdinalIgnoreCase) ? SignatureStatus.NotSigned
                : output.ExitCode == 0 && text.Contains("Status: signed", StringComparison.OrdinalIgnoreCase) ? SignatureStatus.Valid
                : text.Contains("expired", StringComparison.OrdinalIgnoreCase) ? SignatureStatus.Expired
                : SignatureStatus.Untrusted;
            return new SignatureVerification(status, SignatureSource.CodeSign, null, Details: ParseLines(text, ':'));
        }

        var verify = await ProcessRunner.RunCapturingAsync("/usr/bin/codesign", ["--verify", "--strict", path], Timeout, cancellationToken).ConfigureAwait(false);
        var display = await ProcessRunner.RunCapturingAsync("/usr/bin/codesign", ["-dvv", path], Timeout, cancellationToken).ConfigureAwait(false);
        if (verify is not { } check)
            return new SignatureVerification(SignatureStatus.NotVerified, SignatureSource.CodeSign, null);

        var errors = check.Error;
        var result = check.ExitCode == 0 ? SignatureStatus.Valid
            : errors.Contains("not signed at all", StringComparison.OrdinalIgnoreCase) ? SignatureStatus.NotSigned
            : errors.Contains("CSSMERR_TP_CERT_EXPIRED", StringComparison.Ordinal) ? SignatureStatus.Expired
            : errors.Contains("modified", StringComparison.OrdinalIgnoreCase) || errors.Contains("invalid", StringComparison.OrdinalIgnoreCase) ? SignatureStatus.Invalid
            : SignatureStatus.Untrusted;
        var details = display is { } shown ? ParseLines(shown.Error, '=') : [];
        return new SignatureVerification(result, SignatureSource.CodeSign, null, Details: details);
    }

    private static string PathTrim(string path) => path.TrimEnd('/');

    /// <summary>"Key=Value" (codesign) or "Key: Value" lines; repeated keys (Authority) are numbered by order.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseLines(string text, char separator)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = raw.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index == raw.Length - 1)
                continue;
            var key = raw[..index].Trim();
            if (key is "Executable" or "CodeDirectory v" or "Hash choices" or "CMSDigest" or "CMSDigestType" or "CDHash" or "Sealed Resources version" or "Internal requirements count")
                continue;
            result.Add(new KeyValuePair<string, string>(key, raw[(index + 1)..].Trim()));
        }

        return result;
    }
}
