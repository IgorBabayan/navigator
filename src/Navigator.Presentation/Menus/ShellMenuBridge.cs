using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.Platform;

namespace Navigator.Presentation.Menus;

/// <summary>
/// Brings the operating system's own context menu into a Navigator menu. "Open with" (verb <c>openas</c>) and "Send to"
/// (<c>sendto</c>) become the contents of Navigator's sub-menus of the same name; everything else is listed under
/// "Show more options". Entries Navigator already offers itself are filtered out by the shell query
/// (<see cref="NavigatorVerbs"/>), so nothing appears twice.
/// </summary>
public sealed class ShellMenuBridge(IShellMenu menu, Func<nint> ownerWindow, ILogger logger)
{
    public const string OpenWithVerb = "openas";
    public const string SendToVerb = "sendto";

    /// <summary>Shell verbs that duplicate Navigator commands (open, run as, clipboard, rename, shortcut, terminal…).</summary>
    public static readonly IReadOnlySet<string> NavigatorVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "opennewwindow", "opennewprocess", "opencontaining", "explore", "openinfiles",
        "runas", "runasuser", "cut", "copy", "paste", "delete", "rename", "properties", "link",
        "copyaspath", "pintohome", "extract",
        "{9F156763-7844-4DC4-B2B1-901F640F5155}", // Windows Terminal's "Open in Terminal"
    };

    /// <summary>The top-level shell entry with this verb, when the shell offered one with a sub-menu.</summary>
    public ShellMenuItem? FindSubmenu(string verb)
    {
        foreach (var item in menu.Items)
        {
            if (item.HasChildren && string.Equals(item.Verb, verb, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    /// <summary>Entries of a shell sub-menu, filled by the shell on first request.</summary>
    public async Task<IReadOnlyList<MenuEntry>> ChildrenOfAsync(ShellMenuItem parent, CancellationToken cancellationToken)
    {
        var children = await menu.GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false);
        return Translate(children);
    }

    /// <summary>Everything that belongs under "Show more options".</summary>
    public IReadOnlyList<MenuEntry> Remainder()
    {
        var rest = new List<ShellMenuItem>(menu.Items.Count);
        foreach (var item in menu.Items)
        {
            var hasOwnMenu = item.HasChildren && (string.Equals(item.Verb, OpenWithVerb, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(item.Verb, SendToVerb, StringComparison.OrdinalIgnoreCase));
            if (!hasOwnMenu)
                rest.Add(item);
        }

        return Translate(rest);
    }

    private List<MenuEntry> Translate(IReadOnlyList<ShellMenuItem> items)
    {
        var entries = new List<MenuEntry>(items.Count);
        foreach (var source in items)
        {
            if (source.IsSeparator)
            {
                entries.Add(MenuEntry.Divider());
                continue;
            }

            var shellItem = source;
            entries.Add(new MenuEntry
            {
                Kind = source.IsChecked ? MenuEntryKind.Toggle : MenuEntryKind.Action,
                Label = source.Label,
                Picture = source.Icon,
                IsEnabled = source.IsEnabled,
                IsChecked = source.IsChecked,
                Scopes = MenuScope.Anywhere,
                Command = source.HasChildren ? null : new AsyncRelayCommand(() => RunAsync(shellItem)),
                LoadChildren = source.HasChildren ? token => ChildrenOfAsync(shellItem, token) : null,
            });
        }

        PaneMenuComposer.Tidy(entries);
        return entries;
    }

    private async Task RunAsync(ShellMenuItem item)
    {
        try
        {
            if (!await menu.InvokeAsync(item, ownerWindow()).ConfigureAwait(true) && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Shell menu entry {Label} reported failure (or was dismissed)", item.Label);
        }
        catch (OperationCanceledException)
        {
            // The menu was released before the entry ran (window closing); nothing to report.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Shell menu entry {Label} was cancelled", item.Label);
        }
    }
}

/// <summary>
/// A composed pane menu plus what it holds on to: the shell query started for it and the token that ends its lazy
/// sub-menus. Dispose when the menu closes; the shell handlers are released once any clicked entry has run.
/// </summary>
public sealed class PaneMenu : IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Task<IShellMenu?>? _shell;

    public PaneMenu(IReadOnlyList<MenuEntry> entries, CancellationTokenSource lifetime, Task<IShellMenu?>? shell)
    {
        Entries = entries;
        _lifetime = lifetime;
        _shell = shell;
    }

    public IReadOnlyList<MenuEntry> Entries { get; }

    public CancellationToken Lifetime => _lifetime.Token;

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _shell?.ContinueWith(static query =>
        {
            if (query.IsCompletedSuccessfully)
                query.Result?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
