using System.Buffers;
using System.Security.Cryptography;

namespace Navigator.Core.Hashing;

public enum HashKind
{
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
}

/// <summary>Streaming file checksums with progress and cancellation (Properties → Hashes, updater verification).</summary>
public static class FileHasher
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeAsync(string path, HashKind kind, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
        return await ComputeAsync(stream, kind, progress, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeAsync(Stream stream, HashKind kind, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(ToAlgorithm(kind));
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Constant-time comparison of two hex digests (case-insensitive).</summary>
    public static bool DigestsEqual(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
            return false;

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static HashAlgorithmName ToAlgorithm(HashKind kind) => kind switch
    {
        HashKind.Md5 => HashAlgorithmName.MD5,
        HashKind.Sha1 => HashAlgorithmName.SHA1,
        HashKind.Sha384 => HashAlgorithmName.SHA384,
        HashKind.Sha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };
}
