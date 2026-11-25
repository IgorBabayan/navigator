using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;

namespace Navigator.Platform.Shell;

/// <summary>Linux and macOS: file managers there have no shell menu API that other programs can host.</summary>
public sealed class UnsupportedShellMenus : IShellMenuProvider
{
    public bool IsSupported => false;

    public Task<IShellMenu?> OpenAsync(IReadOnlyList<string> paths, ShellMenuRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IShellMenu?>(null);
}

/// <summary>
/// Explorer's classic context menu (IContextMenu) for Windows items, hosted inside Navigator's own menu.
/// <para>
/// Every menu lives on its own short-lived STA thread (<see cref="ShellApartment"/>): shell extensions are apartment
/// threaded, may block for seconds, and must never run on the UI thread. One stalled extension therefore only stalls
/// its own menu, never the next right-click. Interfaces are called through their vtables, like the shell icon
/// provider, so no built-in COM interop is involved (trimming / AOT safe).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellMenus : IShellMenuProvider
{
    public bool IsSupported => true;

    public async Task<IShellMenu?> OpenAsync(IReadOnlyList<string> paths, ShellMenuRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);
        if (paths.Count == 0 || cancellationToken.IsCancellationRequested)
            return null;

        var apartment = new ShellApartment();
        NativeShellMenu? menu = null;
        try
        {
            menu = await apartment.RunAsync(() => NativeShellMenu.Build(apartment, paths, request)).ConfigureAwait(false);
            if (menu is not null && cancellationToken.IsCancellationRequested)
            {
                menu.Dispose();
                menu = null;
            }

            return menu;
        }
        catch (Exception refusal) when (refusal is COMException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Without a menu nobody else will ever end this thread.
            if (menu is null)
                apartment.Close();
        }
    }
}

/// <summary>A dedicated single-threaded apartment that runs queued work in order until it is closed.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ShellApartment : IDisposable
{
    private readonly BlockingCollection<Action> _inbox = new(boundedCapacity: 32);

    public ShellApartment()
    {
        var worker = new Thread(Drain) { IsBackground = true, Name = "Navigator shell menu" };
        worker.SetApartmentState(ApartmentState.STA); // shell handlers are apartment threaded
        worker.Start();
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        // Continuations leave the apartment: callers convert results into UI objects.
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
                    // Boundary of the apartment thread: the failure belongs to the caller, the thread must keep serving.
                    outcome.TrySetException(failure);
                }
            });
        }
        catch (InvalidOperationException)
        {
            outcome.TrySetCanceled(); // closed meanwhile
        }

        return outcome.Task;
    }

    /// <summary>Lets the thread finish what is queued, then exit.</summary>
    public void Close()
    {
        try
        {
            _inbox.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Already closed and drained.
        }
    }

    public void Dispose() => Close();

    private void Drain()
    {
        foreach (var work in _inbox.GetConsumingEnumerable())
            work();
        _inbox.Dispose(); // later RunAsync calls see ObjectDisposedException, an InvalidOperationException: cancelled
    }
}

