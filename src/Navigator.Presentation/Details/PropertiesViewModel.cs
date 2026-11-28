using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Formatting;
using Navigator.Core.Hashing;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;

namespace Navigator.Presentation.Details;

public sealed class HashRowViewModel(HashKind kind, string path) : ObservableObject
{
    private string _value = string.Empty;
    private bool _isComputing;

    public HashKind Kind { get; } = kind;

    public string Name => Kind switch
    {
        HashKind.Md5 => "MD5",
        HashKind.Sha1 => "SHA-1",
        HashKind.Sha384 => "SHA-384",
        HashKind.Sha512 => "SHA-512",
        _ => "SHA-256",
    };

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public bool IsComputing
    {
        get => _isComputing;
        private set => SetProperty(ref _isComputing, value);
    }

    public async Task ComputeAsync(CancellationToken token)
    {
        IsComputing = true;
        try
        {
            Value = await FileHasher.ComputeAsync(path, Kind, cancellationToken: token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Value = ex.Message;
        }
        finally
        {
            IsComputing = false;
        }
    }
}

/// <summary>Platform services behind the Properties window's tabs.</summary>
public sealed record PropertiesServices(
    Localizer Localizer,
    IFileOwnershipProvider Ownership,
    ISecurityInfoProvider Security,
    ISignatureVerifier Signatures,
    IAppCompatibilityStore Compatibility,
    IFolderIconService FolderIcons);

/// <summary>
/// Properties window: General (information, folder statistics, permissions, ownership, checksums) and, where they
/// apply to the item, Security, Digital signatures, Compatibility and Customize (folder icon). Every extra tab loads
/// the first time it is selected, so opening Properties costs no more than before.
/// </summary>
public sealed class PropertiesViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Localizer _localizer;
    private readonly IFileOwnershipProvider _ownership;
    private IReadOnlyList<PropertyRow> _general = [];
    private bool _isCalculating;

    public PropertiesViewModel(IReadOnlyList<string> paths, Localizer localizer, IFileOwnershipProvider ownership)
        : this(paths, new PropertiesServices(localizer, ownership, NoSecurity.Instance, NoSignatures.Instance, NoCompatibility.Instance, NoFolderIcons.Instance), static (_, _) => Task.FromResult<string?>(null))
    {
    }

    /// <param name="pickFile">Shows a file picker (title, accepted extensions); used by the folder icon tab.</param>
    public PropertiesViewModel(IReadOnlyList<string> paths, PropertiesServices services, Func<string, IReadOnlyList<string>, Task<string?>> pickFile)
    {
        ArgumentNullException.ThrowIfNull(services);
        var localizer = services.Localizer;
        Paths = paths;
        _localizer = localizer;
        _ownership = services.Ownership;
        Title = paths.Count == 1 ? Path.GetFileName(PathUtil.TrimTrailingSeparator(paths[0])) : localizer.Format("Status.Selected", paths.Count);
        IsSingleFile = paths.Count == 1 && File.Exists(paths[0]);
        if (IsSingleFile)
        {
            foreach (var kind in new[] { HashKind.Md5, HashKind.Sha1, HashKind.Sha256, HashKind.Sha512 })
                Hashes.Add(new HashRowViewModel(kind, paths[0]));
        }

        ComputeHashCommand = new AsyncRelayCommand<HashRowViewModel>(row => row?.ComputeAsync(_lifetime.Token) ?? Task.CompletedTask);
        var isFolder = paths.Count == 1 && Directory.Exists(paths[0]);
        IconKey = paths.Count > 1 ? "Multiple" : isFolder ? "Folder" : ItemCategories.FromExtension(Path.GetExtension(paths[0]).ToLowerInvariant()).ToString();

        if (paths.Count == 1 && (IsSingleFile || isFolder))
        {
            var path = paths[0];
            Security = new SecurityTabViewModel(path, isFolder, services.Security, localizer, _lifetime.Token);
            if (services.Signatures.CanVerify(path))
                Signatures = new SignaturesTabViewModel(path, services.Signatures, localizer, _lifetime.Token);
            if (IsSingleFile && services.Compatibility.IsSupported(path))
                Compatibility = new CompatibilityTabViewModel(path, services.Compatibility, localizer);
            if (isFolder && services.FolderIcons.IsSupported)
                FolderIcon = new FolderIconTabViewModel(path, services.FolderIcons, localizer, pickFile, _lifetime.Token);
        }
    }

    public SecurityTabViewModel? Security { get; }

    public SignaturesTabViewModel? Signatures { get; }

    public CompatibilityTabViewModel? Compatibility { get; }

    public FolderIconTabViewModel? FolderIcon { get; }

    public bool HasSecurity => Security is not null;

    public bool HasSignatures => Signatures is not null;

    public bool HasCompatibility => Compatibility is not null;

    public bool HasFolderIcon => FolderIcon is not null;

