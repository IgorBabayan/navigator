using System.Text.Json;
using Navigator.Core.IO;
using Navigator.Core.Settings;

namespace Navigator.Presentation.Services;

/// <summary>Files recently opened through Navigator (Home → Recent files). Bounded, most recent first.</summary>
public sealed class RecentItems
{
    public const int Capacity = 40;
    private readonly Lock _gate = new();
    private readonly string? _filePath;
    private readonly List<string> _items;

    public RecentItems(string? filePath)
    {
        _filePath = filePath;
        _items = Load(filePath);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
                return [.. _items];
        }
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            _items.RemoveAll(p => PathUtil.AreEqual(p, path));
            _items.Insert(0, path);
            if (_items.Count > Capacity)
                _items.RemoveRange(Capacity, _items.Count - Capacity);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string path)
    {
        lock (_gate)
            _items.RemoveAll(p => PathUtil.AreEqual(p, path));
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        if (_filePath is null)
            return;
        try
        {
            string json;
            lock (_gate)
                json = JsonSerializer.Serialize(_items, PresentationJsonContext.Default.ListString);
            AtomicFile.WriteAllText(_filePath, json, keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recent files are a convenience; failing to persist them is not an error worth surfacing.
        }
    }

    private static List<string> Load(string? filePath)
    {
        try
        {
            if (filePath is not null && File.Exists(filePath))
                return JsonSerializer.Deserialize(File.ReadAllText(filePath), PresentationJsonContext.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return [];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class PresentationJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
