using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Navigator.Core.IO;
using Navigator.Plugins;

namespace Navigator.Plugin.WindowsSearch;

/// <summary>Pane searches from the Windows Search index. Initialization only registers the provider.</summary>
public sealed class WindowsSearchPlugin : INavigatorPlugin
{
    internal const string EnabledPreference = "enabled";

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows() || !IndexQuery.IsSupported)
            return;
        var text = new PluginStrings(typeof(WindowsSearchPlugin).Assembly, () => context.Language);
        var preferences = new PluginPreferences(() => context.DataDirectory);
        var provider = new WindowsSearchProvider(preferences, context.Log);
        context.AddSearchProvider(provider);
        context.SetSettingsPage(new WindowsSearchSettingsPage(provider, preferences, text, context));
    }
}

/// <summary>
/// Answers a search when the folder searched has items in the index (checked with a one-row query, remembered for two
/// minutes); everything else goes to Navigator's own search. Results stream in batches while the index returns them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSearchProvider(PluginPreferences preferences, IPluginLog log) : ISearchProvider
{
    private static readonly TimeSpan IndexedLifetime = TimeSpan.FromMinutes(2);
    private const int BatchSize = 128;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<string, (bool Indexed, long At)> _indexed = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "windows-search";

    public async ValueTask<bool> CanSearchAsync(PluginSearchRequest request, CancellationToken cancellationToken)
    {
        if (!preferences.GetBool(WindowsSearchPlugin.EnabledPreference, true) || !WindowsSearchProviderRules.IsLocalFolder(request.Root))
            return false;
        var now = Environment.TickCount64;
        if (_indexed.TryGetValue(request.Root, out var known) && now - known.At < IndexedLifetime.TotalMilliseconds)
            return known.Indexed;

        bool indexed;
        try
        {
            indexed = await IndexQuery.RunOnWorker(() =>
            {
                var any = false;
                IndexQuery.Execute(SearchSql.Probe(request.Root), _ =>
                {
                    any = true;
                    return false; // one row is enough
                }, cancellationToken);
                return any;
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log.Info($"The Windows Search index is not available: {ex.Message}");
            indexed = false;
        }

        _indexed[request.Root] = (indexed, Environment.TickCount64);
        return indexed;
    }

    public async IAsyncEnumerable<IReadOnlyList<FileEntry>> SearchAsync(PluginSearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sql = SearchSql.Build(request, request.MaxResults + 1);
        var root = Path.TrimEndingDirectorySeparator(request.Root);
        var batches = Channel.CreateBounded<FileEntry[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
        var query = IndexQuery.RunOnWorker(() =>
        {
            var batch = new List<FileEntry>(BatchSize);
            var clock = Stopwatch.StartNew();
            try
            {
                IndexQuery.Execute(sql, entry =>
                {
                    if (string.Equals(Path.TrimEndingDirectorySeparator(entry.FullPath), root, StringComparison.OrdinalIgnoreCase))
                        return true; // the folder searched is not a result
                    if (!request.IncludeHidden && (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        return true;
                    batch.Add(entry);
                    if (batch.Count < BatchSize && clock.Elapsed < BatchInterval)
                        return true;
                    var full = batch.ToArray();
                    batch.Clear();
                    clock.Restart();
                    // Back-pressure: this (dedicated) worker waits while the pane is busy with earlier batches.
                    try
                    {
                        batches.Writer.WriteAsync(full, cancellationToken).AsTask().GetAwaiter().GetResult();
                        return true;
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                    {
                        return false;
                    }
                }, cancellationToken);
                if (batch.Count > 0 && !cancellationToken.IsCancellationRequested)
                    batches.Writer.TryWrite([.. batch]);
                batches.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                batches.Writer.TryComplete();
            }
            catch (Exception failure)
            {
                batches.Writer.TryComplete(failure);
            }

            return true;
        });

        await foreach (var batch in batches.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return batch;
        await query.ConfigureAwait(false);
    }

    /// <summary>Forgets which folders are indexed (the user changed the indexing options).</summary>
    public void Reset() => _indexed.Clear();

}

/// <summary>Which searches the index may answer (plain string checks).</summary>
internal static class WindowsSearchProviderRules
{
    /// <summary>A folder on a drive of this computer; network shares are not searched through the local index.</summary>
    public static bool IsLocalFolder(string root)
        => root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] is '\\' or '/';
}

/// <summary>Settings → Plugins → Windows Search index.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSearchSettingsPage : IPluginSettingsPage
{
    private readonly WindowsSearchProvider _provider;
    private readonly PluginPreferences _preferences;
    private readonly PluginStrings _text;
    private readonly IPluginContext _context;

    public WindowsSearchSettingsPage(WindowsSearchProvider provider, PluginPreferences preferences, PluginStrings text, IPluginContext context)
    {
        _provider = provider;
        _preferences = preferences;
        _text = text;
        _context = context;
        _preferences.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginSetting> GetSettings() =>
    [
        new PluginSetting(WindowsSearchPlugin.EnabledPreference, PluginSettingKind.Toggle, _text["WindowsSearch.Settings.Enabled"], _text["WindowsSearch.Settings.EnabledHint"])
        {
            IsOn = _preferences.GetBool(WindowsSearchPlugin.EnabledPreference, true),
        },
        new PluginSetting("options", PluginSettingKind.Action, _text["WindowsSearch.Settings.Options"], _text["WindowsSearch.Settings.OptionsHint"])
        {
            ActionText = _text["WindowsSearch.Settings.OptionsButton"],
        },
    ];

    public async Task ApplyAsync(string settingId, bool? value, CancellationToken cancellationToken)
    {
        if (settingId == WindowsSearchPlugin.EnabledPreference && value is { } on)
        {
            _preferences.SetBool(WindowsSearchPlugin.EnabledPreference, on);
            _provider.Reset();
            return;
        }

        if (settingId != "options")
            return;
        _provider.Reset(); // what is indexed may change there
        try
        {
            // Control Panel → Indexing Options.
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "control.exe"), "srchadmin.dll") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            await _context.Ui.ShowMessageAsync(_text["WindowsSearch.Settings.Options"], ex.Message).ConfigureAwait(true);
        }
    }
}
