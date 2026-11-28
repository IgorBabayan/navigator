using System.Runtime.InteropServices;
using Navigator.Core.IO;

namespace Navigator.Plugin.CloudSync;

/// <summary>What a sync client keeps of an item on this device.</summary>
internal enum SyncState
{
    None,

    /// <summary>A placeholder: the content is downloaded when opened (Windows "Available when online", macOS dataless).</summary>
    OnlineOnly,

    /// <summary>Downloaded; may be freed again by the client.</summary>
    Local,

    /// <summary>Pinned: kept on the device ("Always keep on this device").</summary>
    AlwaysAvailable,

    /// <summary>Pinned but not downloaded yet: the client is fetching it.</summary>
    Downloading,
}

/// <summary>
/// Sync state of listed items. On Windows it comes for free with the listing: the Cloud Files placeholder attributes
/// (<c>FILE_ATTRIBUTE_PINNED</c>, <c>UNPINNED</c>, <c>RECALL_ON_OPEN</c>, <c>RECALL_ON_DATA_ACCESS</c>, <c>OFFLINE</c>)
/// are part of every directory entry. On macOS one <c>lstat</c> per item tells File Provider placeholders apart
/// (<c>SF_DATALESS</c>). Elsewhere a file in a sync folder is simply a local copy.
/// </summary>
internal static partial class SyncStates
{
    private const int Offline = 0x1000;
    private const int RecallOnOpen = 0x40000;
    private const int Pinned = 0x80000;
    private const int RecallOnDataAccess = 0x400000;
    private const uint DatalessFlag = 0x40000000; // SF_DATALESS

    public static SyncState Of(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (OperatingSystem.IsWindows())
            return FromWindowsAttributes((int)entry.Attributes);
        if (OperatingSystem.IsMacOS())
            return IsDataless(entry.FullPath) ? SyncState.OnlineOnly : SyncState.Local;
        return SyncState.Local;
    }

    internal static SyncState FromWindowsAttributes(int attributes)
    {
        var placeholder = (attributes & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;
        if ((attributes & Pinned) != 0)
            return placeholder ? SyncState.Downloading : SyncState.AlwaysAvailable;
        return placeholder ? SyncState.OnlineOnly : SyncState.Local;
    }

    // ── macOS: struct stat (64-bit inodes) is 144 bytes on x64 and arm64; st_flags is at offset 116 ──

    private const int StatSize = 144;
    private const int FlagsOffset = 116;

    private static unsafe bool IsDataless(string path)
    {
        var buffer = stackalloc byte[StatSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStatX64(path, buffer) : LStat(path, buffer);
        return result == 0 && (*(uint*)(buffer + FlagsOffset) & DatalessFlag) != 0;
    }

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStat(string path, byte* buffer);

    /// <summary>Intel Macs: the plain symbol is the legacy 32-bit-inode layout.</summary>
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int LStatX64(string path, byte* buffer);
}

/// <summary>Windows helpers.</summary>
internal static partial class Native
{
    /// <summary>"@C:\Program Files\…\FileSyncShell64.dll,-102" → "OneDrive - Personal"; plain text is returned as is.</summary>
    public static unsafe string? ResolveDisplayName(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return null;
        if (resource[0] != '@' || !OperatingSystem.IsWindows())
            return resource;
        const int Capacity = 512;
        var buffer = stackalloc char[Capacity];
        if (LoadIndirectString(resource, buffer, Capacity, 0) != 0)
            return null;
        var text = new string(buffer);
        return text.Length > 0 ? text : null;
    }

    [LibraryImport("shlwapi", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int LoadIndirectString(string source, char* output, int capacity, nint reserved);
}
