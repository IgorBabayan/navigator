using Navigator.Core.Customization;
using Navigator.Core.Signatures;

namespace Navigator.Core.Platform;

/// <summary>What the space bar opens (Settings → Files and folders → Quick Look).</summary>
public enum QuickLookProvider
{
    /// <summary>A running external previewer if there is one (QuickLook, Seer, PowerToys Peek; Quick Look on macOS), else Navigator's own.</summary>
    Automatic,

    /// <summary>Navigator's own preview window.</summary>
    BuiltIn,

    /// <summary>PowerToys Peek (Windows).</summary>
    PowerToysPeek,

    /// <summary>QuickLook by QL-Win (Windows).</summary>
    QuickLook,

    /// <summary>Seer (Windows).</summary>
    Seer,

    /// <summary>The operating system's previewer: Quick Look on macOS, GNOME Sushi on Linux.</summary>
    System,
}

/// <summary>Hands the selected file to an external previewer.</summary>
public interface IQuickLookService
{
    /// <summary>External previewers that can be used on this machine (installed or running). Cheap; cached briefly.</summary>
    IReadOnlyList<QuickLookProvider> GetAvailableProviders();

    /// <summary>The external previewer <see cref="QuickLookProvider.Automatic"/> stands for right now; null means the built-in one.</summary>
    QuickLookProvider? ResolveAutomatic();

    /// <summary>Opens the preview of <paramref name="path"/>, or closes it when the provider toggles. False when the provider is not available.</summary>
    Task<bool> ToggleAsync(QuickLookProvider provider, string path, CancellationToken cancellationToken = default);

    /// <summary>Shows another file in an open external preview (selection moved); false where the provider cannot.</summary>
    Task<bool> SwitchAsync(QuickLookProvider provider, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// A thumbnail made by the platform: either an encoded PNG/JPEG (<see cref="IsEncoded"/>) or 32-bit BGRA pixels with
/// straight alpha, rows packed (stride = Width * 4).
/// </summary>
public sealed record ThumbnailImage(int Width, int Height, byte[] Data, bool IsEncoded);

/// <summary>
/// Thumbnails of files the UI toolkit cannot decode itself: videos, PDFs, Office documents… (the Windows shell's
/// thumbnail handlers, macOS Quick Look, poppler/ffmpeg on Linux). Implementations bound their own concurrency, so
/// callers may request freely; results should be cached by the caller.
/// </summary>
public interface IPlatformThumbnailer
{
    bool IsSupported { get; }

    /// <summary>Whether files of this type can get a thumbnail here. Decided from the extension only (no I/O).</summary>
    bool CanThumbnail(string extension);

    /// <summary>A thumbnail whose longer side is about <paramref name="pixelSize"/>; null when none could be made.</summary>
    Task<ThumbnailImage?> GetThumbnailAsync(string path, int pixelSize, CancellationToken cancellationToken = default);
}

/// <summary>Plays an audio file for the preview pane (one at a time) through the operating system.</summary>
public interface IAudioPreviewPlayer
{
    bool IsSupported { get; }

    /// <summary>True while a file started by <see cref="Play"/> is still playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Stops whatever plays and starts <paramref name="path"/>; false when it could not be started.</summary>
    bool Play(string path);

    void Stop();
}

public enum SignatureStatus
{
    /// <summary>Signed, and the platform trusts the signature.</summary>
    Valid,

    /// <summary>No signature was found.</summary>
    NotSigned,

    /// <summary>The file was changed after it was signed, or the signature is malformed.</summary>
    Invalid,

    /// <summary>Signed with a certificate the platform does not trust (unknown root, explicitly distrusted, revoked).</summary>
    Untrusted,

    /// <summary>The signing certificate expired and the signature carries no timestamp.</summary>
    Expired,

    /// <summary>Signed, but this platform cannot tell whether the signature is trusted.</summary>
    NotVerified,
}

public enum SignatureSource
{
    /// <summary>A signature inside the file (Authenticode).</summary>
    Embedded,

    /// <summary>A Windows security catalog that lists the file's hash (most system files).</summary>
    Catalog,

    /// <summary>An Apple code signature.</summary>
    CodeSign,
}

public sealed record SignatureVerification(
    SignatureStatus Status,
    SignatureSource Source,
    EmbeddedSignature? Signature,
    string? CatalogPath = null,
    IReadOnlyList<KeyValuePair<string, string>>? Details = null);

/// <summary>Digital signatures of programs: WinVerifyTrust on Windows, codesign on macOS, decoding only elsewhere.</summary>
public interface ISignatureVerifier
{
    /// <summary>Whether a Signatures tab makes sense for the item. Decided from the name only.</summary>
    bool CanVerify(string path);

    Task<SignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default);
}

[Flags]
public enum AccessRights
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
    ListFolder = 8,
    Modify = 16,
    FullControl = 32,
    Special = 64,
}

/// <summary>Where an access rule of a folder applies (Windows inheritance flags).</summary>
public enum AccessScope
{
    ThisItem,
    ThisFolderOnly,
    ThisFolderSubfoldersAndFiles,
    ThisFolderAndSubfolders,
    ThisFolderAndFiles,
    SubfoldersAndFilesOnly,
    SubfoldersOnly,
    FilesOnly,
}

public sealed record AccessRuleInfo(string Identity, bool IsDeny, AccessRights Rights, bool IsInherited, AccessScope Scope);

/// <summary>
/// Ownership and permissions of an item: the Windows ACL, or the Unix mode plus any extended ACL entries
/// (getfacl on Linux, "ls -le" on macOS).
/// </summary>
public sealed record SecurityInfo(
    string? Owner,
    string? Group,
    IReadOnlyList<AccessRuleInfo> Rules,
    UnixFileMode? Mode,
    IReadOnlyList<string> ExtendedEntries);

public interface ISecurityInfoProvider
{
    Task<SecurityInfo> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>True where the OS has its own permissions editor (Windows "Security" page).</summary>
    bool HasAdvancedEditor { get; }

    bool OpenAdvancedEditor(string path);
}

/// <summary>Per-program compatibility layers (Windows only).</summary>
public interface IAppCompatibilityStore
{
    /// <summary>Whether the item is a program whose compatibility options can be set here (name only).</summary>
    bool IsSupported(string path);

    CompatibilitySettings Get(string path);

    /// <summary>Saves the settings for the current user; false when the registry could not be written.</summary>
    bool Set(string path, CompatibilitySettings settings);
}

/// <summary>
/// Custom folder icons as the platform's own file manager shows them: desktop.ini on Windows, GIO metadata (GNOME and
/// GTK file managers) or .directory (KDE) on Linux, NSWorkspace on macOS.
/// </summary>
public interface IFolderIconService
{
    bool IsSupported { get; }

    /// <summary>File types accepted as icons (".ico", ".exe", ".png"…).</summary>
    IReadOnlyList<string> IconExtensions { get; }

    /// <summary>Whether an icon inside a resource file can be chosen by index (Windows .exe/.dll).</summary>
    bool SupportsIconIndex { get; }

    /// <summary>The icon file currently set (with ",index" on Windows); null when the folder has the default icon.</summary>
    Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default);

    Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default);

    Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default);
}
