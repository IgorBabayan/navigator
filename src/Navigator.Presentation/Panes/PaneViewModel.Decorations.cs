using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Navigator.Core.IO;
using Navigator.Plugins;
using Navigator.Presentation.Items;
using Navigator.Presentation.Plugins;

namespace Navigator.Presentation.Panes;

/// <summary>
/// Plugin folder decorations (plugin API 1.3): Git status and last commits, cloud sync state… shown as plugin columns,
/// icon badges and status bar segments. A run starts once a local folder's listing is complete and is cancelled by
/// navigation; changes of the folder or a decorator's own change notice start a new run after a short pause, so a
/// burst of file changes costs one run. Everything a decorator does happens off the UI thread; the UI thread only
/// assigns the merged results to the item view models (unchanged results cause no property changes).
/// </summary>
public sealed partial class PaneViewModel
{
    private static readonly TimeSpan DecorationPause = TimeSpan.FromMilliseconds(400);
    private static readonly IReadOnlyDictionary<string, PluginItemDecoration> NoDecorations = new Dictionary<string, PluginItemDecoration>(0);

    private CancellationTokenSource? _decorating;
    private IReadOnlyDictionary<string, PluginItemDecoration> _decorations = NoDecorations;
    private int _decorationSchedule;
    private bool _decorationsAttached;
    private FolderDecorationReason _nextDecorationReason = FolderDecorationReason.Opened;
    private PluginColumnStrip? _pluginColumns;

    /// <summary>Status bar segments of plugins for the folder shown (e.g. the Git branch).</summary>
    public ObservableCollection<StatusSegmentViewModel> StatusSegments { get; } = [];

    public bool HasStatusSegments => StatusSegments.Count > 0;

    /// <summary>Columns plugins fill for the folder shown (details layout).</summary>
    public PluginColumnStrip PluginColumns => _pluginColumns ??= new PluginColumnStrip(_services.Settings);

    /// <summary>The decoration of a new item view model (incremental updates, re-sorts).</summary>
    private PluginItemDecoration? DecorationFor(FileEntry entry)
        => _decorations.Count > 0 && _decorations.TryGetValue(entry.Name, out var decoration) ? decoration : null;

    /// <summary>Header context menu: shows or hides a plugin column. A column shown again is filled by a new run.</summary>
    public void SetPluginColumnVisible(PluginColumnViewModel column, bool isVisible)
    {
        PluginColumns.SetVisible(column, isVisible);
        if (isVisible)
            StartDecorations(FolderDecorationReason.Opened);
    }

    private void AttachDecorations()
    {
        if (_decorationsAttached)
            return;
        _decorationsAttached = true;
        _services.Decorations.Invalidated += OnDecorationsInvalidated;
    }

    private void DetachDecorations()
    {
        if (!_decorationsAttached)
            return;
        _decorationsAttached = false;
        _services.Decorations.Invalidated -= OnDecorationsInvalidated;
    }

    /// <summary>A decorator says its results are stale (a commit, a checkout, a sync): decorate again shortly.</summary>
    private void OnDecorationsInvalidated(object? sender, FolderDecorationsChangedEventArgs e)
    {
        var shown = _location.Path;
        if (shown is null || !IsFolder || !e.Covers(shown))
            return;
        _services.Dispatcher.Post(() =>
        {
            if (!_disposed && IsFolder && string.Equals(_location.Path, shown, StringComparison.Ordinal))
                ScheduleDecorations(FolderDecorationReason.Changed);
        });
    }

    /// <summary>Navigation: results of the previous folder must not be applied to the next one.</summary>
    private void ResetDecorations()
    {
        CancelDecorations();
        _decorationSchedule++;
        _decorations = NoDecorations;
        if (!IsFolder)
            ClearDecorationViews();
    }

    private void ClearDecorationViews()
    {
        if (StatusSegments.Count > 0)
        {
            StatusSegments.Clear();
            OnPropertyChanged(nameof(HasStatusSegments));
        }

        if (_pluginColumns is { } columns && columns.Available.Count > 0)
            columns.Show([]);
    }

    private void CancelDecorations()
    {
        var running = _decorating;
        _decorating = null;
        if (running is null)
            return;
        running.Cancel();
        running.Dispose();
    }

    /// <summary>Starts a run after <see cref="DecorationPause"/>; later calls within the pause replace earlier ones.</summary>
    private async void ScheduleDecorations(FolderDecorationReason reason)
    {
        if (_services.Decorations.Decorators.Count == 0)
            return;
        if (reason > _nextDecorationReason)
            _nextDecorationReason = reason;
        var ticket = ++_decorationSchedule;
        await Task.Delay(DecorationPause).ConfigureAwait(true);
        if (ticket == _decorationSchedule && !_disposed && !IsLoading)
            StartDecorations(_nextDecorationReason);
    }

