using System.Runtime.CompilerServices;

namespace Navigator.Plugins;

/// <summary>A sidebar group known to the host (<see cref="PluginSidebarGroups"/>): its title, icon and position.</summary>
/// <param name="SharedCommandId">
/// A header command that every member offers (e.g. "add-account"): shown once; when several members offer it, the user
/// picks the member first.
/// </param>
internal sealed record SidebarGroup(string Id, string TitleKey, string IconKey, int Order, string? SharedCommandId = null, string? SharedCommandTitleKey = null, string? ChooserLabelKey = null)
{
    public static IReadOnlyDictionary<string, SidebarGroup> Known { get; } = new Dictionary<string, SidebarGroup>(StringComparer.Ordinal)
    {
        // Right below Network (100), above the server plugins (SSH 200, FTP 300).
        [PluginSidebarGroups.Cloud] = new(PluginSidebarGroups.Cloud, "Sidebar.Cloud", "Cloud", 150, "add-account", "Plugin.Cloud.AddAccount", "Plugin.Cloud.Service"),
    };
}

/// <summary>
/// One sidebar section that shows the sections of several plugins (<see cref="IPluginSidebarGroupMember"/>), e.g. the
/// "Cloud" section with the Google Drive and OneDrive accounts. Entries keep pointing at the member that produced them,
/// so entry commands reach the right plugin. The section listens to its members only while someone listens to it, so a
/// replaced snapshot holds no subscriptions.
/// </summary>
internal sealed class SidebarGroupSection : IPluginSidebarSection
{
    private const char Separator = '\u001F';

    private readonly SidebarGroup _group;
    private readonly IReadOnlyList<IPluginSidebarSection> _members;
    private readonly IPluginHostServices _services;
    private readonly ConditionalWeakTable<PluginSidebarEntry, IPluginSidebarSection> _owners = [];
    private readonly Lock _gate = new();
    private EventHandler? _changed;

    public SidebarGroupSection(SidebarGroup group, IReadOnlyList<IPluginSidebarSection> members, IPluginHostServices services)
    {
        _group = group;
        _members = members;
        _services = services;
    }

    public IReadOnlyList<IPluginSidebarSection> Members => _members;

    public string Id => _group.Id;

    public string Title => _services.GetString(_group.TitleKey);

    public string IconKey => _group.IconKey;

    public string? SystemIconKey => null;

    public int Order => _group.Order;

    public bool ShowWhenEmpty => _members.Any(m => m.ShowWhenEmpty);

    public IReadOnlyList<PluginCommand> Commands
    {
        get
        {
            var commands = new List<PluginCommand>();
            var shared = false;
            foreach (var member in _members)
            {
                foreach (var command in member.Commands)
                {
                    if (command.Id == _group.SharedCommandId)
                    {
                        shared = true;
                        continue;
                    }

                    commands.Add(command with { Id = member.Id + Separator + command.Id });
                }
            }

            if (shared)
                commands.Insert(0, new PluginCommand(_group.SharedCommandId!, _services.GetString(_group.SharedCommandTitleKey!), "Add"));
            return commands;
        }
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                var first = _changed is null;
                _changed += value;
                if (first && _changed is not null)
                {
                    foreach (var member in _members)
                        member.Changed += OnMemberChanged;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    foreach (var member in _members)
                        member.Changed -= OnMemberChanged;
                }
            }
        }
    }

    public async ValueTask<IReadOnlyList<PluginSidebarEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        // Members are independent: list them together; one failing member leaves the others visible.
        var lists = await Task.WhenAll(_members.Select(member => LoadAsync(member, cancellationToken))).ConfigureAwait(false);
        var entries = new List<PluginSidebarEntry>();
        for (var i = 0; i < _members.Count; i++)
        {
            foreach (var entry in lists[i])
            {
                _owners.AddOrUpdate(entry, _members[i]);
                entries.Add(entry);
            }
        }

        return entries;
    }

    public async Task ExecuteAsync(string commandId, PluginSidebarEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is not null)
        {
            if (_owners.TryGetValue(entry, out var owner))
                await owner.ExecuteAsync(commandId, entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (commandId == _group.SharedCommandId)
        {
            var offering = _members.Where(m => m.Commands.Any(c => c.Id == commandId)).ToList();
            var target = offering.Count == 1 ? offering[0] : await ChooseAsync(offering).ConfigureAwait(false);
            if (target is not null)
                await target.ExecuteAsync(commandId, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var separator = commandId.IndexOf(Separator, StringComparison.Ordinal);
        if (separator > 0 && _members.FirstOrDefault(m => m.Id == commandId[..separator]) is { } member)
            await member.ExecuteAsync(commandId[(separator + 1)..], null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IPluginSidebarSection?> ChooseAsync(List<IPluginSidebarSection> members)
    {
        if (members.Count == 0)
            return null;
        var title = _services.GetString(_group.SharedCommandTitleKey!);
        var field = new PluginFormField("member", _services.GetString(_group.ChooserLabelKey!), PluginFieldKind.Choice, members[0].Id)
        {
            Choices = [.. members.Select(m => new PluginChoice(m.Id, m.Title))],
        };
        var values = await _services.Ui.ShowFormAsync(new PluginForm(title, _services.GetString("Plugin.Cloud.Continue"), [field])).ConfigureAwait(false);
        return values?.GetValueOrDefault("member") is { } id ? members.FirstOrDefault(m => m.Id == id) : null;
    }

    private async Task<IReadOnlyList<PluginSidebarEntry>> LoadAsync(IPluginSidebarSection member, CancellationToken cancellationToken)
    {
        try
        {
            return await member.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _services.CreateLog(member.Id).Warning("Sidebar entries could not be listed", ex);
            return [];
        }
    }

    private void OnMemberChanged(object? sender, EventArgs e) => _changed?.Invoke(this, EventArgs.Empty);
}