    /// <summary>A tab became visible ("security", "signatures", "compatibility", "customize"): its data is loaded once.</summary>
    public void OnTabSelected(string id)
    {
        _ = id switch
        {
            "security" => Security?.EnsureLoadedAsync() ?? Task.CompletedTask,
            "signatures" => Signatures?.EnsureLoadedAsync() ?? Task.CompletedTask,
            "compatibility" => Compatibility?.EnsureLoadedAsync() ?? Task.CompletedTask,
            "customize" => FolderIcon?.EnsureLoadedAsync() ?? Task.CompletedTask,
            _ => Task.CompletedTask,
        };
    }

    public IReadOnlyList<string> Paths { get; }

    public string Title { get; }

    public string IconKey { get; }

    public bool IsSingleFile { get; }

    public ObservableCollection<HashRowViewModel> Hashes { get; } = [];

    public IAsyncRelayCommand<HashRowViewModel> ComputeHashCommand { get; }

    public IReadOnlyList<PropertyRow> General
    {
        get => _general;
        private set => SetProperty(ref _general, value);
    }

    public bool IsCalculating
    {
        get => _isCalculating;
        private set => SetProperty(ref _isCalculating, value);
    }

    public async Task LoadAsync()
    {
        IsCalculating = true;
        try
        {
            General = await Task.Run(Build, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsCalculating = false;
        }
    }

    private List<PropertyRow> Build()
    {
        var culture = _localizer.Culture;
        var rows = new List<PropertyRow>();
        if (Paths.Count > 1)
        {
            long total = 0;
            int files = 0, folders = 0;
            foreach (var path in Paths)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    var statistics = FolderStatisticsCalculator.Calculate(path, cancellationToken: _lifetime.Token);
                    total += statistics.TotalBytes;
                    files += statistics.FileCount;
                    folders += statistics.FolderCount + 1;
                }
                else if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                    files++;
                }
            }

            rows.Add(new PropertyRow(_localizer["Property.Size"], ByteSize.Format(total, culture)));
            rows.Add(new PropertyRow(_localizer["Property.Contains"], _localizer.Format("Property.ContainsValue", files, folders)));
            rows.Add(new PropertyRow(_localizer["Property.Location"], Path.GetDirectoryName(Paths[0]) ?? string.Empty));
            return rows;
        }

        var single = Paths[0];
        FileSystemInfo info = Directory.Exists(single) ? new DirectoryInfo(single) : new FileInfo(single);
        var isDirectory = info is DirectoryInfo;
        rows.Add(new PropertyRow(_localizer["Property.Name"], info.Name));
        rows.Add(new PropertyRow(_localizer["Property.Location"], Path.GetDirectoryName(info.FullName) ?? info.FullName));

        if (isDirectory)
        {
            var statistics = FolderStatisticsCalculator.Calculate(single, cancellationToken: _lifetime.Token);
            rows.Add(new PropertyRow(_localizer["Property.Size"], ByteSize.Format(statistics.TotalBytes, culture) + (statistics.IsPartial ? " *" : string.Empty)));
            rows.Add(new PropertyRow(_localizer["Property.Contains"], _localizer.Format("Property.ContainsValue", statistics.FileCount, statistics.FolderCount)));
        }
        else
        {
            var length = ((FileInfo)info).Length;
            rows.Add(new PropertyRow(_localizer["Property.Size"], $"{ByteSize.Format(length, culture)} ({length.ToString("N0", culture)} {_localizer["Units.Bytes"]})"));
        }

        rows.Add(new PropertyRow(_localizer["Property.Created"], info.CreationTime.ToString("F", culture)));
        rows.Add(new PropertyRow(_localizer["Property.Modified"], info.LastWriteTime.ToString("F", culture)));
        rows.Add(new PropertyRow(_localizer["Property.Accessed"], info.LastAccessTime.ToString("F", culture)));
        if (info.LinkTarget is { } target)
            rows.Add(new PropertyRow(_localizer["Property.LinkTarget"], target));

        if (OperatingSystem.IsWindows())
        {
            var attributes = new List<string>();
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                attributes.Add(_localizer["Attribute.ReadOnly"]);
            if ((info.Attributes & FileAttributes.Hidden) != 0)
                attributes.Add(_localizer["Attribute.Hidden"]);
            if ((info.Attributes & FileAttributes.System) != 0)
                attributes.Add(_localizer["Attribute.System"]);
            if ((info.Attributes & FileAttributes.Archive) != 0)
                attributes.Add(_localizer["Attribute.Archive"]);
            if ((info.Attributes & FileAttributes.Compressed) != 0)
                attributes.Add(_localizer["Attribute.Compressed"]);
            if ((info.Attributes & FileAttributes.Encrypted) != 0)
                attributes.Add(_localizer["Attribute.Encrypted"]);
            rows.Add(new PropertyRow(_localizer["Property.Attributes"], attributes.Count == 0 ? "—" : string.Join(", ", attributes)));
        }
        else
        {
            rows.Add(new PropertyRow(_localizer["Property.Permissions"], PermissionText.Format(info.UnixFileMode, isDirectory)));
        }

        var (owner, group) = _ownership.GetOwnership(single);
        if (owner is not null)
            rows.Add(new PropertyRow(_localizer["Property.Owner"], owner));
        if (group is not null)
            rows.Add(new PropertyRow(_localizer["Property.Group"], group));
        return rows;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
