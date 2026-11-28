using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Navigator.App.Controls;
using Navigator.App.Views;
using Navigator.App.Views.Dialogs;
using Navigator.Core.Formatting;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Presentation.Details;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.App.Services;

internal sealed class DialogService(WindowService windows, Localizer localizer, PropertiesServices properties) : IDialogService
{
    private Window? Owner => windows.ActiveTopLevel;

    public async Task<ConfirmResponse> ConfirmAsync(ConfirmRequest request)
    {
        var dialog = new DialogWindow(request.Title, request.Message);
        CheckBox? check = null;
        if (request.CheckBoxText is not null)
        {
            check = new CheckBox { Content = request.CheckBoxText };
            dialog.AddContent(check);
        }

        dialog.AddButton(request.PrimaryText, "primary", isDefault: true, isDestructive: request.IsDestructive);
        if (request.SecondaryText is not null)
            dialog.AddButton(request.SecondaryText, "secondary");
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        var result = await dialog.ShowFor(Owner).ConfigureAwait(true);
        return new ConfirmResponse(result switch { "primary" => DialogResult.Primary, "secondary" => DialogResult.Secondary, _ => DialogResult.Cancel }, check?.IsChecked == true);
    }

    public async Task<string?> PromptTextAsync(TextInputRequest request)
    {
        var dialog = new DialogWindow(request.Title, null);
        var box = new TextBox { Text = request.InitialText, PasswordChar = request.IsSecret ? '\u2022' : '\0' };
        var error = new TextBlock { Classes = { "error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        dialog.AddContent(new TextBlock { Text = request.Label, Classes = { "caption" } });
        dialog.AddContent(box);
        dialog.AddContent(error);
        var ok = dialog.AddButton(request.PrimaryText, "ok", isDefault: true);
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        void Validate()
        {
            // A password may legitimately be empty; names may not.
            var message = string.IsNullOrWhiteSpace(box.Text) && !request.IsSecret ? localizer["Validation.Empty"] : request.Validate?.Invoke(box.Text ?? string.Empty);
            error.Text = message;
            error.IsVisible = message is not null && !string.IsNullOrEmpty(box.Text);
            ok.IsEnabled = message is null;
        }

        box.TextChanged += (_, _) => Validate();
        Validate();
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            // Select the name without the extension, like desktop rename.
            box.SelectionStart = 0;
            box.SelectionEnd = request.SelectionLength >= 0 ? request.SelectionLength : box.Text?.Length ?? 0;
        };

        var result = await dialog.ShowFor(Owner).ConfigureAwait(true);
        return result == "ok" && ok.IsEnabled ? box.Text ?? string.Empty : null;
    }

    public async Task<bool> ShowMultiRenameAsync(Navigator.Presentation.Panes.MultiRenameViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var window = new MultiRenameWindow { DataContext = model };
        if (Owner is { } owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
            return window.Confirmed;
        }

        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(window.Confirmed);
        window.Show();
        return await closed.Task.ConfigureAwait(true);
    }

    /// <summary>
    /// A small form built from field descriptions (plugin connection settings). Each field validates as the user types;
    /// the primary button is enabled only when every required field is filled and every validator passes.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> ShowFormAsync(FormRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new DialogWindow(request.Title, null);
        var readers = new List<(FormField Field, Func<string> Read, TextBlock Error)>(request.Fields.Count);
        Button? ok = null;
        Control? first = null;

        void Validate()
        {
            var valid = true;
            foreach (var (field, read, error) in readers)
            {
                var value = read();
                var message = field.IsRequired && string.IsNullOrWhiteSpace(value) ? localizer["Validation.Empty"] : field.Validate?.Invoke(value);
                error.Text = message;
                error.IsVisible = message is not null && value.Length > 0;
                valid &= message is null;
            }

            if (ok is not null)
                ok.IsEnabled = valid;
        }

        foreach (var field in request.Fields)
        {
            var error = new TextBlock { Classes = { "error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
            Control input;
            Func<string> read;
            switch (field.Kind)
            {
                case FormFieldKind.Toggle:
                {
                    var check = new CheckBox { Content = field.Label, IsChecked = string.Equals(field.Value, "true", StringComparison.OrdinalIgnoreCase) };
                    check.IsCheckedChanged += (_, _) => Validate();
                    input = check;
                    read = () => check.IsChecked == true ? "true" : "false";
                    break;
                }

                case FormFieldKind.Choice:
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = field.Choices.Select(c => c.Text).ToList(),
                        SelectedIndex = Math.Max(0, field.Choices.ToList().FindIndex(c => c.Id == field.Value)),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    combo.SelectionChanged += (_, _) => Validate();
                    input = combo;
                    read = () => combo.SelectedIndex >= 0 && combo.SelectedIndex < field.Choices.Count ? field.Choices[combo.SelectedIndex].Id : string.Empty;
                    break;
                }

                case FormFieldKind.FilePath:
                {
                    var box = new TextBox { Text = field.Value, PlaceholderText = field.Placeholder };
                    var browse = new Button { Content = localizer["Dialog.Browse"], Margin = new Avalonia.Thickness(8, 0, 0, 0) };
                    browse.Click += async (_, _) =>
                    {
                        if (await PickFileAsync(field.Label).ConfigureAwait(true) is { } path)
                            box.Text = path;
                    };
                    box.TextChanged += (_, _) => Validate();
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    Grid.SetColumn(browse, 1);
                    row.Children.Add(box);
                    row.Children.Add(browse);
                    input = row;
                    first ??= box;
                    read = () => box.Text ?? string.Empty;
                    break;
                }

                default:
                {
                    var box = new TextBox
                    {
                        Text = field.Value,
                        PlaceholderText = field.Placeholder,
                        PasswordChar = field.Kind == FormFieldKind.Password ? '\u2022' : '\0',
                    };
                    box.TextChanged += (_, _) => Validate();
                    input = box;
                    first ??= box;
                    read = () => box.Text ?? string.Empty;
                    break;
                }
            }

            if (field.Kind != FormFieldKind.Toggle)
                dialog.AddContent(new TextBlock { Text = field.Label, Classes = { "caption" } });
            dialog.AddContent(input);
            dialog.AddContent(error);
            readers.Add((field, read, error));
        }

        ok = dialog.AddButton(request.PrimaryText, "ok", isDefault: true);
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);
        Validate();
        dialog.Opened += (_, _) => first?.Focus();

        var result = await dialog.ShowFor(Owner).ConfigureAwait(true);
        if (result != "ok" || !ok.IsEnabled)
            return null;
        var values = new Dictionary<string, string>(readers.Count, StringComparer.Ordinal);
        foreach (var (field, read, _) in readers)
            values[field.Id] = read();
        return values;
    }

