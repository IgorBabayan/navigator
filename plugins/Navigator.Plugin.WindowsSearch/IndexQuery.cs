using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Navigator.Core.IO;

namespace Navigator.Plugin.WindowsSearch;

/// <summary>A failed call into the Windows Search provider (service stopped, malformed query…).</summary>
internal sealed class IndexQueryException : IOException
{
    public IndexQueryException()
    {
    }

    public IndexQueryException(string message)
        : base(message)
    {
    }

    public IndexQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public IndexQueryException(string step, int hresult)
        : base($"Windows Search: {step} failed (0x{hresult:X8}).")
    {
        HResult = hresult;
    }
}

/// <summary>
/// Runs one Windows Search SQL query through OLE DB: IDataInitialize → IDBInitialize → IDBCreateSession →
/// IDBCreateCommand → ICommandText → IRowset, reading the rows through an accessor that binds the five columns of
/// <see cref="SearchSql.Columns"/> into one fixed buffer (no per-row allocation besides the path string). Interfaces are
/// called through their vtables, as elsewhere in Navigator (no built-in COM interop). Must run on a thread that entered
/// the multithreaded apartment (<see cref="RunOnWorker"/>); 64-bit processes only (the OLE DB structures are laid out
/// for 64 bits).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class IndexQuery
{
    private const string ConnectionString = "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';";

    private const int ClsctxInprocServer = 0x1;
    private const int CoinitMultithreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int DbSEndOfRowset = 0x00040EC6;
    private const uint DbAccessorRowData = 0x2;
    private const uint DbPartAll = 0x1 | 0x2 | 0x4; // value, length, status
    private const ushort DbTypeWStr = 130, DbTypeI8 = 20, DbTypeFileTime = 64, DbTypeUI4 = 19;
    private const uint DbStatusOk = 0;
    private const int PathCapacity = 1024; // characters; longer paths come back truncated and are skipped
    private const int RowsPerFetch = 128;
    private const int DirectoryAttribute = 0x10;

    private static readonly Guid DataInitializeClass = new("2206CDB0-19C1-11D1-89E0-00C04FD7A829"); // MSDAINITIALIZE
    private static readonly Guid IidDataInitialize = new("2206CCB1-19C1-11D1-89E0-00C04FD7A829");
    private static readonly Guid IidDbInitialize = new("0C733A8B-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidDbCreateSession = new("0C733A5D-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidDbCreateCommand = new("0C733A1D-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidCommandText = new("0C733A27-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidAccessor = new("0C733A8C-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid DbGuidDefault = new("C8B521FB-5CF3-11CE-ADE5-00AA0044773D");

    /// <summary>Layout of one bound column inside the row buffer.</summary>
    private readonly record struct Column(nuint Status, nuint Length, nuint Value, nuint MaxLength, ushort Type);

    private static readonly Column[] Layout = BuildLayout(out RowSize);
    private static readonly nuint RowSize;

    public static bool IsSupported => OperatingSystem.IsWindows() && Environment.Is64BitProcess;

    /// <summary>
    /// Runs <paramref name="work"/> on a new background thread inside the multithreaded COM apartment; the result is
    /// the task's. One thread per query keeps a stalled provider from blocking the next search.
    /// </summary>
    public static Task<T> RunOnWorker<T>(Func<T> work)
    {
        var outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(0, CoinitMultithreaded);
            try
            {
                if (initialized < 0 && initialized != RpcEChangedMode)
                    throw new IndexQueryException("CoInitializeEx", initialized);
                outcome.TrySetResult(work());
            }
            catch (Exception failure)
            {
                // Boundary of the worker thread: the failure belongs to the caller.
                outcome.TrySetException(failure);
            }
            finally
            {
                if (initialized >= 0)
                    CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "Navigator Windows Search",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return outcome.Task;
    }

    /// <summary>
    /// Executes <paramref name="sql"/> and hands every row to <paramref name="row"/>; stops when it returns false or
    /// <paramref name="cancellationToken"/> is cancelled. Call on a <see cref="RunOnWorker"/> thread.
    /// </summary>
    public static void Execute(string sql, Func<FileEntry, bool> row, CancellationToken cancellationToken)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The Windows Search index is available to 64-bit Navigator on Windows only.");
        nint factory = 0, source = 0, sessionFactory = 0, commandFactory = 0, command = 0, rowset = 0, accessor = 0;
        nuint accessorHandle = 0;
        var initialized = false;
        try
        {
            var dataInitializeClass = DataInitializeClass;
            var iidDataInitialize = IidDataInitialize;
            Check(CoCreateInstance(&dataInitializeClass, 0, ClsctxInprocServer, &iidDataInitialize, &factory), "IDataInitialize");

            // IDataInitialize::GetDataSource(outer, clsctx, initialization string, riid, out source)
            var iidDbInitialize = IidDbInitialize;
            fixed (char* connection = ConnectionString)
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int, char*, Guid*, nint*, int>)Slot(factory, 3))(factory, 0, ClsctxInprocServer, connection, &iidDbInitialize, &source), "GetDataSource");

            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(source, 3))(source), "IDBInitialize::Initialize");
            initialized = true;

            sessionFactory = Query(source, IidDbCreateSession, "IDBCreateSession");
            var iidCreateCommand = IidDbCreateCommand;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(sessionFactory, 3))(sessionFactory, 0, &iidCreateCommand, &commandFactory), "CreateSession");

            var iidCommandText = IidCommandText;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(commandFactory, 3))(commandFactory, 0, &iidCommandText, &command), "CreateCommand");

            // ICommandText::SetCommandText(dialect, text) — slot 7 (ICommand: Cancel 3, Execute 4, GetDBSession 5; GetCommandText 6)
            var dialect = DbGuidDefault;
            fixed (char* text = sql)
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, char*, int>)Slot(command, 7))(command, &dialect, text), "SetCommandText");

            // ICommand::Execute(outer, riid, params, rows affected, out rowset)
            var iidRowset = IidRowset;
            nint affected = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint, nint*, nint*, int>)Slot(command, 4))(command, 0, &iidRowset, 0, &affected, &rowset), "Execute");
            if (rowset == 0)
                return; // no rows at all

            accessor = Query(rowset, IidAccessor, "IAccessor");
            accessorHandle = CreateAccessor(accessor);
            ReadRows(rowset, accessorHandle, row, cancellationToken);
        }
        finally
        {
            if (accessorHandle != 0)
                _ = ((delegate* unmanaged[Stdcall]<nint, nuint, uint*, int>)Slot(accessor, 6))(accessor, accessorHandle, null); // ReleaseAccessor
            Release(accessor);
            Release(rowset);
            Release(command);
            Release(commandFactory);
            Release(sessionFactory);
            if (initialized)
                _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(source, 4))(source); // Uninitialize
            Release(source);
            Release(factory);
        }
    }

    private static void ReadRows(nint rowset, nuint accessor, Func<FileEntry, bool> row, CancellationToken cancellationToken)
    {
        var handles = stackalloc nuint[RowsPerFetch];
        var buffer = (byte*)NativeMemory.Alloc(RowSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // IRowset::GetNextRows(chapter, offset, count, out obtained, in/out handles) — slot 5, consumer-allocated handles
                nuint obtained = 0;
                var handleArray = handles;
                var result = ((delegate* unmanaged[Stdcall]<nint, nuint, nint, nint, nuint*, nuint**, int>)Slot(rowset, 5))(rowset, 0, 0, RowsPerFetch, &obtained, &handleArray);
                if (result < 0)
                    throw new IndexQueryException("GetNextRows", result);
                try
                {
                    for (nuint i = 0; i < obtained; i++)
                    {
                        // IRowset::GetData(row, accessor, buffer) — slot 4
                        NativeMemory.Clear(buffer, RowSize);
                        if (((delegate* unmanaged[Stdcall]<nint, nuint, nuint, byte*, int>)Slot(rowset, 4))(rowset, handleArray[i], accessor, buffer) < 0)
                            continue;
                        if (ToEntry(buffer) is { } entry && !row(entry))
                            return;
                    }
                }
                finally
                {
                    if (obtained > 0) // IRowset::ReleaseRows(count, handles, options, ref counts, statuses) — slot 6
                        _ = ((delegate* unmanaged[Stdcall]<nint, nuint, nuint*, uint*, uint*, uint*, int>)Slot(rowset, 6))(rowset, obtained, handleArray, null, null, null);
                }

                if (result == DbSEndOfRowset || obtained < RowsPerFetch)
                    return;
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static FileEntry? ToEntry(byte* row)
    {
        var path = Layout[0];
        if (*(uint*)(row + path.Status) != DbStatusOk)
            return null; // null, truncated or not convertible
        var length = (int)Math.Min(*(nuint*)(row + path.Length), path.MaxLength) / sizeof(char);
        var text = new string((char*)(row + path.Value), 0, length);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        if (end >= 0)
            text = text[..end];
        if (text.Length < 3)
            return null;

        var size = IsSet(row, Layout[1]) ? *(long*)(row + Layout[1].Value) : 0;
        var modified = IsSet(row, Layout[2]) ? FromFileTime(*(long*)(row + Layout[2].Value)) : DateTime.MinValue;
        var created = IsSet(row, Layout[3]) ? FromFileTime(*(long*)(row + Layout[3].Value)) : DateTime.MinValue;
        var attributes = IsSet(row, Layout[4]) ? (int)*(uint*)(row + Layout[4].Value) : 0;
        var isDirectory = (attributes & DirectoryAttribute) != 0;
        return new FileEntry(Path.GetFileName(text), text, isDirectory, isDirectory ? -1 : Math.Max(0, size), created, modified, (FileAttributes)attributes);
    }

    private static bool IsSet(byte* row, Column column) => *(uint*)(row + column.Status) == DbStatusOk;

    private static DateTime FromFileTime(long value)
    {
        try
        {
            return value <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static nuint CreateAccessor(nint accessor)
    {
        var bindings = stackalloc DbBinding[Layout.Length];
        var statuses = stackalloc uint[Layout.Length];
        for (var i = 0; i < Layout.Length; i++)
        {
            var column = Layout[i];
            bindings[i] = new DbBinding
            {
                Ordinal = (nuint)(i + 1),
                ValueOffset = column.Value,
                LengthOffset = column.Length,
                StatusOffset = column.Status,
                Part = DbPartAll,
                MaxLength = column.MaxLength,
                Type = column.Type,
            };
        }

        nuint handle = 0;
        // IAccessor::CreateAccessor(flags, count, bindings, row size, out handle, statuses) — slot 4
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nuint, DbBinding*, nuint, nuint*, uint*, int>)Slot(accessor, 4))(accessor, DbAccessorRowData, (nuint)Layout.Length, bindings, RowSize, &handle, statuses), "CreateAccessor");
        return handle;
    }

    private static Column[] BuildLayout(out nuint rowSize)
    {
        var columns = new[] { (DbTypeWStr, (nuint)(PathCapacity * sizeof(char))), (DbTypeI8, (nuint)8), (DbTypeFileTime, (nuint)8), (DbTypeFileTime, (nuint)8), (DbTypeUI4, (nuint)4) };
        var layout = new Column[columns.Length];
        nuint offset = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            var (type, size) = columns[i];
            var status = offset;
            var length = status + 8;
            var value = length + 8;
            layout[i] = new Column(status, length, value, size, type);
            offset = (value + size + 7) & ~(nuint)7;
        }

        rowSize = offset;
        return layout;
    }

    /// <summary>DBBINDING with the 64-bit (8-byte packed) layout of oledb.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DbBinding
    {
        public nuint Ordinal;
        public nuint ValueOffset;
        public nuint LengthOffset;
        public nuint StatusOffset;
        public nint TypeInfo;
        public nint Object;
        public nint BindExtension;
        public uint Part;
        public uint MemoryOwner;
        public uint ParameterIo;
        public nuint MaxLength;
        public uint Flags;
        public ushort Type;
        public byte Precision;
        public byte Scale;
    }

    private static nint Query(nint unknown, Guid iid, string step)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(unknown, 0))(unknown, &iid, &result), step);
        return result;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Release(nint unknown)
    {
        if (unknown != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);
    }

    private static void Check(int hresult, string step)
    {
        if (hresult < 0)
            throw new IndexQueryException(step, hresult);
    }

    [LibraryImport("ole32")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, int context, Guid* iid, nint* instance);
}
