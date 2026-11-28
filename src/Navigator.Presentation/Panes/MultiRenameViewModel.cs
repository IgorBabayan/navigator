using CommunityToolkit.Mvvm.ComponentModel;
using Navigator.Core.IO;
using Navigator.Core.Renaming;
using Navigator.Presentation.Localization;

namespace Navigator.Presentation.Panes;

/// <summary>An item handed to the multi-rename dialog.</summary>
public sealed record MultiRenameItem(string Path, bool IsDirectory, DateTime LastWriteTimeUtc);

/// <summary>One line of the multi-rename preview. Rows are created once; only the new name and error change.</summary>
public sealed class MultiRenameRow : ObservableObject
{
    private string _newName;
    private string? _error;

    internal MultiRenameRow(MultiRenameItem item)
    {
        Item = item;
        OldName = Path.GetFileName(item.Path);
        Folder = Path.GetDirectoryName(item.Path) ?? string.Empty;
        Target = item.Path;
        _newName = OldName;
    }

    public MultiRenameItem Item { get; }

    public string OldName { get; }

    public string Folder { get; }

    /// <summary>Full path the item gets.</summary>
    internal string Target { get; set; }

    public string NewName
    {
        get => _newName;
        internal set
        {
            if (SetProperty(ref _newName, value))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    /// <summary>Why this item cannot get its new name; null when it can.</summary>
    public string? Error
    {
        get => _error;
        internal set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    public bool IsChanged => !string.Equals(_newName, OldName, StringComparison.Ordinal);
}

/// <summary>
/// The multi-rename dialog (Ctrl+R), modelled on Total Commander's Multi-Rename Tool: name and extension masks with
/// placeholders, search &amp; replace (plain or regular expression), case conversion and a counter, with a live
/// preview. Every change recompiles the masks once and re-applies them to the rows; rows are never recreated, so the
/// virtualized preview only redraws what is visible. Existing names are read once per folder, not per keystroke.
/// </summary>
public sealed class MultiRenameViewModel : ObservableObject
{
    /// <summary>Last settings used in this session; the next dialog starts from them (like Total Commander).</summary>
    private static MultiRenameOptions _remembered = new();

    private readonly Localizer _localizer;
    private readonly Dictionary<string, HashSet<string>> _existingByFolder = new(PathUtil.Comparer);
    private readonly HashSet<string> _sources;
    private MultiRenameOptions _options;
    private string? _templateError;
    private string _summary = string.Empty;
    private bool _canRename;
    private int _changedCount;
    private string _counterStart;
    private string _counterStep;
    private string _counterDigits;
    private bool _counterInvalid;

    public MultiRenameViewModel(IReadOnlyList<MultiRenameItem> items, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(localizer);
        _localizer = localizer;
        Rows = [.. items.Select(i => new MultiRenameRow(i))];
        _sources = new HashSet<string>(items.Select(i => i.Path), PathUtil.Comparer);
        _options = _remembered;
        _counterStart = Invariant(_options.CounterStart);
        _counterStep = Invariant(_options.CounterStep);
        _counterDigits = Invariant(_options.CounterDigits);
        CaseChoices =
        [
            localizer["MultiRename.Case.Unchanged"],
            localizer["MultiRename.Case.Lower"],
            localizer["MultiRename.Case.Upper"],
            localizer["MultiRename.Case.FirstLetter"],
            localizer["MultiRename.Case.EachWord"],
        ];
        Title = localizer.Format("MultiRename.Title", items.Count);
        Recompute();
    }

    public string Title { get; }

    public IReadOnlyList<MultiRenameRow> Rows { get; }

    public IReadOnlyList<string> CaseChoices { get; }

    public string NameMask
    {
        get => _options.NameMask;
        set => Update(_options with { NameMask = value ?? string.Empty });
    }

    public string ExtensionMask
    {
        get => _options.ExtensionMask;
        set => Update(_options with { ExtensionMask = value ?? string.Empty });
    }

    public string Search
    {
        get => _options.Search;
        set => Update(_options with { Search = value ?? string.Empty });
    }

    public string Replace
    {
        get => _options.Replace;
        set => Update(_options with { Replace = value ?? string.Empty });
    }

    public bool UseRegex
    {
        get => _options.UseRegex;
        set => Update(_options with { UseRegex = value });
    }

    public bool MatchCase
    {
        get => _options.MatchCase;
        set => Update(_options with { MatchCase = value });
    }

    public int CaseIndex
    {
        get => (int)_options.Case;
        set => Update(_options with { Case = Enum.IsDefined((RenameCase)value) ? (RenameCase)value : RenameCase.Unchanged });
    }

    /// <summary>Counter start ([C]); text so a half-typed number ("-") is not rejected by the box.</summary>
    public string CounterStart
    {
        get => _counterStart;
        set => SetCounter(ref _counterStart, value);
    }

    public string CounterStep
    {
        get => _counterStep;
        set => SetCounter(ref _counterStep, value);
    }

    public string CounterDigits
    {
        get => _counterDigits;
        set => SetCounter(ref _counterDigits, value);
    }

    /// <summary>Why the masks or the pattern cannot be used; null when they can.</summary>
    public string? TemplateError
    {
        get => _templateError;
        private set
        {
            if (SetProperty(ref _templateError, value))
                OnPropertyChanged(nameof(HasTemplateError));
        }
    }

    public bool HasTemplateError => _templateError is not null;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool CanRename
    {
        get => _canRename;
        private set => SetProperty(ref _canRename, value);
    }

    /// <summary>Back to "[N]" / "[E]", no replacement, unchanged case, counter 1/1/1.</summary>
    public void Reset()
    {
        var defaults = new MultiRenameOptions();
        _counterStart = Invariant(defaults.CounterStart);
        _counterStep = Invariant(defaults.CounterStep);
        _counterDigits = Invariant(defaults.CounterDigits);
        _counterInvalid = false;
        _options = defaults with { NameMask = defaults.NameMask + " " }; // force the recompute below
        Update(defaults);
        foreach (var name in new[] { nameof(NameMask), nameof(ExtensionMask), nameof(Search), nameof(Replace), nameof(UseRegex), nameof(MatchCase), nameof(CaseIndex), nameof(CounterStart), nameof(CounterStep), nameof(CounterDigits) })
            OnPropertyChanged(name);
    }

    /// <summary>The renames to perform (changed rows only, in display order). Empty while anything is invalid.</summary>
    public IReadOnlyList<(string Path, string NewName)> GetRenames()
    {
        if (!_canRename)
            return [];
        _remembered = _options;
        var renames = new List<(string, string)>(_changedCount);
        foreach (var row in Rows)
        {
            if (row.IsChanged)
                renames.Add((row.Item.Path, row.NewName));
        }

        return renames;
    }

    private static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long? ParseCounter(string text, long limit)
        => long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value)
           && value >= -limit && value <= limit ? value : null;

    private void SetCounter(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (!SetProperty(ref field, value ?? string.Empty, property))
            return;
        var start = ParseCounter(_counterStart, 999_999_999);
        var step = ParseCounter(_counterStep, 999_999);
        var digits = ParseCounter(_counterDigits, 18);
        _counterInvalid = start is null || step is null || digits is null or < 1;
        if (_counterInvalid)
        {
            TemplateError = _localizer.Format("MultiRename.Error.InvalidCounter", string.Empty).TrimEnd();
            CanRename = false;
            return;
        }

        var options = _options with { CounterStart = start!.Value, CounterStep = step!.Value, CounterDigits = (int)digits!.Value };
        if (options == _options)
            Recompute(); // the error that blocked the previous value must clear
        else
            Update(options);
    }

    private void Update(MultiRenameOptions options)
    {
        if (options == _options)
            return;
        _options = options;
        Recompute();
    }

    private void Recompute()
    {
        if (_counterInvalid)
            return;
        MultiRenameTemplate template;
        try
        {
            template = MultiRenameTemplate.Compile(_options);
            TemplateError = null;
        }
        catch (MultiRenameTemplateException invalid)
        {
            TemplateError = _localizer.Format("MultiRename.Error." + invalid.Key, invalid.Detail);
            CanRename = false;
            return;
        }

        // Pass 1: new names, name validity and how often each target occurs.
        var counts = new Dictionary<string, int>(Rows.Count, PathUtil.Comparer);
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var item = row.Item;
            var name = template.Apply(new RenameSource(row.OldName, item.IsDirectory, row.Folder, item.LastWriteTimeUtc.ToLocalTime()), i);
            row.NewName = name;
            row.Target = Path.Combine(row.Folder, name);
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, row.Target, out _);
            count++;
        }

        // Pass 2: errors (invalid name, two items on one name, a name taken by an item outside the batch).
        int changed = 0, errors = 0;
        foreach (var row in Rows)
        {
            string? error = null;
            if (row.IsChanged)
            {
                var validity = PathUtil.ValidateName(row.NewName);
                if (validity != NameValidationResult.Valid)
                    error = _localizer["Validation." + validity];
                else if (counts[row.Target] > 1)
                    error = _localizer["Validation.Duplicate"];
                else if (!_sources.Contains(row.Target) && ExistingNames(row.Folder).Contains(row.NewName))
                    error = _localizer["Validation.AlreadyExists"];
                changed++;
            }
            else if (counts[row.Target] > 1)
            {
                error = _localizer["Validation.Duplicate"];
            }

            row.Error = error;
            if (error is not null)
                errors++;
        }

        _changedCount = changed;
        CanRename = changed > 0 && errors == 0;
        Summary = errors > 0
            ? _localizer.Format("MultiRename.SummaryErrors", errors)
            : _localizer.Format("MultiRename.Summary", changed, Rows.Count);
    }

    /// <summary>Names present in a folder, read once (the dialog is modal; nothing else renames meanwhile).</summary>
    private HashSet<string> ExistingNames(string folder)
    {
        if (_existingByFolder.TryGetValue(folder, out var names))
            return names;
        names = new HashSet<string>(PathUtil.Comparer);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                names.Add(Path.GetFileName(entry));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: the rename itself reports a clash, the preview cannot know it in advance.
        }

        _existingByFolder[folder] = names;
        return names;
    }
}