/// <summary>One IContextMenu and the popup HMENU it filled. Every native call happens on its <see cref="ShellApartment"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class NativeShellMenu : IShellMenu
{
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;
    private const uint CmfNormal = 0x0;
    private const uint CmfExtendedVerbs = 0x100;
    private const uint GcsVerbW = 0x4;
    private const uint CmicMaskUnicode = 0x4000;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint MiimState = 0x1, MiimId = 0x2, MiimSubmenu = 0x4, MiimString = 0x40, MiimBitmap = 0x80, MiimFType = 0x100;
    private const uint MftSeparator = 0x800;
    private const uint MfsDisabled = 0x3;
    private const uint MfsChecked = 0x8;
    private const int SwShowNormal = 1;
    private const uint SendToLabelResource = 30312; // shell32 string table: "Send to"
    private const int LabelCapacity = 512;

    private static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu2Id = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid ContextMenu3Id = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid UiObjectHandler = new("3981E225-F559-11D3-8E3A-00C04F6837D5");   // BHID_SFUIObject
    private static readonly Guid ViewObjectHandler = new("3981E226-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFViewObject

    private readonly ShellApartment _apartment;
    private readonly nint _handler;
    private readonly nint _handler2;
    private readonly nint _handler3;
    private readonly nint _popup;
    private readonly string? _workingDirectory;
    private readonly IReadOnlySet<string> _hidden;
    private readonly string _sendToLabel;
    private readonly Dictionary<int, uint> _commandOffsets = [];
    private readonly Dictionary<int, (nint Handle, uint Position)> _children = [];
    private readonly Dictionary<int, IReadOnlyList<ShellMenuItem>> _loadedChildren = [];
    private int _lastKey;
    private bool _released;

    private NativeShellMenu(ShellApartment apartment, nint handler, nint popup, string? workingDirectory, IReadOnlySet<string> hidden)
    {
        _apartment = apartment;
        _handler = handler;
        _popup = popup;
        _workingDirectory = workingDirectory;
        _hidden = hidden;
        _handler2 = QueryInterface(handler, ContextMenu2Id);
        _handler3 = QueryInterface(handler, ContextMenu3Id);
        _sendToLabel = WithoutAccelerators(LoadShellString(SendToLabelResource));
    }

    public IReadOnlyList<ShellMenuItem> Items { get; private set; } = [];

    /// <summary>Runs on the apartment thread. Null when the shell has no menu for these paths.</summary>
    public static NativeShellMenu? Build(ShellApartment apartment, IReadOnlyList<string> paths, ShellMenuRequest request)
    {
        var handler = request.Background ? BindFolderView(paths[0]) : BindItems(paths);
        if (handler == 0)
            return null;

        var popup = CreatePopupMenu();
        if (popup == 0)
        {
            Release(handler);
            return null;
        }

        var queryContextMenu = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, int>)(*(nint**)handler)[3];
        var flags = request.ExtendedVerbs ? CmfExtendedVerbs : CmfNormal;
        if (queryContextMenu(handler, popup, 0, FirstId, LastId, flags) < 0)
        {
            _ = DestroyMenu(popup);
            Release(handler);
            return null;
        }

        var directory = request.Background ? paths[0] : Path.GetDirectoryName(paths[0]);
        var menu = new NativeShellMenu(apartment, handler, popup, directory, request.HiddenVerbs);
        menu.Items = menu.Read(popup);
        return menu;
    }

    public Task<IReadOnlyList<ShellMenuItem>> GetChildrenAsync(ShellMenuItem parent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return _apartment.RunAsync<IReadOnlyList<ShellMenuItem>>(() =>
        {
            if (_released || cancellationToken.IsCancellationRequested || !_children.TryGetValue(parent.Key, out var child))
                return [];
            if (_loadedChildren.TryGetValue(parent.Key, out var known))
                return known;

            // "Send to", "Open with" and similar sub-menus are empty until their handler sees WM_INITMENUPOPUP.
            var position = (nint)child.Position;
            if (_handler3 != 0)
            {
                nint ignored;
                var handleMenuMessage2 = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint*, int>)(*(nint**)_handler3)[7];
                _ = handleMenuMessage2(_handler3, WmInitMenuPopup, (nuint)child.Handle, position, &ignored);
            }
            else if (_handler2 != 0)
            {
                var handleMenuMessage = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, int>)(*(nint**)_handler2)[6];
                _ = handleMenuMessage(_handler2, WmInitMenuPopup, (nuint)child.Handle, position);
            }

            var entries = Read(child.Handle);
            _loadedChildren[parent.Key] = entries;
            return entries;
        });
    }

    public Task<bool> InvokeAsync(ShellMenuItem item, nint ownerWindow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item, nameof(item));
        return _apartment.RunAsync(() =>
        {
            if (_released || cancellationToken.IsCancellationRequested || !_commandOffsets.TryGetValue(item.Key, out var offset))
                return false;

            fixed (char* directory = _workingDirectory)
            {
                // CMINVOKECOMMANDINFOEX with the command given as an offset (MAKEINTRESOURCE) in both verb fields.
                InvokeInfo info = default;
                info.Size = (uint)sizeof(InvokeInfo);
                info.Mask = CmicMaskUnicode;
                info.Owner = ownerWindow;
                info.Verb = (nint)offset;
                info.VerbW = (nint)offset;
                info.DirectoryW = (nint)directory;
                info.Show = SwShowNormal;
                var invokeCommand = (delegate* unmanaged[Stdcall]<nint, InvokeInfo*, int>)(*(nint**)_handler)[4];
                return invokeCommand(_handler, &info) >= 0;
            }
        });
    }

    public void Dispose()
    {
        // Queued behind any pending invocation, so a clicked entry still runs before its handler is released.
        _ = _apartment.RunAsync(() =>
        {
            if (_released)
                return false;
            _released = true;
            _ = DestroyMenu(_popup);
            if (_handler3 != 0)
                Release(_handler3);
            if (_handler2 != 0)
                Release(_handler2);
            Release(_handler);
            return true;
        });
        _apartment.Close();
    }

    // ── Reading a menu ──

    private List<ShellMenuItem> Read(nint menu)
    {
        var total = GetMenuItemCount(menu);
        if (total <= 0)
            return [];

        var entries = new List<ShellMenuItem>(total);
        var label = stackalloc char[LabelCapacity];
        for (uint position = 0; position < (uint)total; position++)
        {
            MenuItemData data = default;
            data.Size = (uint)sizeof(MenuItemData);
            data.Mask = MiimFType | MiimState | MiimId | MiimSubmenu | MiimString | MiimBitmap;
            data.Text = label;
            data.TextCapacity = LabelCapacity - 1;
            if (GetMenuItemInfoW(menu, position, 1, &data) == 0)
                continue;

            var key = ++_lastKey;
            if ((data.Type & MftSeparator) != 0)
            {
                entries.Add(ShellMenuItem.Separator(key));
                continue;
            }

            var caption = WithoutAccelerators(new string(label, 0, (int)Math.Min(data.TextCapacity, LabelCapacity - 1)));
            if (caption.Length == 0)
                continue; // owner-drawn entries carry no text Navigator could show

            var hasCommand = data.Id >= FirstId && data.Id <= LastId;
            var verb = hasCommand ? ReadVerb(data.Id - FirstId) : null;
            if (verb is null && data.SubMenu != 0 && _sendToLabel.Length > 0 && string.Equals(caption, _sendToLabel, StringComparison.OrdinalIgnoreCase))
                verb = "sendto";
            if ((verb is not null && _hidden.Contains(verb)) || _hidden.Contains(caption))
                continue;

            if (data.SubMenu != 0)
                _children[key] = (data.SubMenu, position);
            else if (hasCommand)
                _commandOffsets[key] = data.Id - FirstId;
            else
                continue;

            entries.Add(new ShellMenuItem(key, caption, verb, false, (data.State & MfsDisabled) == 0, (data.State & MfsChecked) != 0,
                data.SubMenu != 0, MenuBitmaps.ToImage(data.Bitmap)));
        }

        return entries;
    }

    private string? ReadVerb(uint offset)
    {
        // Some handlers report offsets far outside their range and crash when asked about them (seen with GPU vendor entries).
        if (offset > 5000)
            return null;

        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        buffer[0] = '\0';
        var getCommandString = (delegate* unmanaged[Stdcall]<nint, nuint, uint, uint*, char*, uint, int>)(*(nint**)_handler)[5];
        if (getCommandString(_handler, offset, GcsVerbW, null, buffer, capacity) < 0)
            return null;
        var verb = new string(buffer);
        return verb.Length == 0 ? null : verb;
    }

    /// <summary>"Se&amp;nd to\tCtrl+N" → "Send to": access-key markers dropped ("&amp;&amp;" is a literal ampersand), shortcut text cut.</summary>
    internal static string WithoutAccelerators(string raw)
    {
        var tab = raw.IndexOf('\t', StringComparison.Ordinal);
        var span = tab >= 0 ? raw.AsSpan(0, tab) : raw.AsSpan();
        if (span.IndexOf('&') < 0)
            return span.Trim().ToString();

        var cleaned = new System.Text.StringBuilder(span.Length);
        for (var index = 0; index < span.Length; index++)
        {
            if (span[index] == '&')
            {
                if (index + 1 < span.Length && span[index + 1] == '&')
                {
                    cleaned.Append('&');
                    index++;
                }

                continue;
            }

            cleaned.Append(span[index]);
        }

        return cleaned.ToString().Trim();
    }

    // ── Binding ──

    private static nint BindItems(IReadOnlyList<string> paths)
    {
        var lists = new nint[paths.Count];
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                nint list;
                if (SHParseDisplayName(paths[i], 0, &list, 0, null) < 0 || list == 0)
                    return 0;
                lists[i] = list;
            }

            nint array;
            fixed (nint* first = lists)
            {
                if (SHCreateShellItemArrayFromIDLists((uint)lists.Length, first, &array) < 0 || array == 0)
                    return 0;
            }

            try
            {
                return BindToHandler(array, UiObjectHandler);
            }
            finally
            {
                Release(array);
            }
        }
        finally
        {
            foreach (var list in lists)
            {
                if (list != 0)
                    Marshal.FreeCoTaskMem(list);
            }
        }
    }

    private static nint BindFolderView(string folder)
    {
        nint list;
        if (SHParseDisplayName(folder, 0, &list, 0, null) < 0 || list == 0)
            return 0;
        try
        {
            nint item;
            var itemId = ShellItemId;
            if (SHCreateItemFromIDList(list, &itemId, &item) < 0 || item == 0)
                return 0;
            try
            {
                return BindToHandler(item, ViewObjectHandler);
            }
            finally
            {
                Release(item);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(list);
        }
    }

    /// <summary>IShellItem::BindToHandler and IShellItemArray::BindToHandler share vtable slot 3 and signature.</summary>
    private static nint BindToHandler(nint source, Guid handlerKind)
    {
        var kind = handlerKind;
        var wanted = ContextMenuId;
        nint result;
        var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(nint**)source)[3];
        return bind(source, 0, &kind, &wanted, &result) < 0 ? 0 : result;
    }

    private static nint QueryInterface(nint unknown, Guid interfaceId)
    {
        var wanted = interfaceId;
        nint result;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)unknown)[0];
        return query(unknown, &wanted, &result) < 0 ? 0 : result;
    }

    private static void Release(nint unknown)
        => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    private static string LoadShellString(uint id)
    {
        var module = GetModuleHandleW("shell32.dll");
        if (module == 0)
            return "";
        var buffer = stackalloc char[256];
        var length = LoadStringW(module, id, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>MENUITEMINFOW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemData
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint SubMenu;
        public nint CheckedBitmap;
        public nint UncheckedBitmap;
        public nuint ItemData;
        public char* Text;
        public uint TextCapacity;
        public nint Bitmap;
    }

    /// <summary>CMINVOKECOMMANDINFOEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public uint Size;
        public uint Mask;
        public nint Owner;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
        public nint Title;
        public nint VerbW;
        public nint ParametersW;
        public nint DirectoryW;
        public nint TitleW;
        public int InvokeX;
        public int InvokeY;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, nint* idList, uint attributesIn, uint* attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateShellItemArrayFromIDLists(uint count, nint* idLists, nint* itemArray);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateItemFromIDList(nint idList, Guid* interfaceId, nint* item);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemInfoW(nint menu, uint item, int byPosition, MenuItemData* data);

    [LibraryImport("user32.dll")]
    private static partial int LoadStringW(nint instance, uint id, char* buffer, int capacity);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string name);
}

