using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Navigator.Core.Settings;

namespace Navigator.Plugins;

/// <summary>
/// Owns every plugin: built-in ones (compiled into the app, permanent) and installed ones (packages in
/// <see cref="PluginDirectory"/>). Startup cost is kept off the first frame: built-ins only register their
/// contributions; installed plugins are loaded by <see cref="StartAsync"/> on a worker thread after the window is up.
/// Contributions are published as immutable snapshots, so readers (sidebar, panes) never lock.
/// </summary>
/// <remarks>
/// Layout: <c>{PluginDirectory}/installed.json</c>, <c>{id}/{version}/</c> (package files), <c>{id}/data/</c> (plugin
/// settings), <c>.staging/</c> (extraction in progress). Folders not referenced by installed.json are removed at start,
/// which is how updates and removals finish (loaded assemblies cannot be deleted while the app runs on Windows).
/// </remarks>
public sealed class PluginHost : IDisposable
{
    private const string DataFolderName = "data";
    private const string StagingFolderName = ".staging";

    private readonly IPluginHostServices _services;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedDirectories = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Contributions _contributions = Contributions.Empty;
    private bool _disposed;

    public PluginHost(string pluginDirectory, IPluginHostServices services, IEnumerable<BuiltInPlugin> builtIns)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        PluginDirectory = pluginDirectory;
        _services = services;
        foreach (var builtIn in builtIns)
            InitializeBuiltIn(builtIn);
        RebuildContributions(raise: false);
    }

    public string PluginDirectory { get; }

    /// <summary>Raised (on any thread) when plugins or their contributions change.</summary>
    public event EventHandler? Changed;

    /// <summary>Completes when the installed plugins have been loaded (or failed to).</summary>
    public Task WhenLoaded => _loaded.Task;

    public IReadOnlyList<IPluginSidebarSection> SidebarSections => Volatile.Read(ref _contributions).Sections;

    public bool TryGetFileSystem(string scheme, out IRemoteFileSystem fileSystem)
        => Volatile.Read(ref _contributions).FileSystems.TryGetValue(scheme, out fileSystem!);

    public IReadOnlyList<PluginInfo> GetPlugins()
    {
        lock (_gate)
        {
            return [.. _slots.Values
                .OrderByDescending(s => s.IsBuiltIn)
                .ThenBy(s => s.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => s.ToInfo())];
        }
    }

    public PluginInfo? Find(string id)
    {
        lock (_gate)
            return _slots.TryGetValue(id, out var slot) ? slot.ToInfo() : null;
    }

    /// <summary>Loads the installed plugins on a worker thread and finishes pending updates/removals. Call once.</summary>
    public Task StartAsync() => Task.Run(() =>
    {
        try
        {
            var state = ReadState();
            CleanUp(state);
            foreach (var installed in state.Plugins)
                LoadInstalled(installed.Id, installed.Version);
            RebuildContributions(raise: true);
        }
        finally
        {
            _loaded.TrySetResult();
        }
    });

    /// <summary>
    /// Installs or updates a plugin from a verified package. A new plugin starts right away; an update of a running
    /// plugin becomes active after a restart (<see cref="PluginStatus.UpdatePending"/>).
    /// </summary>
    public async Task<PluginInfo> InstallAsync(string packagePath, string? expectedId, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var staging = Path.Combine(PluginDirectory, StagingFolderName, Guid.NewGuid().ToString("N"));
            PluginManifest manifest;
            try
            {
                manifest = await Task.Run(() => PluginPackage.Extract(packagePath, staging, cancellationToken), cancellationToken).ConfigureAwait(false);
                Validate(manifest, expectedId);
            }
            catch
            {
                TryDelete(staging);
                throw;
            }

            var target = Path.Combine(PluginDirectory, manifest.Id, manifest.Version);
            bool loadNow;
            lock (_gate)
            {
                if (_loadedDirectories.Contains(target))
                {
                    TryDelete(staging); // this exact version is loaded in this process: its files stay as they are
                }
                else
                {
                    TryDelete(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(staging, target);
                }

                var state = ReadState();
                state.Plugins.RemoveAll(p => p.Id == manifest.Id);
                state.Plugins.Add(new InstalledPlugin { Id = manifest.Id, Version = manifest.Version });
                WriteState(state);

                if (_slots.TryGetValue(manifest.Id, out var slot) && slot.Instance is not null && slot.Status == PluginStatus.Active)
                {
                    slot.PendingVersion = manifest.ParsedVersion == slot.Manifest.ParsedVersion ? null : manifest.Version;
                    slot.Status = slot.PendingVersion is null ? PluginStatus.Active : PluginStatus.UpdatePending;
                    loadNow = false;
                }
                else
                {
                    loadNow = true;
                }
            }

            if (loadNow)
            {
                await Task.Run(() => LoadInstalled(manifest.Id, manifest.Version), cancellationToken).ConfigureAwait(false);
                RebuildContributions(raise: false);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return Find(manifest.Id)!;
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Removes an installed plugin: its contributions disappear at once, its files and settings are deleted.</summary>
    public async Task UninstallAsync(string id)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Slot slot;
            lock (_gate)
            {
                if (!_slots.TryGetValue(id, out slot!))
                    throw new InvalidOperationException($"Plugin '{id}' is not installed.");
                if (slot.IsBuiltIn)
                    throw new InvalidOperationException($"'{slot.Manifest.Name}' is part of Navigator and cannot be removed.");
            }

            Stop(slot);
            RebuildContributions(raise: false);

            lock (_gate)
            {
                var state = ReadState();
                state.Plugins.RemoveAll(p => p.Id == id);
                WriteState(state);

                var folder = Path.Combine(PluginDirectory, id);
                TryDelete(Path.Combine(folder, DataFolderName));
                if (slot.Directory is not null && _loadedDirectories.Contains(slot.Directory))
                {
                    slot.Status = PluginStatus.Removed; // assemblies stay mapped until exit; deleted on next start
                    slot.PendingVersion = null;
                }
                else
                {
                    TryDelete(folder);
                    _slots.Remove(id);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _changes.Release();
        }
    }

    private void InitializeBuiltIn(BuiltInPlugin builtIn)
    {
        var manifest = builtIn.Manifest;
        manifest.Validate(requireEntryPoint: false);
        var slot = new Slot(manifest, isBuiltIn: true);
        _slots[manifest.Id] = slot;
        try
        {
            var context = CreateContext(manifest.Id);
            var instance = builtIn.Create();
            instance.Initialize(context);
            slot.Instance = instance;
            slot.Context = context;
            context.IsLive = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            slot.Status = PluginStatus.Failed;
            slot.Error = ex.Message;
            _services.CreateLog(manifest.Id).Error("Built-in plugin failed to start", ex);
        }
    }

    private void LoadInstalled(string id, string version)
    {
        var directory = Path.Combine(PluginDirectory, id, version);
        PluginManifest manifest;
        try
        {
            manifest = PluginManifest.Parse(File.ReadAllBytes(Path.Combine(directory, PluginManifest.FileName)));
            if (manifest.Id != id || manifest.Version != version)
                throw new InvalidDataException("plugin.json does not match the installed plugin.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (_gate)
                _slots[id] = new Slot(new PluginManifest { Id = id, Name = id, Version = version }, isBuiltIn: false) { Status = PluginStatus.Failed, Error = ex.Message };
            _services.CreateLog(id).Warning("Installed plugin is unreadable", ex);
            return;
        }

        var slot = new Slot(manifest, isBuiltIn: false) { Directory = directory };
        lock (_gate)
        {
            if (_slots.TryGetValue(id, out var existing) && existing.IsBuiltIn)
                return; // a package can never replace a built-in plugin
            _slots[id] = slot;
        }

        if (!PluginApi.IsCompatible(manifest.ApiVersion))
        {
            slot.Status = PluginStatus.Incompatible;
            slot.Error = $"Plugin API {manifest.ApiVersion}; Navigator provides {PluginApi.Version}.";
            return;
        }

        PluginContext? context = null;
        try
        {
            var loadContext = new PluginLoadContext(Path.Combine(directory, manifest.Assembly));
            lock (_gate)
                _loadedDirectories.Add(directory);
            var assembly = loadContext.LoadFromAssemblyPath(Path.Combine(directory, manifest.Assembly));
            var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
            if (!typeof(INavigatorPlugin).IsAssignableFrom(type))
                throw new InvalidDataException($"{manifest.EntryType} does not implement INavigatorPlugin.");
            var instance = (INavigatorPlugin)Activator.CreateInstance(type)!;
            context = CreateContext(id);
            instance.Initialize(context);
            slot.Instance = instance;
            slot.Context = context;
            slot.Status = PluginStatus.Active;
            context.IsLive = true;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or TypeLoadException or InvalidDataException or MissingMethodException
                                       or TargetInvocationException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            context?.Detach();
            slot.Status = PluginStatus.Failed;
            slot.Error = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            _services.CreateLog(id).Error("Plugin failed to load", ex);
        }
    }

    private PluginContext CreateContext(string id)
        => new(id, Path.Combine(PluginDirectory, id, DataFolderName), _services, () => RebuildContributions(raise: true));

    private void Stop(Slot slot)
    {
        slot.Context?.Detach();
        if (slot.Instance is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _services.CreateLog(slot.Manifest.Id).Warning("Plugin failed to shut down cleanly", ex);
            }
        }

        slot.Instance = null;
        slot.Context = null;
    }

    private void Validate(PluginManifest manifest, string? expectedId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(manifest.Id, out var existing) && existing.IsBuiltIn)
                throw new InvalidDataException($"'{existing.Manifest.Name}' is part of Navigator and cannot be replaced by a package.");
        }

        if (expectedId is not null && manifest.Id != expectedId)
            throw new InvalidDataException($"The package contains plugin '{manifest.Id}', not '{expectedId}'.");
        if (!PluginApi.IsCompatible(manifest.ApiVersion))
            throw new InvalidDataException($"The plugin needs plugin API {manifest.ApiVersion}; this Navigator provides {PluginApi.Version}.");
    }

    private void RebuildContributions(bool raise)
    {
        var sections = new List<IPluginSidebarSection>();
        var fileSystems = new Dictionary<string, IRemoteFileSystem>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Context is not { IsAttached: true } context)
                    continue;
                var (ownSections, ownFileSystems) = context.Snapshot();
                sections.AddRange(ownSections);
                foreach (var fileSystem in ownFileSystems)
                {
                    foreach (var scheme in fileSystem.Schemes)
                        fileSystems.TryAdd(scheme, fileSystem);
                }
            }
        }

        sections.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
        Volatile.Write(ref _contributions, new Contributions([.. sections], fileSystems.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)));
        if (raise)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes leftovers: staging folders, old versions and plugins no longer in installed.json.</summary>
    private void CleanUp(PluginState state)
    {
        if (!Directory.Exists(PluginDirectory))
            return;
        TryDelete(Path.Combine(PluginDirectory, StagingFolderName));
        var active = state.Plugins.ToDictionary(p => p.Id, p => p.Version, StringComparer.Ordinal);
        foreach (var folder in Directory.EnumerateDirectories(PluginDirectory))
        {
            var id = Path.GetFileName(folder);
            if (!active.TryGetValue(id, out var version))
            {
                TryDelete(folder);
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (name != version && name != DataFolderName)
                    TryDelete(child);
            }
        }
    }

    private PluginState ReadState()
    {
        var path = Path.Combine(PluginDirectory, PluginState.FileName);
        try
        {
            if (!File.Exists(path))
                return new PluginState();
            var state = JsonSerializer.Deserialize(File.ReadAllBytes(path), PluginJsonContext.Default.PluginState) ?? new PluginState();
            state.Plugins.RemoveAll(p => !PluginManifest.IsValidId(p.Id) || !Core.Versioning.SemVersion.TryParse(p.Version, out _));
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _services.CreateLog("host").Warning("installed.json is unreadable; no plugins are loaded", ex);
            return new PluginState();
        }
    }

    private void WriteState(PluginState state)
        => AtomicFile.WriteAllBytes(Path.Combine(PluginDirectory, PluginState.FileName), JsonSerializer.SerializeToUtf8Bytes(state, PluginJsonContext.Default.PluginState));

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.CreateLog("host").Warning($"Could not delete {path}; it is retried on the next start", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Slot> slots;
        lock (_gate)
            slots = [.. _slots.Values];
        foreach (var slot in slots)
            Stop(slot);
        _changes.Dispose();
    }

    private sealed class Slot(PluginManifest manifest, bool isBuiltIn)
    {
        public PluginManifest Manifest { get; } = manifest;

        public bool IsBuiltIn { get; } = isBuiltIn;

        public INavigatorPlugin? Instance { get; set; }

        public PluginContext? Context { get; set; }

        public PluginStatus Status { get; set; } = PluginStatus.Active;

        public string? Error { get; set; }

        public string? PendingVersion { get; set; }

        /// <summary>Folder of the loaded package (null for built-ins).</summary>
        public string? Directory { get; init; }

        public PluginInfo ToInfo() => new(Manifest, IsBuiltIn, Status, Error, PendingVersion);
    }

    private sealed record Contributions(IReadOnlyList<IPluginSidebarSection> Sections, FrozenDictionary<string, IRemoteFileSystem> FileSystems)
    {
        public static Contributions Empty { get; } = new([], FrozenDictionary<string, IRemoteFileSystem>.Empty);
    }
}
