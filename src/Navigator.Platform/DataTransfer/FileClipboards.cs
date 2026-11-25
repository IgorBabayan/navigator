using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.IO;
using Navigator.Core.Platform;

namespace Navigator.Platform.DataTransfer;

/// <summary>Linux and macOS: only Navigator's own file list is pasted for now.</summary>
public sealed class UnsupportedFileClipboard : ISystemFileClipboard
{
    public bool HasFiles() => false;

    public Task<ClipboardFileList?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<ClipboardFileList?>(null);

    public Task<IReadOnlyList<string>> PasteStreamedAsync(string destination, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public void ForgetCut()
    {
    }
}

/// <summary>Relative names in a FileGroupDescriptor come from another program (or another machine): only plain, relative names pass.</summary>
internal static class StreamedNames
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>"Folder\sub\file.txt" → ["Folder", "sub", "file.txt"]; null for rooted, empty, "." / ".." or invalid segments.</summary>
    public static string[]? Split(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
            return null;
        var segments = name.Split(Separators, StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.AsSpan().IndexOfAny(InvalidNameChars) >= 0
                || segment.EndsWith(' ') || segment.EndsWith('.'))
                return null;
        }

        return segments;
    }

    // Windows rules on every OS: the names arrive from a Windows clipboard; ':' would also open an alternate data stream.
    private static readonly System.Buffers.SearchValues<char> InvalidNameChars = System.Buffers.SearchValues.Create(
        "\"*:<>?|\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000B\f\r\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F");
}

