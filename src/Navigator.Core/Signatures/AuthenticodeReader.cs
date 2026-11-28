using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Navigator.Core.Signatures;

/// <summary>One certificate of a signature, flattened for display.</summary>
public sealed record CertificateSummary(
    string Subject,
    string Issuer,
    string SerialNumber,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter)
{
    /// <summary>The subject's common name ("CN=Contoso Ltd, O=…" → "Contoso Ltd"), or the whole subject.</summary>
    public string DisplayName => CommonName(Subject);

    public string IssuerDisplayName => CommonName(Issuer);

    private static string CommonName(string distinguishedName)
    {
        foreach (var part in distinguishedName.Split(", ", StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..].Trim('"');
        }

        return distinguishedName;
    }
}

/// <summary>Who signed a file and how, as read from its embedded PKCS #7 signature (not a trust decision).</summary>
public sealed record EmbeddedSignature(
    CertificateSummary Signer,
    IReadOnlyList<CertificateSummary> Chain,
    string DigestAlgorithm,
    DateTime? SigningTime,
    int NestedSignatures);

/// <summary>
/// Reads Authenticode signatures embedded in Windows programs and libraries (PE files: .exe, .dll, .sys, .msi is not
/// PE) and PKCS #7 blobs such as catalog files, with the base class library only: the PE certificate table is located
/// by hand, the SignedData structure is decoded with <see cref="AsnReader"/>, certificates are loaded with
/// <see cref="X509CertificateLoader"/>. Works on every OS; whether the signature is <em>trusted</em> is the platform's
/// decision (WinVerifyTrust on Windows).
/// </summary>
public static class AuthenticodeReader
{
    private const int MaxSignatureBytes = 16 * 1024 * 1024;
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string CounterSignatureOid = "1.2.840.113549.1.9.6";
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";
    private const string TimestampTokenOid = "1.3.6.1.4.1.311.3.3.1";
    private const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";

    /// <summary>True when the file starts like a PE image ("MZ" + "PE\0\0").</summary>
    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = Open(path);
            return TryLocateCertificateTable(stream, out _, out _) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The PKCS #7 SignedData of a signed PE file; null when it is not a PE file or carries no signature.</summary>
    public static byte[]? ReadSignatureBlob(string path)
    {
        using var stream = Open(path);
        if (TryLocateCertificateTable(stream, out var offset, out var size) is not true || size < 8 || size > MaxSignatureBytes || offset + size > stream.Length)
            return null;

        // WIN_CERTIFICATE: dwLength, wRevision, wCertificateType (2 = PKCS #7 SignedData), bCertificate.
        Span<byte> header = stackalloc byte[8];
        stream.Position = offset;
        stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (type != 2 || length <= 8 || length > size)
            return null;
        var blob = new byte[length - 8];
        stream.ReadExactly(blob);
        return blob;
    }

    /// <summary>Decodes a PKCS #7 SignedData blob (an embedded signature or a .cat file); null when it is not one.</summary>
    public static EmbeddedSignature? Decode(byte[] pkcs7)
    {
        ArgumentNullException.ThrowIfNull(pkcs7);
        try
        {
            return DecodeSignedData(pkcs7);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads and decodes the embedded signature of a PE file.</summary>
    public static EmbeddedSignature? ReadEmbedded(string path)
        => ReadSignatureBlob(path) is { } blob ? Decode(blob) : null;

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    /// <summary>
    /// Null: not a PE file. False: PE without a certificate table. True: the table's file offset and size.
    /// </summary>
    private static bool? TryLocateCertificateTable(Stream stream, out long offset, out int size)
    {
        offset = 0;
        size = 0;
        if (stream.Length < 0x40)
            return null;
        Span<byte> buffer = stackalloc byte[24];
        stream.Position = 0;
        stream.ReadExactly(buffer[..2]);
        if (buffer[0] != 'M' || buffer[1] != 'Z')
            return null;
        stream.Position = 0x3C;
        stream.ReadExactly(buffer[..4]);
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (peOffset <= 0 || peOffset + 24 + 2 > stream.Length)
            return null;
        stream.Position = peOffset;
        stream.ReadExactly(buffer);
        if (!buffer[..4].SequenceEqual("PE\0\0"u8))
            return null;

        var optionalHeader = peOffset + 24;
        stream.Position = optionalHeader;
        stream.ReadExactly(buffer[..2]);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var directories = magic switch { 0x10B => optionalHeader + 96, 0x20B => optionalHeader + 112, _ => -1L };
        if (directories < 0)
            return null;

        // Data directory 4: the certificate table (its "RVA" is a plain file offset).
        var entry = directories + (4 * 8);
        if (entry + 8 > stream.Length)
            return false;
        stream.Position = entry;
        stream.ReadExactly(buffer[..8]);
        offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        size = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        return offset > 0 && size > 0;
    }

    private static EmbeddedSignature? DecodeSignedData(ReadOnlyMemory<byte> data)
    {
        var contentInfo = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != SignedDataOid)
            return null;
        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger(); // version
        signedData.ReadSetOf(); // digestAlgorithms
        signedData.ReadSequence(); // encapContentInfo

        var certificates = new List<X509Certificate2>();
        try
        {
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var set = signedData.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0));
                while (set.HasData)
                {
                    var encoded = set.ReadEncodedValue();
                    try
                    {
                        certificates.Add(X509CertificateLoader.LoadCertificate(encoded.Span));
                    }
                    catch (CryptographicException)
                    {
                        // Attribute certificates or other choices: not shown.
                    }
                }
            }

            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
                signedData.ReadEncodedValue(); // CRLs

