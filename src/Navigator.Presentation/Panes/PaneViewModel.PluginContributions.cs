using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Core.Navigation;
using Navigator.Core.Search;
using Navigator.Plugins;
using Navigator.Presentation.Menus;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Plugin context-menu entries and plugin search providers (plugin API 1.3). Both are asked off the UI thread and never
/// hold up Navigator's own work: a menu opens with Navigator's entries and gains the plugin entries when they arrive; a
/// search falls back to Navigator's own engine when no provider accepts it or the provider fails before its first result.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>How long a menu waits for a provider before leaving its entries out.</summary>
    private static readonly TimeSpan PluginMenuPatience = TimeSpan.FromSeconds(3);

    /// <summary>How long a search waits for a provider to say whether it can answer.</summary>
    private static readonly TimeSpan SearchProviderPatience = TimeSpan.FromSeconds(1);

    /// <summary>The entries of every menu provider for <paramref name="request"/>, or null when there are none to ask.</summary>
    private Task<IReadOnlyList<MenuEntry>>? PluginMenuEntriesAsync(PluginMenuRequest request, CancellationToken lifetime)
    {
        var providers = _services.Plugins.MenuProviders;
        if (providers.Count == 0 || request.Targets.Count == 0 || request.Targets.Any(RemoteUri.IsRemote))
            return null;
        return Task.Run(() => CollectPluginMenuEntriesAsync(providers, request, lifetime), CancellationToken.None);
    }

    private async Task<IReadOnlyList<MenuEntry>> CollectPluginMenuEntriesAsync(IReadOnlyList<IItemMenuProvider> providers, PluginMenuRequest request, CancellationToken lifetime)
    {
        var asked = new Task<IReadOnlyList<PluginMenuItem>>[providers.Count];
        for (var i = 0; i < providers.Count; i++)
            asked[i] = AskMenuProviderAsync(providers[i], request, lifetime);
        var answers = await Task.WhenAll(asked).ConfigureAwait(false);

        var entries = new List<MenuEntry>();
        foreach (var items in answers)
        {
            if (items.Count == 0)
                continue;
            if (entries.Count > 0)
                entries.Add(MenuEntry.Divider());
            foreach (var item in items)
                entries.Add(ToMenuEntry(item));
        }

        PaneMenuComposer.Tidy(entries);
        return entries;
    }

    private async Task<IReadOnlyList<PluginMenuItem>> AskMenuProviderAsync(IItemMenuProvider provider, PluginMenuRequest request, CancellationToken lifetime)
    {
        try
        {
            return await provider.GetItemsAsync(request, lifetime).WaitAsync(PluginMenuPatience, lifetime).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (TimeoutException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Menu entries of {Provider} took longer than {Seconds} s and were left out", provider.Id, PluginMenuPatience.TotalSeconds);
            return [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Menu provider {Provider} failed", provider.Id);
            return [];
        }
    }

    private MenuEntry ToMenuEntry(PluginMenuItem item)
    {
        if (item.IsSeparator)
            return MenuEntry.Divider();
        var invoke = item.Invoke;
        var load = item.LoadChildren;
        return new MenuEntry
        {
            Kind = item.IsChecked ? MenuEntryKind.Toggle : MenuEntryKind.Action,
            Label = item.Title,
            Glyph = item.IconKey,
            SystemIcon = item.SystemIconKey,
            IsEnabled = item.IsEnabled,
            IsChecked = item.IsChecked,
            Scopes = MenuScope.Anywhere,
            Command = invoke is null || item.Children is not null || load is not null ? null : new AsyncRelayCommand(() => RunPluginMenuItemAsync(item.Title, invoke)),
            Children = item.Children is { } children ? [.. children.Select(ToMenuEntry)] : null,
            LoadChildren = load is null ? null : token => LoadPluginChildrenAsync(load, token),
        };
    }

    private async Task<IReadOnlyList<MenuEntry>> LoadPluginChildrenAsync(Func<CancellationToken, Task<IReadOnlyList<PluginMenuItem>>> load, CancellationToken token)
    {
        try
        {
            var items = await Task.Run(() => load(token), token).WaitAsync(PluginMenuPatience, token).ConfigureAwait(false);
            var entries = new List<MenuEntry>(items.Count);
            foreach (var item in items)
                entries.Add(ToMenuEntry(item));
            PaneMenuComposer.Tidy(entries);
            return entries;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw; // the menu closed: the view stops filling it
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "A plugin sub-menu could not be filled");
            return [];
        }
    }

    private async Task RunPluginMenuItemAsync(string title, Func<CancellationToken, Task> invoke)
    {
        try
        {
            await invoke(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Dismissed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Plugin menu entry {Entry} failed", title);
            await _services.Dialogs.ShowMessageAsync(title, ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Results for a pane search: from the first plugin provider that accepts it (an index), otherwise from
    /// <see cref="FileSearchEngine"/>. A provider failing before its first result is replaced by the engine.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken token)
    {
        var request = new PluginSearchRequest(query.Root, query.Text, query.ContentText, query.Recursive, query.IncludeHidden, query.MaxResults);
        var provider = await ChooseSearchProviderAsync(request, token).ConfigureAwait(true);
        if (provider is null)
        {
            await foreach (var batch in FileSearchEngine.SearchAsync(query, token).ConfigureAwait(true))
                yield return batch;
            yield break;
        }

        var produced = 0;
        var fallBack = false;
        var results = provider.SearchAsync(request, token).GetAsyncEnumerator(token);
        try
        {
            while (produced < query.MaxResults)
            {
                IReadOnlyList<FileEntry> batch;
                try
                {
                    if (!await results.MoveNextAsync().ConfigureAwait(true))
                        break;
                    batch = results.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Search provider {Provider} failed", provider.Id);
                    fallBack = produced == 0;
                    break;
                }

                if (batch.Count == 0)
                    continue;
                if (produced + batch.Count > query.MaxResults)
                    batch = [.. batch.Take(query.MaxResults - produced)];
                produced += batch.Count;
                yield return batch;
            }
        }
        finally
        {
            await results.DisposeAsync().ConfigureAwait(true);
        }

        if (!fallBack)
            yield break;
        await foreach (var batch in FileSearchEngine.SearchAsync(query, token).ConfigureAwait(true))
            yield return batch;
    }

    private async Task<ISearchProvider?> ChooseSearchProviderAsync(PluginSearchRequest request, CancellationToken token)
    {
        var providers = _services.Plugins.SearchProviders;
        if (providers.Count == 0 || RemoteUri.IsRemote(request.Root))
            return null;
        foreach (var provider in providers)
        {
            try
            {
                if (await Task.Run(() => provider.CanSearchAsync(request, token).AsTask(), token).WaitAsync(SearchProviderPatience, token).ConfigureAwait(true))
                    return provider;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Search provider {Provider} could not be asked", provider.Id);
            }
        }

        return null;
    }
}
