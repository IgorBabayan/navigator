using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Navigator.App.Views;
using Navigator.App.Views.Dialogs;
using Navigator.Core.Formatting;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Presentation.Details;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.App.Services;

internal sealed class DialogService(WindowService windows, Localizer localizer, IFileOwnershipProvider ownership) : IDialogService
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
        var box = new TextBox { Text = request.InitialText };
        var error = new TextBlock { Classes = { "error" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        dialog.AddContent(new TextBlock { Text = request.Label, Classes = { "caption" } });
        dialog.AddContent(box);
        dialog.AddContent(error);
        var ok = dialog.AddButton(request.PrimaryText, "ok", isDefault: true);
        dialog.AddButton(localizer["Dialog.Cancel"], "cancel", isCancel: true);

        void Validate()
        {
            var message = string.IsNullOrWhiteSpace(box.Text) ? localizer["Validation.Empty"] : request.Validate?.Invoke(box.Text);
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
        return result == "ok" && ok.IsEnabled ? box.Text : null;
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
        var model = new PropertiesViewModel(paths, localizer, ownership);
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
