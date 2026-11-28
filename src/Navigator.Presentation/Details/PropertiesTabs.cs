using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Customization;
using Navigator.Core.Platform;
using Navigator.Core.Signatures;
using Navigator.Presentation.Localization;
using Navigator.Presentation.SettingsPages;

namespace Navigator.Presentation.Details;

/// <summary>A tab whose data is read the first time it is shown.</summary>
public abstract class LazyTabViewModel : ObservableObject
{
    private Task? _loading;
    private bool _isLoading;
    private string? _message;

    public bool IsLoading
    {
        get => _isLoading;
        protected set => SetProperty(ref _isLoading, value);
    }

    /// <summary>An error or confirmation shown under the tab's content.</summary>
    public string? Message
    {
        get => _message;
        protected set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public Task EnsureLoadedAsync() => _loading ??= RunLoadAsync();

    private async Task RunLoadAsync()
    {
        IsLoading = true;
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected abstract Task LoadAsync();
}

public sealed record AccessRuleRow(string Identity, string Access, string Permissions, string AppliesTo, string InheritedText, bool IsDeny);

/// <summary>
/// Security tab: owner, group and access rules (Windows ACL, with Explorer's own editor one click away), or on Unix
/// the mode as nine check boxes that can be applied (chmod), plus any extended ACL entries.
/// </summary>
public sealed class SecurityTabViewModel : LazyTabViewModel
{
    private readonly string _path;
    private readonly bool _isFolder;
    private readonly ISecurityInfoProvider _provider;
    private readonly Localizer _localizer;
    private readonly CancellationToken _lifetime;
    private string? _owner;
    private string? _group;
    private IReadOnlyList<AccessRuleRow> _rules = [];
    private IReadOnlyList<string> _extended = [];
    private UnixFileMode? _mode;
    private UnixFileMode _editedMode;

    public SecurityTabViewModel(string path, bool isFolder, ISecurityInfoProvider provider, Localizer localizer, CancellationToken lifetime)
    {
        _path = path;
        _isFolder = isFolder;
        _provider = provider;
        _localizer = localizer;
        _lifetime = lifetime;
        ApplyModeCommand = new AsyncRelayCommand(ApplyModeAsync, () => _mode is { } mode && mode != _editedMode);
        OpenAdvancedCommand = new RelayCommand(() => _provider.OpenAdvancedEditor(_path), () => _provider.HasAdvancedEditor);
    }

    public string Owner => _owner ?? "—";

    public string Group => _group ?? "—";

    public bool HasGroup => _group is not null;

    public IReadOnlyList<AccessRuleRow> Rules
    {
        get => _rules;
        private set
        {
            if (SetProperty(ref _rules, value))
                OnPropertyChanged(nameof(HasRules));
        }
    }

    public bool HasRules => _rules.Count > 0;

    public IReadOnlyList<string> ExtendedEntries
    {
        get => _extended;
        private set
        {
            if (SetProperty(ref _extended, value))
                OnPropertyChanged(nameof(HasExtendedEntries));
        }
    }

    public bool HasExtendedEntries => _extended.Count > 0;

    public bool HasMode => _mode is not null;

    public bool HasAdvancedEditor => _provider.HasAdvancedEditor;

    /// <summary>"rwxr-xr-x (755)" of the edited mode.</summary>
    public string ModeText => HasMode ? $"{PermissionText.Format(_editedMode, _isFolder)[1..]} ({Octal(_editedMode)})" : string.Empty;

    public bool OwnerRead { get => Has(UnixFileMode.UserRead); set => Toggle(UnixFileMode.UserRead, value); }

    public bool OwnerWrite { get => Has(UnixFileMode.UserWrite); set => Toggle(UnixFileMode.UserWrite, value); }

    public bool OwnerExecute { get => Has(UnixFileMode.UserExecute); set => Toggle(UnixFileMode.UserExecute, value); }

    public bool GroupRead { get => Has(UnixFileMode.GroupRead); set => Toggle(UnixFileMode.GroupRead, value); }

    public bool GroupWrite { get => Has(UnixFileMode.GroupWrite); set => Toggle(UnixFileMode.GroupWrite, value); }

    public bool GroupExecute { get => Has(UnixFileMode.GroupExecute); set => Toggle(UnixFileMode.GroupExecute, value); }

    public bool OthersRead { get => Has(UnixFileMode.OtherRead); set => Toggle(UnixFileMode.OtherRead, value); }

    public bool OthersWrite { get => Has(UnixFileMode.OtherWrite); set => Toggle(UnixFileMode.OtherWrite, value); }

    public bool OthersExecute { get => Has(UnixFileMode.OtherExecute); set => Toggle(UnixFileMode.OtherExecute, value); }

    /// <summary>Label of the execute column: "Execute" for files, "List / enter" for folders.</summary>
    public string ExecuteLabel => _localizer[_isFolder ? "Security.Traverse" : "Security.Execute"];

    public IAsyncRelayCommand ApplyModeCommand { get; }

    public IRelayCommand OpenAdvancedCommand { get; }

    protected override async Task LoadAsync()
    {
        var info = await _provider.GetAsync(_path, _lifetime).ConfigureAwait(true);
        _owner = info.Owner;
        _group = info.Group;
        _mode = info.Mode;
        _editedMode = info.Mode ?? 0;
        Rules = [.. info.Rules.Select(ToRow)];
        ExtendedEntries = info.ExtendedEntries;
        OnPropertyChanged(string.Empty);
        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    private AccessRuleRow ToRow(AccessRuleInfo rule)
        => new(
            rule.Identity,
            _localizer[rule.IsDeny ? "Security.Deny" : "Security.Allow"],
            RightsText(rule.Rights),
            _localizer["Security.Scope." + rule.Scope],
            rule.IsInherited ? _localizer["Security.Inherited"] : _localizer["Security.Explicit"],
            rule.IsDeny);

    private string RightsText(AccessRights rights)
    {
        if ((rights & AccessRights.FullControl) != 0)
            return _localizer["Security.Right.FullControl"];
        var names = new List<string>();
        foreach (var right in new[] { AccessRights.Modify, AccessRights.Read, AccessRights.Write, AccessRights.Execute, AccessRights.ListFolder, AccessRights.Special })
        {
            if ((rights & right) != 0)
                names.Add(_localizer["Security.Right." + right]);
        }

        return string.Join(", ", names);
    }

    private bool Has(UnixFileMode flag) => (_editedMode & flag) != 0;

    private void Toggle(UnixFileMode flag, bool value)
    {
        var mode = value ? _editedMode | flag : _editedMode & ~flag;
        if (mode == _editedMode)
            return;
        _editedMode = mode;
        OnPropertyChanged(string.Empty);
        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    private async Task ApplyModeAsync()
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = _editedMode;
        try
        {
            await Task.Run(
                () =>
                {
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(_path, mode);
                },
                _lifetime).ConfigureAwait(true);
            _mode = mode;
            Message = _localizer["Security.Applied"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }

        ApplyModeCommand.NotifyCanExecuteChanged();
    }

    private static string Octal(UnixFileMode mode)
        => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');
}

public sealed record CertificateRow(string Name, string Issuer, string Validity);

/// <summary>Digital signatures tab: whether the file is signed, by whom, when, and whether the platform trusts it.</summary>
public sealed class SignaturesTabViewModel : LazyTabViewModel
{
    private readonly string _path;
    private readonly ISignatureVerifier _verifier;
    private readonly Localizer _localizer;
    private readonly CancellationToken _lifetime;
    private SignatureVerification? _result;

    public SignaturesTabViewModel(string path, ISignatureVerifier verifier, Localizer localizer, CancellationToken lifetime)
    {
        _path = path;
        _verifier = verifier;
        _localizer = localizer;
        _lifetime = lifetime;
    }

    public bool IsLoaded => _result is not null;

    public string StatusText => _result is null ? string.Empty : _localizer["Signature.Status." + _result.Status];

    public string StatusDescription => _result is null ? string.Empty : _localizer["Signature.Description." + _result.Status];

    /// <summary>"Success", "Warning" or "Error" (the status icon's tone).</summary>
    public string Tone => _result?.Status switch
    {
        SignatureStatus.Valid => "Success",
        SignatureStatus.NotVerified or SignatureStatus.NotSigned => "Warning",
        null => "Warning",
        _ => "Error",
    };

    public string StatusIconKey => Tone switch { "Success" => "ShieldCheck", "Error" => "ShieldAlert", _ => "ShieldOutline" };

    public bool HasSigner => _result?.Signature is not null;

    public string Signer => _result?.Signature?.Signer.DisplayName ?? string.Empty;

    public string Issuer => _result?.Signature?.Signer.IssuerDisplayName ?? string.Empty;

    public string SerialNumber => _result?.Signature?.Signer.SerialNumber ?? string.Empty;

    public string Thumbprint => _result?.Signature?.Signer.Thumbprint ?? string.Empty;

    public string Validity => _result?.Signature?.Signer is { } signer ? Period(signer) : string.Empty;

    public string DigestAlgorithm => _result?.Signature?.DigestAlgorithm ?? string.Empty;

    public string SigningTime => _result?.Signature?.SigningTime is { } time ? time.ToLocalTime().ToString("F", _localizer.Culture) : _localizer["Signature.NoTimestamp"];

    public string Source => _result is null ? string.Empty
        : _result.Source == SignatureSource.Catalog ? _localizer.Format("Signature.Source.Catalog", Path.GetFileName(_result.CatalogPath ?? string.Empty))
        : _localizer["Signature.Source." + _result.Source];

    public bool HasNestedSignatures => _result?.Signature?.NestedSignatures > 0;

    public string NestedSignaturesText => _localizer.Format("Signature.Nested", _result?.Signature?.NestedSignatures ?? 0);

    public IReadOnlyList<CertificateRow> Chain => _result?.Signature?.Chain.Select(c => new CertificateRow(c.DisplayName, c.IssuerDisplayName, Period(c))).ToList() ?? [];

    public bool HasChain => Chain.Count > 1;

    public IReadOnlyList<PropertyRow> Details => _result?.Details?.Select(d => new PropertyRow(d.Key, d.Value)).ToList() ?? [];

    public bool HasDetails => _result?.Details is { Count: > 0 };

    protected override async Task LoadAsync()
    {
        _result = await _verifier.VerifyAsync(_path, _lifetime).ConfigureAwait(true);
        OnPropertyChanged(string.Empty);
    }

    private string Period(CertificateSummary certificate)
        => $"{certificate.NotBefore.ToString("d", _localizer.Culture)} – {certificate.NotAfter.ToString("d", _localizer.Culture)}";
}

/// <summary>Compatibility tab (Windows programs): the AppCompat layers Explorer's own tab offers, saved per user.</summary>
public sealed class CompatibilityTabViewModel : LazyTabViewModel
{
    private readonly string _path;
    private readonly IAppCompatibilityStore _store;
    private readonly Localizer _localizer;
    private CompatibilitySettings _saved = CompatibilitySettings.Default;
    private CompatibilitySettings _edited = CompatibilitySettings.Default;
    private CompatibilityMode _lastMode = CompatibilityMode.Windows8;
    private ReducedColorMode _lastColor = ReducedColorMode.Colors8Bit;
    private DpiScalingOverride _lastDpi = DpiScalingOverride.Application;

    public CompatibilityTabViewModel(string path, IAppCompatibilityStore store, Localizer localizer)
    {
        _path = path;
        _store = store;
        _localizer = localizer;
        Modes = [.. Enum.GetValues<CompatibilityMode>().Where(m => m != CompatibilityMode.None).Select(m => new Option<CompatibilityMode>(m, localizer["Compatibility.Mode." + m]))];
        ColorModes = [new(ReducedColorMode.Colors8Bit, localizer["Compatibility.Color8"]), new(ReducedColorMode.Colors16Bit, localizer["Compatibility.Color16"])];
        DpiOptions = [.. Enum.GetValues<DpiScalingOverride>().Where(d => d != DpiScalingOverride.None).Select(d => new Option<DpiScalingOverride>(d, localizer["Compatibility.Dpi." + d]))];
        ApplyCommand = new RelayCommand(Apply, () => _edited != _saved);
        ResetCommand = new RelayCommand(() => Edit(CompatibilitySettings.Default), () => !_edited.IsDefault);
    }

    public IReadOnlyList<Option<CompatibilityMode>> Modes { get; }

    public IReadOnlyList<Option<ReducedColorMode>> ColorModes { get; }

    public IReadOnlyList<Option<DpiScalingOverride>> DpiOptions { get; }

    public bool UseCompatibilityMode
    {
        get => _edited.Mode != CompatibilityMode.None;
        set => Edit(_edited with { Mode = value ? _lastMode : CompatibilityMode.None });
    }

    public Option<CompatibilityMode> Mode
    {
        get => Modes.FirstOrDefault(m => m.Value == (_edited.Mode == CompatibilityMode.None ? _lastMode : _edited.Mode)) ?? Modes[^1];
        set
        {
            if (value is null)
                return;
            _lastMode = value.Value;
            if (UseCompatibilityMode)
                Edit(_edited with { Mode = value.Value });
        }
    }

    public bool UseReducedColor
    {
        get => _edited.ReducedColor != ReducedColorMode.None;
        set => Edit(_edited with { ReducedColor = value ? _lastColor : ReducedColorMode.None });
    }

    public Option<ReducedColorMode> ColorMode
    {
        get => ColorModes.FirstOrDefault(c => c.Value == (_edited.ReducedColor == ReducedColorMode.None ? _lastColor : _edited.ReducedColor)) ?? ColorModes[0];
        set
        {
            if (value is null)
                return;
            _lastColor = value.Value;
            if (UseReducedColor)
                Edit(_edited with { ReducedColor = value.Value });
        }
    }

    public bool Resolution640x480
    {
        get => _edited.Resolution640x480;
        set => Edit(_edited with { Resolution640x480 = value });
    }

    public bool DisableFullscreenOptimizations
    {
        get => _edited.DisableFullscreenOptimizations;
        set => Edit(_edited with { DisableFullscreenOptimizations = value });
    }

    public bool RunAsAdministrator
    {
        get => _edited.RunAsAdministrator;
        set => Edit(_edited with { RunAsAdministrator = value });
    }

    public bool OverrideDpi
    {
        get => _edited.DpiOverride != DpiScalingOverride.None;
        set => Edit(_edited with { DpiOverride = value ? _lastDpi : DpiScalingOverride.None });
    }

    public Option<DpiScalingOverride> Dpi
    {
        get => DpiOptions.FirstOrDefault(d => d.Value == (_edited.DpiOverride == DpiScalingOverride.None ? _lastDpi : _edited.DpiOverride)) ?? DpiOptions[0];
        set
        {
            if (value is null)
                return;
            _lastDpi = value.Value;
            if (OverrideDpi)
                Edit(_edited with { DpiOverride = value.Value });
        }
    }

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand ResetCommand { get; }

    protected override async Task LoadAsync()
    {
        _saved = await Task.Run(() => _store.Get(_path)).ConfigureAwait(true);
        if (_saved.Mode != CompatibilityMode.None)
            _lastMode = _saved.Mode;
        if (_saved.ReducedColor != ReducedColorMode.None)
            _lastColor = _saved.ReducedColor;
        if (_saved.DpiOverride != DpiScalingOverride.None)
            _lastDpi = _saved.DpiOverride;
        Edit(_saved);
    }

    private void Edit(CompatibilitySettings settings)
    {
        _edited = settings;
        Message = null;
        OnPropertyChanged(string.Empty);
        ApplyCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    private void Apply()
    {
        if (_store.Set(_path, _edited))
        {
            _saved = _edited;
            Message = _localizer["Compatibility.Saved"];
        }
        else
        {
            Message = _localizer["Compatibility.SaveFailed"];
        }

        ApplyCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Customize tab of folders: the folder's icon as the system's file manager shows it.</summary>
public sealed class FolderIconTabViewModel : LazyTabViewModel
{
    private readonly string _folder;
    private readonly IFolderIconService _service;
    private readonly Localizer _localizer;
    private readonly Func<string, IReadOnlyList<string>, Task<string?>> _pickFile;
    private readonly CancellationToken _lifetime;
    private string? _current;
    private string _iconIndex = "0";

    public FolderIconTabViewModel(string folder, IFolderIconService service, Localizer localizer, Func<string, IReadOnlyList<string>, Task<string?>> pickFile, CancellationToken lifetime)
    {
        _folder = folder;
        _service = service;
        _localizer = localizer;
        _pickFile = pickFile;
        _lifetime = lifetime;
        ChangeIconCommand = new AsyncRelayCommand(ChangeAsync);
        RestoreDefaultCommand = new AsyncRelayCommand(RestoreAsync, () => _current is not null);
    }

    /// <summary>The current icon ("C:\Icons\a.ico,0"), or "Default".</summary>
    public string CurrentIcon => _current switch
    {
        null => _localizer["FolderIcon.Default"],
        "" => _localizer["FolderIcon.Custom"],
        var icon => icon,
    };

    public bool SupportsIconIndex => _service.SupportsIconIndex;

    /// <summary>Index of the icon inside an .exe/.dll/.icl resource file (Windows).</summary>
    public string IconIndex
    {
        get => _iconIndex;
        set => SetProperty(ref _iconIndex, value);
    }

    public string Hint => _localizer.Format("FolderIcon.Hint", string.Join(", ", _service.IconExtensions));

    public IAsyncRelayCommand ChangeIconCommand { get; }

    public IAsyncRelayCommand RestoreDefaultCommand { get; }

    protected override async Task LoadAsync()
    {
        _current = await _service.GetCustomIconAsync(_folder, _lifetime).ConfigureAwait(true);
        OnPropertyChanged(nameof(CurrentIcon));
        RestoreDefaultCommand.NotifyCanExecuteChanged();
    }

    private async Task ChangeAsync()
    {
        var icon = await _pickFile(_localizer["FolderIcon.Pick"], _service.IconExtensions).ConfigureAwait(true);
        if (icon is null)
            return;
        var index = _service.SupportsIconIndex && int.TryParse(_iconIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;
        if (await _service.SetIconAsync(_folder, icon, index, _lifetime).ConfigureAwait(true))
        {
            Message = _localizer["FolderIcon.Changed"];
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            Message = _localizer["FolderIcon.Failed"];
        }
    }

    private async Task RestoreAsync()
    {
        if (await _service.ResetAsync(_folder, _lifetime).ConfigureAwait(true))
        {
            Message = _localizer["FolderIcon.Restored"];
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            Message = _localizer["FolderIcon.Failed"];
        }
    }
}

// Null services for callers (and tests) that only need the General tab.
internal sealed class NoSecurity : ISecurityInfoProvider
{
    public static NoSecurity Instance { get; } = new();

    public bool HasAdvancedEditor => false;

    public Task<SecurityInfo> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new SecurityInfo(null, null, [], null, []));

    public bool OpenAdvancedEditor(string path) => false;
}

internal sealed class NoSignatures : ISignatureVerifier
{
    public static NoSignatures Instance { get; } = new();

    public bool CanVerify(string path) => false;

    public Task<SignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new SignatureVerification(SignatureStatus.NotVerified, SignatureSource.Embedded, null));
}

internal sealed class NoCompatibility : IAppCompatibilityStore
{
    public static NoCompatibility Instance { get; } = new();

    public bool IsSupported(string path) => false;

    public CompatibilitySettings Get(string path) => CompatibilitySettings.Default;

    public bool Set(string path, CompatibilitySettings settings) => false;
}

internal sealed class NoFolderIcons : IFolderIconService
{
    public static NoFolderIcons Instance { get; } = new();

    public bool IsSupported => false;

    public IReadOnlyList<string> IconExtensions => [];

    public bool SupportsIconIndex => false;

    public Task<string?> GetCustomIconAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<bool> SetIconAsync(string folder, string iconPath, int index, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ResetAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
