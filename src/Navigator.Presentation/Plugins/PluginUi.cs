using Navigator.Plugins;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;

namespace Navigator.Presentation.Plugins;

/// <summary>Plugin dialogs, shown through <see cref="IDialogService"/> on the UI thread whatever thread the plugin calls from.</summary>
public sealed class PluginUi(IUiDispatcher dispatcher, IDialogService dialogs, Localizer localizer) : IPluginUi
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryText, bool isDestructive = false)
        => OnUiThreadAsync(async () => (await dialogs.ConfirmAsync(new ConfirmRequest(title, message, primaryText, IsDestructive: isDestructive)).ConfigureAwait(true)).Result == DialogResult.Primary);

    public Task<string?> PromptTextAsync(string title, string label, string initialText = "", bool isSecret = false)
        => OnUiThreadAsync(() => dialogs.PromptTextAsync(new TextInputRequest(title, label, initialText, localizer["Dialog.Ok"], IsSecret: isSecret)));

    public Task ShowMessageAsync(string title, string message)
        => OnUiThreadAsync(async () =>
        {
            await dialogs.ShowMessageAsync(title, message).ConfigureAwait(true);
            return true;
        });

    public Task<IReadOnlyDictionary<string, string>?> ShowFormAsync(PluginForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var request = new FormRequest(form.Title, form.PrimaryText, [.. form.Fields.Select(f => new FormField(
            f.Id, f.Label, (FormFieldKind)(int)f.Kind, f.Value, f.IsRequired, [.. f.Choices.Select(c => new FormChoice(c.Id, c.Text))], f.Placeholder, f.Validate))]);
        return OnUiThreadAsync(() => dialogs.ShowFormAsync(request));
    }

    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> show)
    {
        if (dispatcher.CheckAccess())
            return show();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await show().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }
}
