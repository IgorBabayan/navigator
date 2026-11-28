using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.Platform;
using Navigator.Platform.Shell;

namespace Navigator.Platform.Launch;

/// <summary>
/// The applications Explorer lists under "Open with" for a file type (SHAssocEnumHandlers, recommended handlers), and
/// launching one of them through its own IAssocHandler, so packaged (Store) apps and programs with DDE or unusual
/// command lines start exactly as they do from Explorer.
/// <para>
/// Each call runs on its own short-lived STA thread (<see cref="ShellApartment"/>), never on the UI thread: handler
/// enumeration loads association data and may touch the network for packaged apps. Interfaces are called through their
/// vtables like the rest of the shell interop, so no built-in COM interop is needed (trimming / AOT safe).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsAssocHandlers
{
    /// <summary>Recommended applications for the file's type, the default one first. Empty when there are none.</summary>
    public static async Task<IReadOnlyList<AppHandler>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension) || cancellationToken.IsCancellationRequested)
            return [];

        var apartment = new ShellApartment();
        try
        {
            var work = apartment.RunAsync<IReadOnlyList<AppHandler>>(() => cancellationToken.IsCancellationRequested ? [] : Enumerate(extension));
            apartment.Close(); // ends the thread once the queued work is done
            return await work.ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is COMException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
        finally
        {
            apartment.Close();
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> with the handler whose <see cref="AppHandler.Id"/> came from <see cref="ListAsync"/>.
    /// Null when no such handler is registered any more (the caller then starts the program directly).
    /// </summary>
    public static async Task<bool?> InvokeAsync(string path, AppHandler handler)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return null;

        var apartment = new ShellApartment();
        try
        {
            var work = apartment.RunAsync(() => Invoke(extension, path, handler.Id));
            apartment.Close();
            return await work.ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is COMException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            apartment.Close();
        }
    }
}

/// <summary>The native side of <see cref="WindowsAssocHandlers"/>; everything here runs on the apartment thread.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsAssocHandlers
{
    private const int AssocFilterRecommended = 0x1;
    private const uint AssocInitIgnoreUnknown = 0x400; // ASSOCF_INIT_IGNOREUNKNOWN
    private const int AssocStrExecutable = 2;
    private const int AssocStrFriendlyAppName = 4;
    private const int MaxHandlers = 32;

    // IAssocHandler vtable slots (after IUnknown's three).
    private const int SlotGetName = 3;
    private const int SlotGetUiName = 4;
    private const int SlotGetIconLocation = 5;
    private const int SlotInvoke = 8;

    // ── Apartment thread only ──

    private static List<AppHandler> Enumerate(string extension)
    {
        var found = new List<AppHandler>();
        var defaultExecutable = QueryAssociation(extension, AssocStrExecutable);
        var defaultName = QueryAssociation(extension, AssocStrFriendlyAppName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ForEachHandler(extension, handler =>
        {
            var id = ReadString(handler, SlotGetName);
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
                return true;

            var display = ReadString(handler, SlotGetUiName);
            if (string.IsNullOrWhiteSpace(display))
                display = Path.GetFileNameWithoutExtension(id);

            var isDefault = (defaultExecutable is not null && string.Equals(defaultExecutable, id, StringComparison.OrdinalIgnoreCase))
                || (defaultName is not null && string.Equals(defaultName, display, StringComparison.OrdinalIgnoreCase));
            found.Add(new AppHandler(id, display, IconFileOf(handler), isDefault));
            return found.Count < MaxHandlers;
        });

        // The default application first, the rest in the shell's own (most recently used) order.
        return [.. found.OrderByDescending(h => h.IsDefault)];
    }

    private static bool? Invoke(string extension, string path, string id)
    {
        bool? outcome = null;
        ForEachHandler(extension, handler =>
        {
            if (!string.Equals(ReadString(handler, SlotGetName), id, StringComparison.OrdinalIgnoreCase))
                return true;

            var data = NativeShellMenu.DataObjectFor(path);
            if (data == 0)
            {
                outcome = false;
                return false;
            }

            try
            {
                var invoke = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)handler)[SlotInvoke];
                outcome = invoke(handler, data) >= 0;
            }
            finally
            {
                Release(data);
            }

            return false;
        });
        return outcome;
    }

    /// <summary>Walks the recommended handlers of <paramref name="extension"/> until <paramref name="visit"/> returns false.</summary>
    private static void ForEachHandler(string extension, Func<nint, bool> visit)
    {
        nint enumerator;
        if (SHAssocEnumHandlers(extension, AssocFilterRecommended, &enumerator) < 0 || enumerator == 0)
            return;

        try
        {
            // IEnumAssocHandlers::Next(celt, rgelt, pceltFetched): S_OK (0) while there are more.
            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)(*(nint**)enumerator)[3];
            for (var guard = 0; guard < 256; guard++)
            {
                nint handler = 0;
                uint fetched = 0;
                if (next(enumerator, 1, &handler, &fetched) != 0 || fetched == 0 || handler == 0)
                    break;
                try
                {
                    if (!visit(handler))
                        break;
                }
                finally
                {
                    Release(handler);
                }
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>The handler's icon file when it is a plain file (program); packaged apps report resource strings instead.</summary>
    private static string? IconFileOf(nint handler)
    {
        nint text = 0;
        int index;
        var getIconLocation = (delegate* unmanaged[Stdcall]<nint, nint*, int*, int>)(*(nint**)handler)[SlotGetIconLocation];
        if (getIconLocation(handler, &text, &index) < 0 || text == 0)
            return null;
        try
        {
            var location = Environment.ExpandEnvironmentVariables(Marshal.PtrToStringUni(text) ?? string.Empty).Trim('"');
            return location.Length > 0 && location[0] != '@' && Path.IsPathFullyQualified(location) && File.Exists(location) ? location : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>IAssocHandler::GetName / GetUIName: a CoTaskMem string the caller frees.</summary>
    private static string? ReadString(nint handler, int slot)
    {
        nint text = 0;
        var read = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)handler)[slot];
        if (read(handler, &text) < 0 || text == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string? QueryAssociation(string extension, int what)
    {
        const int Capacity = 1024;
        var buffer = stackalloc char[Capacity];
        var size = (uint)Capacity;
        if (AssocQueryStringW(AssocInitIgnoreUnknown, what, extension, null, buffer, &size) < 0 || size <= 1)
            return null;
        var text = new string(buffer, 0, (int)Math.Min(size - 1, Capacity - 1));
        return text.Length == 0 ? null : text;
    }

    private static void Release(nint unknown)
        => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHAssocEnumHandlers(string extension, int filter, nint* enumerator);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryStringW(uint flags, int what, string association, string? extra, char* output, uint* outputLength);
}