    public async Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
    {
        var name = Path.GetFileName(conflict.DestinationPath);
        var dialog = new DialogWindow(
            localizer[conflict.IsFolderMerge ? "Conflict.FolderTitle" : "Conflict.Title"],
            localizer.Format(conflict.IsFolderMerge ? "Conflict.FolderMessage" : "Conflict.Message", name, Path.GetDirectoryName(conflict.DestinationPath)));

        if (!conflict.SourceIsDirectory || !conflict.DestinationIsDirectory)
        {
            var details = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddRow(details, 0, localizer["Conflict.Incoming"], Describe(conflict.SourceLength, conflict.SourceLastWriteUtc, conflict.SourceIsDirectory));
            AddRow(details, 1, localizer["Conflict.Existing"], Describe(conflict.DestinationLength, conflict.DestinationLastWriteUtc, conflict.DestinationIsDirectory));
            dialog.AddContent(details);
        }

        var applyAll = new CheckBox { Content = localizer["Conflict.ApplyToAll"] };
        dialog.AddContent(applyAll);
        if (conflict.CanReplace)
            dialog.AddButton(localizer[conflict.IsFolderMerge ? "Conflict.Merge" : "Conflict.Replace"], "replace", isDefault: true);
        dialog.AddButton(localizer["Conflict.KeepBoth"], "keep", isDefault: !conflict.CanReplace);
        dialog.AddButton(localizer["Conflict.Skip"], "skip");
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        var result = await dialog.ShowFor(Owner).ConfigureAwait(true);
        var resolution = result switch
        {
            "replace" => ConflictResolution.Replace,
            "keep" => ConflictResolution.KeepBoth,
            "skip" => ConflictResolution.Skip,
            _ => ConflictResolution.Cancel,
        };
        return new ConflictChoice(resolution, applyAll.IsChecked == true);
    }