            var signerInfos = signedData.ReadSetOf(skipSortOrderValidation: true);
            if (!signerInfos.HasData)
                return null;
            var signerInfo = signerInfos.ReadSequence();
            signerInfo.ReadInteger(); // version

            // sid: issuerAndSerialNumber, or [0] subjectKeyIdentifier.
            X509Certificate2? signer = null;
            if (signerInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var sid = signerInfo.ReadSequence();
                sid.ReadEncodedValue(); // issuer name
                var serial = sid.ReadIntegerBytes().ToArray();
                signer = certificates.FirstOrDefault(c => SameSerial(c.SerialNumberBytes.Span, serial));
            }
            else
            {
                var keyId = signerInfo.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
                signer = certificates.FirstOrDefault(c => c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any(e => e.SubjectKeyIdentifierBytes.Span.SequenceEqual(keyId)));
            }

            signer ??= LeafOf(certificates);
            if (signer is null)
                return null;

            var digest = DigestName(signerInfo.ReadSequence().ReadObjectIdentifier());
            DateTime? signingTime = null;
            if (signerInfo.HasData && signerInfo.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                signingTime = SigningTimeFrom(signerInfo.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0)));
            signerInfo.ReadSequence(); // signatureAlgorithm
            signerInfo.ReadOctetString(); // signature

            var nested = 0;
            if (signerInfo.HasData && signerInfo.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                var unsigned = signerInfo.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 1));
                while (unsigned.HasData)
                {
                    var attribute = unsigned.ReadSequence();
                    var oid = attribute.ReadObjectIdentifier();
                    var values = attribute.ReadSetOf(skipSortOrderValidation: true);
                    while (values.HasData)
                    {
                        var value = values.ReadEncodedValue();
                        switch (oid)
                        {
                            case CounterSignatureOid:
                                signingTime ??= CounterSignatureTime(value);
                                break;
                            case TimestampTokenOid:
                                signingTime ??= TimestampTokenTime(value);
                                break;
                            case NestedSignatureOid:
                                nested++;
                                break;
                        }
                    }
                }
            }

            var chain = OrderChain(signer, certificates);
            return new EmbeddedSignature(Summarize(signer), [.. chain.Select(Summarize)], digest, signingTime, nested);
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }
    }

    private static bool SameSerial(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        => new BigInteger(a, isUnsigned: true, isBigEndian: true) == new BigInteger(b, isUnsigned: true, isBigEndian: true);

    /// <summary>The certificate that issued no other certificate of the set.</summary>
    private static X509Certificate2? LeafOf(List<X509Certificate2> certificates)
        => certificates.FirstOrDefault(c => !certificates.Any(o => !ReferenceEquals(o, c) && o.Issuer == c.Subject));

    /// <summary>Signer first, then each issuer found in the blob (the root is often not embedded).</summary>
    private static List<X509Certificate2> OrderChain(X509Certificate2 signer, List<X509Certificate2> certificates)
    {
        var chain = new List<X509Certificate2> { signer };
        var current = signer;
        while (chain.Count < 8 && current.Subject != current.Issuer
               && certificates.FirstOrDefault(c => c.Subject == current.Issuer && !chain.Contains(c)) is { } issuer)
        {
            chain.Add(issuer);
            current = issuer;
        }

        return chain;
    }

    private static CertificateSummary Summarize(X509Certificate2 certificate)
        => new(certificate.Subject, certificate.Issuer, certificate.SerialNumber, certificate.Thumbprint, certificate.NotBefore, certificate.NotAfter);

    private static DateTime? SigningTimeFrom(AsnReader attributes)
    {
        while (attributes.HasData)
        {
            var attribute = attributes.ReadSequence();
            if (attribute.ReadObjectIdentifier() != SigningTimeOid)
                continue;
            var values = attribute.ReadSetOf(skipSortOrderValidation: true);
            var tag = values.PeekTag();
            return tag.HasSameClassAndValue(Asn1Tag.UtcTime) ? values.ReadUtcTime().UtcDateTime : values.ReadGeneralizedTime().UtcDateTime;
        }

        return null;
    }

    /// <summary>A legacy countersignature is a SignerInfo whose signed attributes carry the signing time.</summary>
    private static DateTime? CounterSignatureTime(ReadOnlyMemory<byte> value)
    {
        try
        {
            var info = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            info.ReadInteger();
            info.ReadEncodedValue(); // sid
            info.ReadSequence(); // digest algorithm
            return info.HasData && info.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))
                ? SigningTimeFrom(info.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0)))
                : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>An RFC 3161 token: SignedData whose content is a TSTInfo with the generation time.</summary>
    private static DateTime? TimestampTokenTime(ReadOnlyMemory<byte> value)
    {
        try
        {
            var contentInfo = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            contentInfo.ReadObjectIdentifier();
            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            signedData.ReadInteger();
            signedData.ReadSetOf(skipSortOrderValidation: true);
            var encap = signedData.ReadSequence();
            encap.ReadObjectIdentifier();
            var content = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
            var tstInfo = new AsnReader(content, AsnEncodingRules.BER).ReadSequence();
            tstInfo.ReadInteger(); // version
            tstInfo.ReadObjectIdentifier(); // policy
            tstInfo.ReadSequence(); // messageImprint
            tstInfo.ReadIntegerBytes(); // serialNumber
            return tstInfo.ReadGeneralizedTime().UtcDateTime;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static string DigestName(string oid) => oid switch
    {
        "1.2.840.113549.2.5" => "MD5",
        "1.3.14.3.2.26" => "SHA-1",
        "2.16.840.1.101.3.4.2.1" => "SHA-256",
        "2.16.840.1.101.3.4.2.2" => "SHA-384",
        "2.16.840.1.101.3.4.2.3" => "SHA-512",
        _ => oid,
    };
}
