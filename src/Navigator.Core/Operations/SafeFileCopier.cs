using System.Buffers;

namespace Navigator.Core.Operations;

/// <summary>
/// Copies one file without ever exposing a partially written destination:
/// data is written to a hidden temporary file in the destination folder, metadata is applied,
/// and only then is the temporary file renamed onto the final name (an atomic rename on the same volume).
/// On failure or cancellation the temporary file is removed and the destination is untouched.
/// </summary>
internal static class SafeFileCopier
{
    public const string TemporaryMarker = ".navigator-partial";

    public static async ValueTask CopyAsync(string source, string destination, bool overwrite, bool flushToDisk, OperationContext context)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new IOException($"Invalid destination '{destination}'.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid().ToString("N")[..8]}{TemporaryMarker}");
        var bufferSize = Math.Clamp(context.Options.CopyBufferSize, 64 * 1024, 16 * 1024 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long reported = 0;
        var committed = false;

        try
        {
            var sourceInfo = new FileInfo(source);
            await using (var input = new FileStream(source, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            }))
            {
                var length = input.Length;
                await using var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                    BufferSize = 0,
                    // Reserves space up front: "disk full" surfaces before any data is written.
                    PreallocationSize = length,
                });

                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), context.CancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), context.CancellationToken).ConfigureAwait(false);
                    context.ReportBytes(read);
                    reported += read;
                    await context.CheckpointAsync().ConfigureAwait(false);
                }

                if (output.Length != length)
                    throw new OperationStepException(FileOperationErrorKind.VerificationFailed, $"Copied size of '{source}' does not match.");

                if (flushToDisk)
                    output.Flush(flushToDisk: true);
            }

            CopyMetadata(sourceInfo, temporary);

            if (overwrite)
                PrepareForReplace(destination);

            File.Move(temporary, destination, overwrite);
            committed = true;
        }
        catch
        {
            // Progress must not count bytes that were rolled back (a retry adds them again).
            context.ReportBytes(-reported);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!committed)
                TryDelete(temporary);
        }
    }

    private static void CopyMetadata(FileInfo source, string target)
    {
        try
        {
            File.SetCreationTimeUtc(target, source.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not every file system stores creation time; this is not a reason to fail the copy.
        }

        File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);

        if (OperatingSystem.IsWindows())
        {
            var attributes = source.Attributes & ~(FileAttributes.Hidden | FileAttributes.ReadOnly);
            if ((source.Attributes & FileAttributes.Hidden) != 0)
                attributes |= FileAttributes.Hidden;
            File.SetAttributes(target, attributes == 0 ? FileAttributes.Normal : attributes);
            // ReadOnly is applied after the rename; a read-only file can still be renamed on Windows.
            if ((source.Attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
        else
        {
            try
            {
                File.SetUnixFileMode(target, source.UnixFileMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // e.g. FAT/exFAT volumes have no Unix permissions.
            }
        }
    }

    /// <summary>The user chose to replace: a read-only destination must not block the atomic replace.</summary>
    internal static void PrepareForReplace(string destination)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(destination))
            return;

        var attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                    File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover hidden .navigator-partial file is cleaned up by the next copy attempt.
        }
    }
}