    /// <summary>
    /// The reference app's "Conflicting file name" dialog: one choice that applies to every conflict ("Custom" while the
    /// rows disagree), a count line, then one row per conflict with its own choice. A row whose item cannot replace the
    /// existing one (file onto folder or the reverse) offers only a new name or skipping.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, NameConflictAction>?> ResolveNameConflictsAsync(IReadOnlyList<NameConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var dialog = new DialogWindow(localizer["NameConflict.Title"], null) { Width = 640 };
        var rows = new List<(NameConflict Conflict, ComboBox Choice)>(conflicts.Count);
        var syncing = false;

        ComboBoxItem Option(string key, NameConflictAction? action) => new() { Content = localizer[key], Tag = action };

        var everything = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        everything.Items.Add(Option("NameConflict.Custom", null));
        everything.Items.Add(Option("NameConflict.GenerateNewName", NameConflictAction.GenerateNewName));
        everything.Items.Add(Option("NameConflict.ReplaceExisting", NameConflictAction.ReplaceExisting));
        everything.Items.Add(Option("NameConflict.Skip", NameConflictAction.Skip));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock { Text = localizer["NameConflict.ApplyToAll"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(everything, 1);
        header.Children.Add(everything);
        dialog.AddContent(header);
        dialog.AddContent(new TextBlock
        {
            Classes = { "warning" },
            Text = conflicts.Count == 1 ? localizer["NameConflict.CountOne"] : localizer.Format("NameConflict.CountMany", conflicts.Count),
        });
        dialog.AddContent(new Separator());

        static NameConflictAction? ChoiceOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as NameConflictAction?;

        static void Select(ComboBox box, NameConflictAction action)
        {
            foreach (var entry in box.Items)
            {
                if (entry is ComboBoxItem { Tag: NameConflictAction tagged } item && tagged == action)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
        }

        // The "all" choice mirrors the rows: their common choice, or "Custom" when they differ.
        void ReflectRows()
        {
            NameConflictAction? common = null;
            var mixed = false;
            foreach (var (_, choice) in rows)
            {
                var action = ChoiceOf(choice);
                if (common is null)
                    common = action;
                else if (common != action)
                    mixed = true;
            }

            syncing = true;
            if (mixed || common is null)
                everything.SelectedIndex = 0;
            else
                Select(everything, common.Value);
            syncing = false;
        }

        var list = new StackPanel { Spacing = 12 };
        foreach (var conflict in conflicts)
        {
            var choice = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
            choice.Items.Add(Option("NameConflict.GenerateNewName", NameConflictAction.GenerateNewName));
            if (conflict.CanReplace)
                choice.Items.Add(Option("NameConflict.ReplaceExisting", NameConflictAction.ReplaceExisting));
            choice.Items.Add(Option("NameConflict.Skip", NameConflictAction.Skip));
            Select(choice, conflict.CanReplace ? NameConflictAction.ReplaceExisting : NameConflictAction.GenerateNewName);
            choice.SelectionChanged += (_, _) =>
            {
                if (!syncing)
                    ReflectRows();
            };
            rows.Add((conflict, choice));
            list.Children.Add(ConflictRow(conflict, choice));
        }

        everything.SelectionChanged += (_, _) =>
        {
            if (syncing || ChoiceOf(everything) is not { } action)
                return;
            syncing = true;
            foreach (var (conflict, choice) in rows)
                Select(choice, action == NameConflictAction.ReplaceExisting && !conflict.CanReplace ? NameConflictAction.GenerateNewName : action);
            syncing = false;
            ReflectRows();
        };
        ReflectRows();

        dialog.AddContent(new ScrollViewer { Content = list, MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        dialog.AddButton(localizer["NameConflict.Continue"], "continue", isDefault: true);
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        if (await dialog.ShowFor(Owner).ConfigureAwait(true) != "continue")
            return null;

        var answers = new Dictionary<string, NameConflictAction>(rows.Count, StringComparer.Ordinal);
        foreach (var (conflict, choice) in rows)
            answers[conflict.SourcePath] = ChoiceOf(choice) ?? NameConflictAction.Skip;
        return answers;
    }

    /// <summary>Icon, "name → name" and "Downloads → Documents" on the left, the row's own choice on the right.</summary>
    private static Grid ConflictRow(NameConflict conflict, ComboBox choice)
    {
        static string FolderOf(string path)
        {
            var parent = Path.GetDirectoryName(path) ?? path;
            var name = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? parent : name;
        }

        var icon = new IconView
        {
            Kind = conflict.SourceIsDirectory ? "Folder" : "File",
            SystemIcon = conflict.SourceIsDirectory ? SystemIconKey.Stock(SystemIconKind.Folder) : SystemIconKey.ForPath(conflict.SourcePath),
            Width = 32,
            Height = 32,
            Margin = new Avalonia.Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        names.Children.Add(new TextBlock
        {
            Text = $"{Path.GetFileName(conflict.SourcePath)} \u2192 {Path.GetFileName(conflict.DestinationPath)}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        names.Children.Add(new TextBlock
        {
            Classes = { "route" },
            Text = $"{FolderOf(conflict.SourcePath)} \u2192 {FolderOf(conflict.DestinationPath)}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(names, 1);
        Grid.SetColumn(choice, 2);
        choice.Margin = new Avalonia.Thickness(12, 0, 0, 0);
        row.Children.Add(icon);
        row.Children.Add(names);
        row.Children.Add(choice);
        return row;
    }

    public async Task<ErrorChoice> ResolveErrorAsync(string path, FileOperationErrorKind kind, string message, bool canRetry)
    {
        var dialog = new DialogWindow(localizer["ErrorKind." + kind], localizer.Format("Error.ItemFailed", Path.GetFileName(path)) + Environment.NewLine + message);
        var applyAll = new CheckBox { Content = localizer["Conflict.ApplyToAll"] };
        dialog.AddContent(applyAll);
        if (canRetry)
            dialog.AddButton(localizer["Error.Retry"], "retry", isDefault: true);
        dialog.AddButton(localizer["Conflict.Skip"], "skip", isDefault: !canRetry);
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        var result = await dialog.ShowFor(Owner).ConfigureAwait(true);
        var resolution = result switch { "retry" => ErrorResolution.Retry, "skip" => ErrorResolution.Skip, _ => ErrorResolution.Cancel };
        return new ErrorChoice(resolution, applyAll.IsChecked == true);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new DialogWindow(title, message);
        dialog.AddButton(localizer["Dialog.Ok"], "ok", isDefault: true, isCancel: true);
        await dialog.ShowFor(Owner).ConfigureAwait(true);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (extensions is { Count: > 0 })
            options.FileTypeFilter = [new FilePickerFileType(title) { Patterns = [.. extensions.Select(e => "*" + e)] }];
        var files = await storage.OpenFilePickerAsync(options).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        if (Owner?.StorageProvider is not { } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public void ShowProperties(IReadOnlyList<string> paths)
    {
        var model = new PropertiesViewModel(paths, properties, (title, extensions) => PickFileAsync(title, extensions));
        var window = new PropertiesWindow { DataContext = model };
        window.Closed += (_, _) => model.Dispose();
        if (Owner is { } owner)
            window.Show(owner);
        else
            window.Show();
        _ = model.LoadAsync();
    }

    private string Describe(long length, DateTime modifiedUtc, bool isDirectory)
    {
        var size = isDirectory ? localizer["Item.FolderType"] : ByteSize.Format(Math.Max(0, length), localizer.Culture);
        return modifiedUtc > DateTime.MinValue ? $"{size} · {modifiedUtc.ToLocalTime().ToString("g", localizer.Culture)}" : size;
    }

    private static void AddRow(Grid grid, int row, string label, string value)
    {
        var header = new TextBlock { Text = label, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(12, 0, 0, 4) };
        Grid.SetRow(header, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(header);
        grid.Children.Add(text);
    }
}
