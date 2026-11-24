using System.Globalization;
using Navigator.Core.Navigation;

namespace Navigator.Plugins.Remote;

/// <summary>Per-scheme facts a <see cref="RemoteConnectionsSection"/> needs to build its form.</summary>
public sealed record RemoteProtocol(string Scheme, string DisplayName, int DefaultPort);

/// <summary>
/// Ready-made sidebar section for plugins that browse servers: lists saved connections and offers
/// "Add connection", "Edit…" and "Remove". Texts come from the host ("Plugin.Remote.*"), so every remote
/// plugin looks and reads the same.
/// </summary>
public sealed class RemoteConnectionsSection : IPluginSidebarSection
{
    private const string AddCommand = "add";
    private const string EditCommand = "edit";
    private const string RemoveCommand = "remove";

    private readonly IPluginContext _context;
    private readonly RemoteConnectionStore _store;
    private readonly Func<string> _title;
    private readonly IReadOnlyList<RemoteProtocol> _protocols;
    private readonly bool _supportsKeyFiles;
    private readonly bool _supportsAnonymous;

    public RemoteConnectionsSection(IPluginContext context, RemoteConnectionStore store, string id, Func<string> title, string iconKey, int order,
        IReadOnlyList<RemoteProtocol> protocols, bool supportsKeyFiles, bool supportsAnonymous)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfZero(protocols?.Count ?? 0);
        _context = context;
        _store = store;
        _title = title;
        _protocols = protocols!;
        _supportsKeyFiles = supportsKeyFiles;
        _supportsAnonymous = supportsAnonymous;
        Id = id;
        IconKey = iconKey;
        Order = order;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Id { get; }

    public string Title => _title();

    public string IconKey { get; }

    public string? SystemIconKey => null;

    public int Order { get; }

    public bool ShowWhenEmpty => true;

    public IReadOnlyList<PluginCommand> Commands => [new(AddCommand, Text("Plugin.Remote.Add"), "Add")];

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var commands = new PluginCommand[]
        {
            new(EditCommand, Text("Plugin.Remote.Edit"), "Rename"),
            new(RemoveCommand, Text("Plugin.Remote.Remove"), "Delete", IsDestructive: true),
        };
        IReadOnlyList<PluginSidebarEntry> entries = [.. _store.GetAll()
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new PluginSidebarEntry(c.Id, c.Name, IconKey, Location.Remote(c.Address.ToString()), c.Address.Authority) { Commands = commands })];
        return ValueTask.FromResult(entries);
    }

    public async Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken)
    {
        switch (commandId)
        {
            case AddCommand:
                await EditAsync(null).ConfigureAwait(false);
                break;
            case EditCommand when entry is not null && _store.Find(entry.Id) is { } existing:
                await EditAsync(existing).ConfigureAwait(false);
                break;
            case RemoveCommand when entry is not null:
                if (await _context.Ui.ConfirmAsync(Text("Plugin.Remote.Remove"), string.Format(CultureInfo.CurrentCulture, Text("Plugin.Remote.RemoveConfirm"), entry.Name),
                        Text("Plugin.Remote.Remove"), isDestructive: true).ConfigureAwait(false))
                    _store.Remove(entry.Id);
                break;
        }
    }

    private async Task EditAsync(RemoteConnection? existing)
    {
        var protocol = existing is null ? _protocols[0] : _protocols.FirstOrDefault(p => p.Scheme == existing.Scheme) ?? _protocols[0];
        var fields = new List<PluginFormField>
        {
            new("name", Text("Plugin.Remote.Name"), Value: existing?.Name ?? string.Empty) { Placeholder = Text("Plugin.Remote.NamePlaceholder") },
        };
        if (_protocols.Count > 1)
        {
            fields.Add(new PluginFormField("scheme", Text("Plugin.Remote.Protocol"), PluginFieldKind.Choice, protocol.Scheme)
            {
                Choices = [.. _protocols.Select(p => new PluginChoice(p.Scheme, p.DisplayName))],
            });
        }

        fields.Add(new PluginFormField("host", Text("Plugin.Remote.Host"), Value: existing?.Host ?? string.Empty, IsRequired: true) { Validate = ValidateHost });
        fields.Add(new PluginFormField("port", Text("Plugin.Remote.Port"), PluginFieldKind.Number, existing is { Port: > 0 } ? existing.Port.ToString(CultureInfo.InvariantCulture) : string.Empty)
        {
            Placeholder = Text("Plugin.Remote.PortDefault"),
            Validate = ValidatePort,
        });
        fields.Add(new PluginFormField("user", Text("Plugin.Remote.User"), Value: existing?.UserName ?? string.Empty));

        var authChoices = new List<PluginChoice> { new(nameof(RemoteAuthentication.Password), Text("Plugin.Remote.AuthPassword")) };
        if (_supportsKeyFiles)
            authChoices.Add(new PluginChoice(nameof(RemoteAuthentication.KeyFile), Text("Plugin.Remote.AuthKeyFile")));
        if (_supportsAnonymous)
            authChoices.Add(new PluginChoice(nameof(RemoteAuthentication.Anonymous), Text("Plugin.Remote.AuthAnonymous")));
        if (authChoices.Count > 1)
        {
            fields.Add(new PluginFormField("auth", Text("Plugin.Remote.Authentication"), PluginFieldKind.Choice, (existing?.Authentication ?? RemoteAuthentication.Password).ToString())
            {
                Choices = authChoices,
            });
        }

        if (_supportsKeyFiles)
            fields.Add(new PluginFormField("key", Text("Plugin.Remote.KeyFile"), PluginFieldKind.FilePath, existing?.KeyFile ?? string.Empty));
        fields.Add(new PluginFormField("path", Text("Plugin.Remote.Path"), Value: existing?.InitialPath ?? "/"));

        var title = Text(existing is null ? "Plugin.Remote.AddTitle" : "Plugin.Remote.EditTitle");
        var values = await _context.Ui.ShowFormAsync(new PluginForm(title, Text("Plugin.Remote.Save"), fields)).ConfigureAwait(false);
        if (values is null)
            return;

        var connection = existing ?? new RemoteConnection();
        connection.Scheme = values.GetValueOrDefault("scheme") is { Length: > 0 } scheme && _protocols.Any(p => p.Scheme == scheme) ? scheme : protocol.Scheme;
        connection.Host = values.GetValueOrDefault("host", string.Empty).Trim();
        connection.Port = int.TryParse(values.GetValueOrDefault("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : 0;
        connection.UserName = values.GetValueOrDefault("user") is { Length: > 0 } user ? user.Trim() : null;
        connection.Authentication = Enum.TryParse<RemoteAuthentication>(values.GetValueOrDefault("auth"), out var auth) ? auth : RemoteAuthentication.Password;
        connection.KeyFile = values.GetValueOrDefault("key") is { Length: > 0 } key ? key.Trim() : null;
        connection.InitialPath = RemoteUri.NormalizePath(values.GetValueOrDefault("path"));
        connection.Name = values.GetValueOrDefault("name") is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : connection.Address.Authority[(connection.Scheme.Length + 3)..];
        _store.AddOrUpdate(connection);
    }

    private string? ValidateHost(string value)
    {
        var host = value.Trim();
        return host.Length == 0 || host.AsSpan().IndexOfAny(" /\\@") >= 0 ? Text("Plugin.Remote.InvalidHost") : null;
    }

    private string? ValidatePort(string value)
        => value.Length == 0 || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535) ? null : Text("Plugin.Remote.InvalidPort");

    private string Text(string key) => _context.GetHostString(key);
}
