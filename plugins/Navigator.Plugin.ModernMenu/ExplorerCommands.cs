using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;
using Navigator.Plugins;

namespace Navigator.Plugin.ModernMenu;

/// <summary>
/// A worker thread in the multithreaded COM apartment that runs queued work in order. Packaged commands live out of
/// process (their package's COM surrogate), so every call is a cross-process call: keeping them on one worker means a
/// slow handler delays only other app commands, never the UI, and objects are always released after their last use.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ComWorker : IDisposable
{
    private readonly BlockingCollection<Action> _inbox = new(boundedCapacity: 256);

    public ComWorker()
    {
        var thread = new Thread(Drain) { IsBackground = true, Name = "Navigator app commands" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        var outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _inbox.Add(() =>
            {
                try
                {
                    outcome.TrySetResult(work());
                }
                catch (Exception failure)
                {
                    // Boundary of the worker thread: the failure belongs to the caller; the thread keeps serving.
                    outcome.TrySetException(failure);
                }
            });
        }
        catch (InvalidOperationException)
        {
            outcome.TrySetCanceled();
        }

        return outcome.Task;
    }

    public void Dispose() => _inbox.CompleteAdding();

    private void Drain()
    {
        var initialized = CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        try
        {
            foreach (var work in _inbox.GetConsumingEnumerable())
                work();
        }
        finally
        {
            if (initialized >= 0)
                CoUninitialize();
            _inbox.Dispose();
        }
    }

    [LibraryImport("ole32")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32")]
    private static partial void CoUninitialize();
}

/// <summary>
/// The packaged commands of one context menu: the shell item array of the selection and every IExplorerCommand created
/// for it, released together (on the worker) a few seconds after the menu closed, so a click that closes the menu
/// still runs its command.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class ExplorerCommandSession
{
    private const int ClsctxLocalServer = 0x4;
    private const uint EcsDisabled = 0x1, EcsHidden = 0x2, EcsChecked = 0x8;
    private const uint EcfHasSubcommands = 0x1, EcfIsSeparator = 0x8, EcfSeparatorBefore = 0x20, EcfSeparatorAfter = 0x40;
    private const int MaxDepth = 2;
    private const int MaxChildren = 32;
    private static readonly Guid IidExplorerCommand = new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private readonly ComWorker _worker;
    private readonly List<nint> _objects = [];
    private nint _items;
    private int _released;

    private ExplorerCommandSession(ComWorker worker) => _worker = worker;

    /// <summary>
    /// Creates the commands of <paramref name="verbs"/> for <paramref name="paths"/> and describes them as menu entries
    /// (titles, icons, states and sub-commands as the apps report them for these items). Runs on the worker.
    /// </summary>
    public static Task<IReadOnlyList<PluginMenuItem>> BuildAsync(ComWorker worker, IReadOnlyList<PackagedVerb> verbs, IReadOnlyList<string> paths, CancellationToken menuLifetime)
    {
        var session = new ExplorerCommandSession(worker);
        menuLifetime.Register(session.ReleaseLater);
        return worker.RunAsync<IReadOnlyList<PluginMenuItem>>(() =>
        {
            if (menuLifetime.IsCancellationRequested)
                return [];
            session._items = CreateItemArray(paths);
            if (session._items == 0)
                return [];
            var entries = new List<PluginMenuItem>();
            foreach (var verb in verbs)
            {
                if (menuLifetime.IsCancellationRequested)
                    break;
                var clsid = verb.Clsid;
                var iid = IidExplorerCommand;
                nint command = 0;
                if (CoCreateInstance(&clsid, 0, ClsctxLocalServer, &iid, &command) < 0 || command == 0)
                    continue; // the app is not registered for out-of-process use, or failed to start
                session._objects.Add(command);
                session.Describe(command, depth: 0, entries);
            }

            return entries;
        });
    }

    /// <summary>Adds the entry (or entries, with separators the command asks for) of one command to <paramref name="into"/>.</summary>
    private void Describe(nint command, int depth, List<PluginMenuItem> into)
    {
        uint state = 0, flags = 0;
        // GetState(items, okToBeSlow, out state): a failure counts as "enabled".
        if (((delegate* unmanaged[Stdcall]<nint, nint, int, uint*, int>)Slot(command, 7))(command, _items, 1, &state) >= 0 && (state & EcsHidden) != 0)
            return;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(command, 9))(command, &flags); // GetFlags
        if ((flags & EcfIsSeparator) != 0)
        {
            into.Add(PluginMenuItem.Separator);
            return;
        }

        var title = TakeString(command, 3); // GetTitle
        if (string.IsNullOrWhiteSpace(title))
            return;
        title = title.Replace("&&", "\u0001", StringComparison.Ordinal).Replace("&", string.Empty, StringComparison.Ordinal).Replace('\u0001', '&');
        var icon = IconKeyOf(TakeString(command, 4)); // GetIcon

        List<PluginMenuItem>? children = null;
        if ((flags & EcfHasSubcommands) != 0 && depth < MaxDepth)
        {
            children = [];
            nint enumerator = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(command, 10))(command, &enumerator) >= 0 && enumerator != 0)
            {
                _objects.Add(enumerator);
                for (var i = 0; i < MaxChildren; i++)
                {
                    nint child = 0;
                    uint fetched = 0;
                    // IEnumExplorerCommand::Next(count, out command, out fetched)
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(enumerator, 3))(enumerator, 1, &child, &fetched) != 0 || fetched == 0 || child == 0)
                        break;
                    _objects.Add(child);
                    Describe(child, depth + 1, children);
                }
            }

            if (children.Count == 0)
                return;
        }

        if ((flags & EcfSeparatorBefore) != 0)
            into.Add(PluginMenuItem.Separator);
        into.Add(new PluginMenuItem(title)
        {
            IconKey = "ApplicationOutline",
            SystemIconKey = icon,
            IsEnabled = (state & EcsDisabled) == 0,
            IsChecked = (state & EcsChecked) != 0,
            Children = children,
            Invoke = children is null ? _ => InvokeAsync(command) : null,
        });
        if ((flags & EcfSeparatorAfter) != 0)
            into.Add(PluginMenuItem.Separator);
    }

    private Task<bool> InvokeAsync(nint command) => _worker.RunAsync(() =>
    {
        if (Volatile.Read(ref _released) != 0)
            return false;
        // The app's process may bring its window to the front (it was started by this click).
        _ = CoAllowSetForegroundWindow(command, 0);
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(command, 8))(command, _items, 0); // Invoke(items, bind context)
        if (result < 0)
            throw new IOException($"The app command failed (0x{result:X8}).");
        return true;
    });

    /// <summary>The menu closed: everything is released after <see cref="Grace"/>, behind any command still queued.</summary>
    private void ReleaseLater() => _ = Task.Delay(Grace).ContinueWith(_ => _worker.RunAsync(() =>
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return false;
        for (var i = _objects.Count - 1; i >= 0; i--)
            Release(_objects[i]);
        _objects.Clear();
        Release(_items);
        _items = 0;
        return true;
    }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>"C:\Program Files\App\app.exe,-101" → the system icon of app.exe; package resources are resolved first.</summary>
    private static string? IconKeyOf(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return null;
        var path = resource.Trim();
        if (path.StartsWith('@'))
            path = ResolveIndirect(path) ?? string.Empty;
        var comma = path.LastIndexOf(',');
        if (comma > 1 && int.TryParse(path.AsSpan(comma + 1), out _))
            path = path[..comma];
        path = path.Trim().Trim('"');
        return path.Length > 3 && Path.IsPathFullyQualified(path) && File.Exists(path) ? SystemIconKey.ForPath(path) : null;
    }

    private static string? ResolveIndirect(string resource)
    {
        const int Capacity = 1024;
        var buffer = stackalloc char[Capacity];
        return SHLoadIndirectString(resource, buffer, Capacity, 0) >= 0 ? new string(buffer) : null;
    }

    /// <summary>Calls a "get string" method (title, icon) and frees the string the app allocated.</summary>
    private string? TakeString(nint command, int slot)
    {
        char* text = null;
        if (((delegate* unmanaged[Stdcall]<nint, nint, char**, int>)Slot(command, slot))(command, _items, &text) < 0 || text == null)
            return null;
        try
        {
            return new string(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)text);
        }
    }

    /// <summary>An IShellItemArray of the paths (absolute PIDLs from the shell's parser).</summary>
    private static nint CreateItemArray(IReadOnlyList<string> paths)
    {
        var pidls = new nint[paths.Count];
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                uint attributes = 0;
                if (SHParseDisplayName(paths[i], 0, out pidls[i], 0, &attributes) < 0)
                    return 0;
            }

            nint array = 0;
            fixed (nint* list = pidls)
            {
                if (SHCreateShellItemArrayFromIDLists((uint)pidls.Length, list, &array) < 0)
                    return 0;
            }

            return array;
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                if (pidl != 0)
                    Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(nint unknown)
    {
        if (unknown != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);
    }

    [LibraryImport("ole32")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, int context, Guid* iid, nint* instance);

    [LibraryImport("ole32")]
    private static partial int CoAllowSetForegroundWindow(nint unknown, nint reserved);

    [LibraryImport("shell32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesWanted, uint* attributes);

    [LibraryImport("shell32")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint count, nint* pidls, nint* array);

    [LibraryImport("shlwapi", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, char* output, int capacity, nint reserved);
}