/// <summary>
/// The Windows clipboard. Real files (Explorer copy/cut) are read as CF_HDROP through the Win32 clipboard, which is
/// fast and works on any thread. Streamed files (Remote Desktop: a file copied in the remote session; zip folders,
/// Outlook attachments) are FileGroupDescriptorW + FileContents, which only the OLE clipboard can deliver item by item,
/// so they are read on a short-lived STA thread and written straight into the destination in 1 MiB chunks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsFileClipboard : ISystemFileClipboard
{
    private const ushort HDropFormat = 15; // CF_HDROP
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint TymedHGlobal = 1;
    private const uint TymedIStream = 4;
    private const uint ContentAspect = 1; // DVASPECT_CONTENT
    private const uint DescriptorHasAttributes = 0x4;  // FD_ATTRIBUTES
    private const uint DescriptorHasWriteTime = 0x20;  // FD_WRITESTIME
    private const uint DescriptorHasSize = 0x40;       // FD_FILESIZE
    private const uint DirectoryAttribute = 0x10;      // FILE_ATTRIBUTE_DIRECTORY
    private const int DescriptorSize = 592;            // sizeof(FILEDESCRIPTORW)
    private const int ChunkSize = 1 << 20;

    private static readonly ushort FileGroupDescriptorFormat = Register("FileGroupDescriptorW");
    private static readonly ushort FileContentsFormat = Register("FileContents");
    private static readonly ushort PreferredDropEffectFormat = Register("Preferred DropEffect");

    /// <summary>Clipboard sequence number when an external cut was read; 0 when the last read was not a cut.</summary>
    private uint _cutSequence;

    public bool HasFiles()
        => IsClipboardFormatAvailable(HDropFormat) != 0
           || (FileGroupDescriptorFormat != 0 && IsClipboardFormatAvailable(FileGroupDescriptorFormat) != 0);

    public Task<ClipboardFileList?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (IsClipboardFormatAvailable(HDropFormat) != 0)
            return Task.Run(ReadDropList, cancellationToken); // the owner may render the data lazily: keep it off the UI thread
        if (FileGroupDescriptorFormat != 0 && IsClipboardFormatAvailable(FileGroupDescriptorFormat) != 0)
            return Task.FromResult<ClipboardFileList?>(new ClipboardFileList([], IsCut: false, IsStreamed: true));
        return Task.FromResult<ClipboardFileList?>(null);
    }

    public void ForgetCut()
    {
        var sequence = _cutSequence;
        _cutSequence = 0;
        if (sequence == 0 || GetClipboardSequenceNumber() != sequence || !TryOpenClipboard())
            return;
        try
        {
            // Explorer does the same after pasting a cut: the moved files are gone, a second paste must not find them.
            if (GetClipboardSequenceNumber() == sequence)
                _ = EmptyClipboard();
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    public Task<IReadOnlyList<string>> PasteStreamedAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var outcome = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                outcome.TrySetResult(PasteStreamed(destination, cancellationToken));
            }
            catch (Exception failure)
            {
                // Boundary of the worker thread: the failure belongs to the awaiting caller.
                outcome.TrySetException(failure);
            }
        })
        {
            IsBackground = true,
            Name = "Navigator clipboard paste",
        };
        worker.SetApartmentState(ApartmentState.STA); // the OLE clipboard needs an OLE-initialized STA
        worker.Start();
        return outcome.Task;
    }

    // ── CF_HDROP ──

    private ClipboardFileList? ReadDropList()
    {
        if (!TryOpenClipboard())
            throw new IOException("The clipboard is in use by another application.");
        try
        {
            var drop = GetClipboardData(HDropFormat);
            if (drop == 0)
                return null;
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            if (count == 0)
                return null;

            var paths = new List<string>((int)Math.Min(count, 4096));
            var buffer = new char[1024];
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFileW(drop, i, null, 0);
                if (length == 0)
                    continue;
                if (length + 1 > buffer.Length)
                    buffer = new char[length + 1];
                fixed (char* text = buffer)
                    length = DragQueryFileW(drop, i, text, (uint)buffer.Length);
                if (length > 0)
                    paths.Add(new string(buffer, 0, (int)length));
            }

            var effect = ReadDropEffect();
            var cut = (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
            _cutSequence = cut ? GetClipboardSequenceNumber() : 0;
            return paths.Count == 0 ? null : new ClipboardFileList(paths, cut);
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    /// <summary>"Preferred DropEffect": DROPEFFECT_MOVE after a cut in Explorer, COPY | LINK after a copy. Needs an open clipboard.</summary>
    private static uint ReadDropEffect()
    {
        if (PreferredDropEffectFormat == 0)
            return DropEffectCopy;
        var handle = GetClipboardData(PreferredDropEffectFormat);
        if (handle == 0 || GlobalSize(handle) < sizeof(uint))
            return DropEffectCopy;
        var data = GlobalLock(handle);
        if (data == 0)
            return DropEffectCopy;
        try
        {
            return *(uint*)data;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static bool TryOpenClipboard()
    {
        // Another program may hold the clipboard for a moment (clipboard managers, the RDP clipboard monitor).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(0) != 0)
                return true;
            Thread.Sleep(15);
        }

        return false;
    }

    // ── FileGroupDescriptorW / FileContents ──

    private readonly record struct Descriptor(int Index, string Name, bool IsDirectory, long? Size, DateTime? Modified);

    private static List<string> PasteStreamed(string destination, CancellationToken cancellationToken)
    {
        var initialized = OleInitialize(0);
        if (initialized < 0)
            throw new IOException($"The clipboard could not be opened (OLE 0x{initialized:X8}).");
        try
        {
            nint data;
            var result = OleGetClipboard(&data);
            if (result < 0 || data == 0)
                throw new IOException($"The clipboard could not be read (0x{result:X8}).");
            try
            {
                return WriteAll(data, ReadDescriptors(data), destination, cancellationToken);
            }
            finally
            {
                Release(data);
            }
        }
        finally
        {
            OleUninitialize();
        }
    }

    private static List<Descriptor> ReadDescriptors(nint data)
    {
        var format = new FormatEtc { Format = FileGroupDescriptorFormat, Aspect = ContentAspect, Index = -1, Tymed = TymedHGlobal };
        StgMedium medium;
        var result = GetData(data, &format, &medium);
        if (result < 0)
            throw new IOException($"The clipboard no longer holds files (0x{result:X8}).");
        try
        {
            if (medium.Tymed != TymedHGlobal || medium.Handle == 0)
                return [];
            var size = (long)GlobalSize(medium.Handle);
            if (size < sizeof(uint))
                return [];
            var block = (byte*)GlobalLock(medium.Handle);
            if (block == null)
                return [];
            try
            {
                var count = Math.Min(*(uint*)block, (ulong)(size - sizeof(uint)) / DescriptorSize);
                var list = new List<Descriptor>((int)count);
                for (var i = 0; i < (int)count; i++)
                {
                    // FILEDESCRIPTORW: flags 0, attributes 36, last write 56, size high 64 / low 68, name 72 (WCHAR[260]).
                    var entry = block + sizeof(uint) + ((long)i * DescriptorSize);
                    var flags = Unsafe.ReadUnaligned<uint>(entry);
                    var attributes = Unsafe.ReadUnaligned<uint>(entry + 36);
                    var written = Unsafe.ReadUnaligned<long>(entry + 56);
                    var length = ((long)Unsafe.ReadUnaligned<uint>(entry + 64) << 32) | Unsafe.ReadUnaligned<uint>(entry + 68);
                    var name = new ReadOnlySpan<char>(entry + 72, 260);
                    var end = name.IndexOf('\0');
                    list.Add(new Descriptor(
                        i,
                        (end < 0 ? name : name[..end]).ToString(),
                        (flags & DescriptorHasAttributes) != 0 && (attributes & DirectoryAttribute) != 0,
                        (flags & DescriptorHasSize) != 0 ? length : null,
                        (flags & DescriptorHasWriteTime) != 0 && written > 0 ? DateTime.FromFileTimeUtc(written) : null));
                }

                return list;
            }
            finally
            {
                _ = GlobalUnlock(medium.Handle);
            }
        }
        finally
        {
            ReleaseStgMedium(&medium);
        }
    }

    private static List<string> WriteAll(nint data, List<Descriptor> items, string destination, CancellationToken cancellationToken)
    {
        // Top-level names are made unique once; children follow their (possibly renamed) parent.
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkSize, pinned: true);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StreamedNames.Split(item.Name) is not { } segments)
                continue; // a hostile or broken name never leaves the destination folder

            if (!roots.TryGetValue(segments[0], out var root))
            {
                var isDirectory = segments.Length > 1 || item.IsDirectory;
                root = PathUtil.GetUniqueName(destination, segments[0], isDirectory, p => File.Exists(p) || Directory.Exists(p));
                roots[segments[0]] = root;
                created.Add(Path.Combine(destination, root));
            }

            segments[0] = root;
            var target = Path.Combine(destination, Path.Combine(segments));
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (segments.Length > 1)
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteFile(data, item, target, buffer, cancellationToken);
        }

        return created;
    }

    private static void WriteFile(nint data, Descriptor item, string target, byte[] buffer, CancellationToken cancellationToken)
    {
        var format = new FormatEtc { Format = FileContentsFormat, Aspect = ContentAspect, Index = item.Index, Tymed = TymedIStream | TymedHGlobal };
        StgMedium medium;
        var result = GetData(data, &format, &medium);
        if (result < 0)
            throw new IOException($"'{item.Name}' could not be read from the clipboard (0x{result:X8}).");
        try
        {
            try
            {
                using (var output = new FileStream(target, new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew, // the name is unique: never overwrite
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           BufferSize = 0,           // writes are 1 MiB chunks already
                           PreallocationSize = item.Size is > 0 ? item.Size.Value : 0,
                       }))
                {
                    if (medium.Tymed == TymedIStream)
                        CopyStream(medium.Handle, output, buffer, cancellationToken);
                    else if (medium.Tymed == TymedHGlobal)
                        CopyGlobal(medium.Handle, output, item.Size, cancellationToken);
                    else
                        throw new IOException($"'{item.Name}' is offered in a format Navigator cannot read.");
                }

                if (item.Modified is { } modified)
                    File.SetLastWriteTimeUtc(target, modified);
            }
            catch
            {
                TryDelete(target); // no half-written file is left behind (cancel, lost RDP connection)
                throw;
            }
        }
        finally
        {
            ReleaseStgMedium(&medium);
        }
    }

    private static void CopyStream(nint stream, FileStream output, byte[] buffer, CancellationToken cancellationToken)
    {
        var vtable = *(nint**)stream;
        var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)vtable[5];
        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtable[3];
        _ = seek(stream, 0, 0, null); // STREAM_SEEK_SET; some sources hand out a stream that was read before

        fixed (byte* chunk = buffer)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint count = 0;
                var result = read(stream, chunk, (uint)buffer.Length, &count);
                if (result < 0)
                    throw new IOException($"Reading from the clipboard failed (0x{result:X8}).");
                if (count == 0)
                    return;
                output.Write(buffer, 0, (int)count);
            }
        }
    }

    private static void CopyGlobal(nint handle, FileStream output, long? declaredSize, CancellationToken cancellationToken)
    {
        // GlobalSize may be rounded up; the descriptor's size is exact when present.
        var size = (long)GlobalSize(handle);
        if (declaredSize is { } exact && exact < size)
            size = exact;
        var block = (byte*)GlobalLock(handle);
        if (block == null)
            return;
        try
        {
            for (long offset = 0; offset < size; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(new ReadOnlySpan<byte>(block + offset, (int)Math.Min(ChunkSize, size - offset)));
            }
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure is what the user needs to see.
        }
    }

    private static int GetData(nint data, FormatEtc* format, StgMedium* medium)
        => ((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int>)(*(nint**)data)[3])(data, format, medium);

    private static void Release(nint unknown)
        => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    private static ushort Register(string name)
    {
        var id = RegisterClipboardFormatW(name);
        return id is >= 0xC000 and <= 0xFFFF ? (ushort)id : (ushort)0;
    }

    /// <summary>FORMATETC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort Format;
        public nint TargetDevice;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    /// <summary>STGMEDIUM.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        public uint Tymed;
        public nint Handle;
        public nint UnknownForRelease;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll")]
    private static partial int IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial int OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    private static partial int CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial int EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint drop, uint index, char* file, uint capacity);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial int GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int OleGetClipboard(nint* data);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(StgMedium* medium);
}