/// <summary>Menu entry bitmaps (HBITMAP) → straight-alpha BGRA pixels for the UI.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MenuBitmaps
{
    public static SystemIconImage? ToImage(nint bitmap)
    {
        // 0 is "no bitmap"; -1 and 1…11 are the HBMMENU_* system placeholders, not real bitmaps.
        if (bitmap == 0 || (bitmap >= -1 && bitmap <= 11))
            return null;

        BitmapFacts facts;
        if (GetObjectW(bitmap, sizeof(BitmapFacts), &facts) == 0 || facts.Width is <= 0 or > 256 || facts.Height is <= 0 or > 256)
            return null;

        int width = facts.Width, height = facts.Height;
        var pixels = new byte[width * height * 4];
        var screen = GetDC(0);
        try
        {
            var header = stackalloc byte[40 + (256 * 4)]; // BITMAPINFOHEADER plus room for a color table
            new Span<byte>(header, 40 + (256 * 4)).Clear();
            *(uint*)header = 40;                // biSize
            *(int*)(header + 4) = width;        // biWidth
            *(int*)(header + 8) = -height;      // biHeight: negative = top-down rows
            *(ushort*)(header + 12) = 1;        // biPlanes
            *(ushort*)(header + 14) = 32;       // biBitCount
            fixed (byte* target = pixels)
            {
                if (GetDIBits(screen, bitmap, 0, (uint)height, target, header, 0) != height)
                    return null;
            }
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }

        if (HasAnyAlpha(pixels))
            Unpremultiply(pixels);
        else
            KeyOutBackground(pixels, width, height);
        return new SystemIconImage(width, height, pixels);
    }

    private static bool HasAnyAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return true;
        }

        return false;
    }

    /// <summary>Menu bitmaps with alpha are premultiplied DIB sections; the UI expects straight alpha.</summary>
    private static void Unpremultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255)
                continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }
    }

    /// <summary>Old-style bitmaps have no alpha: their bottom-left color is the background and becomes transparent.</summary>
    private static void KeyOutBackground(byte[] pixels, int width, int height)
    {
        var corner = (height - 1) * width * 4;
        byte blue = pixels[corner], green = pixels[corner + 1], red = pixels[corner + 2];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var isBackground = pixels[i] == blue && pixels[i + 1] == green && pixels[i + 2] == red;
            pixels[i + 3] = isBackground ? (byte)0 : (byte)255;
        }
    }

    /// <summary>BITMAP.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapFacts
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public nint Bits;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint deviceContext, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);
}
