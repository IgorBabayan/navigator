namespace Navigator.Plugins;

/// <summary>
/// Dialogs shown by the host on behalf of a plugin, in Navigator's own style. Every method may be called from any
/// thread; the host switches to the UI thread. A cancelled dialog returns false / null.
/// </summary>
public interface IPluginUi
{
    Task<bool> ConfirmAsync(string title, string message, string primaryText, bool isDestructive = false);

    /// <summary>One-line input. <paramref name="isSecret"/> masks the text (passwords, passphrases).</summary>
    Task<string?> PromptTextAsync(string title, string label, string initialText = "", bool isSecret = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>A small form. Returns the field values by <see cref="PluginFormField.Id"/>, or null when cancelled.</summary>
    Task<IReadOnlyDictionary<string, string>?> ShowFormAsync(PluginForm form);

    /// <summary>A transient notification in the window's corner (e.g. "Pushed to origin/main"); never blocks (plugin API 1.3).</summary>
    void Notify(string title, string message, PluginNotificationKind kind = PluginNotificationKind.Information);
}

public enum PluginFieldKind
{
    Text,
    Number,
    Password,

    /// <summary>A local file path with a "Browse…" button.</summary>
    FilePath,

    /// <summary>One of <see cref="PluginFormField.Choices"/> (the value is the choice id).</summary>
    Choice,

    /// <summary>A check box; the value is "true" or "false".</summary>
    Toggle,
}

public sealed record PluginChoice(string Id, string Text);

/// <summary>One form field. <see cref="Validate"/> returns an error message for invalid input, or null.</summary>
public sealed record PluginFormField(string Id, string Label, PluginFieldKind Kind = PluginFieldKind.Text, string Value = "", bool IsRequired = false)
{
    public IReadOnlyList<PluginChoice> Choices { get; init; } = [];

    public string? Placeholder { get; init; }

    public Func<string, string?>? Validate { get; init; }
}

public sealed record PluginForm(string Title, string PrimaryText, IReadOnlyList<PluginFormField> Fields);