    /// <summary>Starts a run for the folder shown now (UI thread).</summary>
    private void StartDecorations(FolderDecorationReason reason)
    {
        CancelDecorations();
        _nextDecorationReason = FolderDecorationReason.Opened;
        var decorators = _services.Decorations.Decorators;
        if (_disposed || decorators.Count == 0 || !IsFolder || _location.Path is not { } folder)
        {
            _decorations = NoDecorations;
            ClearDecorationViews();
            return;
        }

        AttachDecorations();
        var run = new CancellationTokenSource();
        _decorating = run;
        var entries = _entries.Values.ToArray();
        _ = RunDecorationsAsync(folder, entries, decorators, reason, run.Token);
    }

    private async Task RunDecorationsAsync(string folder, FileEntry[] entries, IReadOnlyList<IFolderDecorator> decorators, FolderDecorationReason reason, CancellationToken token)
    {
        try
        {
            var applicable = await Task.Run(() => Applicable(decorators, folder), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
                return;

            PluginColumns.Show(applicable);
            RemoveSegmentsExcept(applicable);
            if (applicable.Count == 0)
            {
                ApplyDecorations(NoDecorations);
                return;
            }

            var requests = new FolderDecorationRequest[applicable.Count];
            for (var i = 0; i < applicable.Count; i++)
                requests[i] = new FolderDecorationRequest(folder, entries, PluginColumns.VisibleColumnsOf(applicable[i]), reason);

            await foreach (var merged in DecorateAll(applicable, requests, token).ReadAllAsync(token).ConfigureAwait(true))
            {
                if (token.IsCancellationRequested)
                    return;
                ApplyDecorations(merged.Items);
                ApplySegments(applicable, merged.Segments);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Navigated away or a newer run started.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Folder decorations failed");
        }
    }

    private List<IFolderDecorator> Applicable(IReadOnlyList<IFolderDecorator> decorators, string folder)
    {
        var applicable = new List<IFolderDecorator>(decorators.Count);
        foreach (var decorator in decorators)
        {
            try
            {
                if (decorator.AppliesTo(folder))
                    applicable.Add(decorator);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Folder decorator {Decorator} failed", decorator.Id);
            }
        }

        return applicable;
    }

    /// <summary>One merged state of all decorators of a run.</summary>
    private sealed record MergedDecorations(IReadOnlyDictionary<string, PluginItemDecoration> Items, PluginStatusSegment?[] Segments);

    /// <summary>
    /// Runs every decorator concurrently (worker threads) and streams merged snapshots: each step is merged into its
    /// decorator's result, then all results are combined (badge of the first decorator that has one; cells keyed by
    /// decorator). Steps that arrive together are combined once.
    /// </summary>
    private ChannelReader<MergedDecorations> DecorateAll(List<IFolderDecorator> decorators, FolderDecorationRequest[] requests, CancellationToken token)
    {
        var steps = Channel.CreateUnbounded<(int Index, FolderDecoration Step)>(new UnboundedChannelOptions { SingleReader = true });
        var output = Channel.CreateBounded<MergedDecorations>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        var logger = _logger;

        var producers = new Task[decorators.Count];
        for (var i = 0; i < decorators.Count; i++)
        {
            var index = i;
            producers[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var step in decorators[index].DecorateAsync(requests[index], token).ConfigureAwait(false))
                    {
                        if (step is not null)
                            steps.Writer.TryWrite((index, step));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(ex, "Folder decorator {Decorator} failed", decorators[index].Id);
                }
            }, token);
        }

        _ = Task.WhenAll(producers).ContinueWith(_ => steps.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        _ = Task.Run(async () =>
        {
            var perDecorator = new Dictionary<string, ItemDecoration>[decorators.Count];
            var segments = new PluginStatusSegment?[decorators.Count];
            try
            {
                while (await steps.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (steps.Reader.TryRead(out var next))
                    {
                        if (next.Step.Status is { } status)
                            segments[next.Index] = status;
                        MergeStep(perDecorator[next.Index] ??= new Dictionary<string, ItemDecoration>(StringComparer.Ordinal), next.Step.Items);
                    }

                    await output.Writer.WriteAsync(new MergedDecorations(Combine(decorators, perDecorator), [.. segments]), token).ConfigureAwait(false);
                }

                output.Writer.TryComplete();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                output.Writer.TryComplete();
            }
        }, token);

        return output.Reader;
    }

    private static void MergeStep(Dictionary<string, ItemDecoration> into, IReadOnlyDictionary<string, ItemDecoration> step)
    {
        foreach (var (name, decoration) in step)
        {
            if (decoration is null)
                continue;
            if (!into.TryGetValue(name, out var earlier))
            {
                into[name] = decoration;
                continue;
            }

            Dictionary<string, string>? cells = null;
            if (earlier.Cells is { Count: > 0 } || decoration.Cells is { Count: > 0 })
            {
                cells = new Dictionary<string, string>(StringComparer.Ordinal);
                if (earlier.Cells is { } old)
                {
                    foreach (var (key, text) in old)
                        cells[key] = text;
                }

                if (decoration.Cells is { } fresh)
                {
                    foreach (var (key, text) in fresh)
                        cells[key] = text;
                }
            }

            into[name] = new ItemDecoration { Badge = decoration.Badge ?? earlier.Badge, Cells = cells };
        }
    }

    private static Dictionary<string, PluginItemDecoration> Combine(List<IFolderDecorator> decorators, Dictionary<string, ItemDecoration>?[] results)
    {
        var capacity = 0;
        foreach (var result in results)
            capacity = Math.Max(capacity, result?.Count ?? 0);
        var badges = new Dictionary<string, PluginBadge>(capacity, StringComparer.Ordinal);
        var cells = new Dictionary<string, Dictionary<string, string>>(capacity, StringComparer.Ordinal);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is not { } result)
                continue;
            var prefix = decorators[i].Id + ".";
            foreach (var (name, decoration) in result)
            {
                if (decoration.Badge is { } badge)
                    badges.TryAdd(name, badge);
                if (decoration.Cells is not { Count: > 0 } own)
                    continue;
                if (!cells.TryGetValue(name, out var row))
                    cells[name] = row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, text) in own)
                {
                    if (!string.IsNullOrEmpty(text))
                        row[prefix + key] = text;
                }
            }
        }

        var combined = new Dictionary<string, PluginItemDecoration>(Math.Max(badges.Count, cells.Count), StringComparer.Ordinal);
        foreach (var (name, badge) in badges)
            combined[name] = new PluginItemDecoration(badge, cells.GetValueOrDefault(name));
        foreach (var (name, row) in cells)
        {
            if (!combined.ContainsKey(name))
                combined[name] = new PluginItemDecoration(null, row);
        }

        return combined;
    }

    /// <summary>Assigns decorations to every item of the listing (filtered-out items too). UI thread.</summary>
    private void ApplyDecorations(IReadOnlyDictionary<string, PluginItemDecoration> decorations)
    {
        _decorations = decorations;
        foreach (var item in _sortedAll)
            item.Decoration = decorations.Count > 0 && decorations.TryGetValue(item.Name, out var decoration) ? decoration : null;
    }

    private void ApplySegments(List<IFolderDecorator> decorators, PluginStatusSegment?[] segments)
    {
        var changed = false;
        for (var i = 0; i < decorators.Count; i++)
        {
            var decorator = decorators[i];
            var index = IndexOfSegment(decorator.Id);
            if (segments[i] is not { } segment)
                continue;
            if (index >= 0 && SameSegment(StatusSegments[index].Source, segment))
                continue;
            var model = new StatusSegmentViewModel(decorator.Id, segment, [.. segment.Commands.Select(c => SegmentCommand(decorator, c))]);
            if (index >= 0)
                StatusSegments[index] = model;
            else
                StatusSegments.Add(model);
            changed = true;
        }

        if (changed)
            OnPropertyChanged(nameof(HasStatusSegments));
    }

    private static bool SameSegment(PluginStatusSegment a, PluginStatusSegment b)
        => string.Equals(a.Text, b.Text, StringComparison.Ordinal) && string.Equals(a.IconKey, b.IconKey, StringComparison.Ordinal)
           && string.Equals(a.ToolTip, b.ToolTip, StringComparison.Ordinal) && a.Commands.SequenceEqual(b.Commands);

    private void RemoveSegmentsExcept(List<IFolderDecorator> applicable)
    {
        var removed = false;
        for (var i = StatusSegments.Count - 1; i >= 0; i--)
        {
            var id = StatusSegments[i].DecoratorId;
            if (!applicable.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal)))
            {
                StatusSegments.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
            OnPropertyChanged(nameof(HasStatusSegments));
    }

    private int IndexOfSegment(string decoratorId)
    {
        for (var i = 0; i < StatusSegments.Count; i++)
        {
            if (string.Equals(StatusSegments[i].DecoratorId, decoratorId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private StatusSegmentCommandViewModel SegmentCommand(IFolderDecorator decorator, PluginCommand command)
        => new(command.Title, command.IconKey, command.IsDestructive, new AsyncRelayCommand(() => RunSegmentCommandAsync(decorator, command)));

    /// <summary>Runs a status segment command (Pull, Push…) for the folder shown, then decorates the folder again.</summary>
    private async Task RunSegmentCommandAsync(IFolderDecorator decorator, PluginCommand command)
    {
        if (!IsFolder || _location.Path is not { } folder)
            return;
        try
        {
            await decorator.ExecuteAsync(command.Id, folder, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Plugin command {Command} of {Decorator} failed", command.Id, decorator.Id);
            await _services.Dialogs.ShowMessageAsync(command.Title, ex.Message).ConfigureAwait(true);
        }

        if (!_disposed && IsFolder && string.Equals(_location.Path, folder, StringComparison.Ordinal))
            StartDecorations(FolderDecorationReason.Refresh);
    }
}
