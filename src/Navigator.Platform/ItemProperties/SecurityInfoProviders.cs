using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Navigator.Core.Platform;

namespace Navigator.Platform.ItemProperties;

/// <summary>
/// Unix (Linux, macOS): owner, group and mode, plus extended ACL entries when the system has them — getfacl on Linux
/// (when the acl tools are installed), "ls -led" on macOS. The tools only run when the Security tab is opened.
/// </summary>
public sealed class UnixSecurityInfoProvider(IFileOwnershipProvider ownership) : ISecurityInfoProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly string? _getfacl = OperatingSystem.IsLinux() ? ProcessRunner.FindOnPath("getfacl") : null;

    public bool HasAdvancedEditor => false;

    public bool OpenAdvancedEditor(string path) => false;

    public async Task<SecurityInfo> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var (owner, group) = await Task.Run(() => ownership.GetOwnership(path), cancellationToken).ConfigureAwait(false);
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mode = File.GetUnixFileMode(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Shown as unknown.
            }
        }

        var extended = new List<string>();
        if (_getfacl is not null)
        {
            var output = await ProcessRunner.RunForOutputAsync(_getfacl, ["--omit-header", "--absolute-names", "--skip-base", path], Timeout, cancellationToken).ConfigureAwait(false);
            if (output is not null)
                extended.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#')));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var output = await ProcessRunner.RunForOutputAsync("/bin/ls", ["-led", path], Timeout, cancellationToken).ConfigureAwait(false);
            if (output is not null)
            {
                // ACL entries follow the listing line as " 0: group:everyone deny delete".
                extended.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Skip(1)
                    .Where(l => l.Length > 2 && char.IsAsciiDigit(l[0])));
            }
        }

        return new SecurityInfo(owner, group, [], mode, extended);
    }
}

/// <summary>Windows: the item's ACL (owner, primary group, explicit and inherited entries) and Explorer's Security page.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSecurityInfoProvider : ISecurityInfoProvider
{
    private const uint ShopFilePath = 0x2;

    public bool HasAdvancedEditor => true;

    public bool OpenAdvancedEditor(string path) => SHObjectProperties(0, ShopFilePath, path, "Security");

    public Task<SecurityInfo> GetAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Get(path), cancellationToken);

    private static SecurityInfo Get(string path)
    {
        var isDirectory = Directory.Exists(path);
        FileSystemSecurity security;
        try
        {
            const AccessControlSections Sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            security = isDirectory ? new DirectoryInfo(path).GetAccessControl(Sections) : new FileInfo(path).GetAccessControl(Sections);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or PrivilegeNotHeldException)
        {
            return new SecurityInfo(null, null, [], null, []);
        }

        var rules = new List<AccessRuleInfo>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            rules.Add(new AccessRuleInfo(
                Name(rule.IdentityReference),
                rule.AccessControlType == AccessControlType.Deny,
                Rights(rule.FileSystemRights, isDirectory),
                rule.IsInherited,
                isDirectory ? Scope(rule.InheritanceFlags, rule.PropagationFlags) : AccessScope.ThisItem));
        }

        return new SecurityInfo(
            security.GetOwner(typeof(SecurityIdentifier)) is { } owner ? Name(owner) : null,
            security.GetGroup(typeof(SecurityIdentifier)) is { } group ? Name(group) : null,
            rules,
            null,
            []);
    }

    /// <summary>"DOMAIN\user", or the SID when it cannot be resolved (deleted accounts).</summary>
    private static string Name(IdentityReference identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException, or "the trust relationship… failed" for domain accounts offline.
            return identity.Value;
        }
    }

    private static AccessRights Rights(FileSystemRights rights, bool isDirectory)
    {
        // Generic rights (inherit-only entries such as CREATOR OWNER) map onto the standard ones.
        var value = (int)rights;
        if ((value & unchecked((int)0x10000000)) != 0)
            rights |= FileSystemRights.FullControl;
        if ((value & unchecked((int)0x80000000)) != 0)
            rights |= FileSystemRights.Read;
        if ((value & 0x40000000) != 0)
            rights |= FileSystemRights.Write;
        if ((value & 0x20000000) != 0)
            rights |= FileSystemRights.ReadAndExecute;

        if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            return AccessRights.FullControl | AccessRights.Modify | AccessRights.Read | AccessRights.Write | AccessRights.Execute | (isDirectory ? AccessRights.ListFolder : 0);

        var result = AccessRights.None;
        if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
            result |= AccessRights.Modify;
        if ((rights & FileSystemRights.Read) == FileSystemRights.Read)
            result |= AccessRights.Read;
        if ((rights & FileSystemRights.Write) == FileSystemRights.Write)
            result |= AccessRights.Write;
        if ((rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
            result |= AccessRights.Execute | (isDirectory ? AccessRights.ListFolder : 0);
        if (result == AccessRights.None)
            result = AccessRights.Special;
        return result;
    }

    private static AccessScope Scope(InheritanceFlags inheritance, PropagationFlags propagation)
    {
        var containers = (inheritance & InheritanceFlags.ContainerInherit) != 0;
        var objects = (inheritance & InheritanceFlags.ObjectInherit) != 0;
        var inheritOnly = (propagation & PropagationFlags.InheritOnly) != 0;
        return (containers, objects, inheritOnly) switch
        {
            (false, false, _) => AccessScope.ThisFolderOnly,
            (true, true, false) => AccessScope.ThisFolderSubfoldersAndFiles,
            (true, false, false) => AccessScope.ThisFolderAndSubfolders,
            (false, true, false) => AccessScope.ThisFolderAndFiles,
            (true, true, true) => AccessScope.SubfoldersAndFilesOnly,
            (true, false, true) => AccessScope.SubfoldersOnly,
            (false, true, true) => AccessScope.FilesOnly,
        };
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint window, uint type, string objectName, string? page);
}
