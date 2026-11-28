using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Navigator.Core.Settings;

/// <summary>Transforms the raw JSON of schema version <see cref="FromVersion"/> into version FromVersion + 1.</summary>
public interface ISettingsMigration
{
    int FromVersion { get; }

    void Apply(JsonObject root);
}

public enum SettingsLoadStatus
{
    Loaded,
    CreatedDefaults,
    Migrated,
    RecoveredFromBackup,
    ResetAfterCorruption,
    NewerSchema,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? PreservedCopyPath = null);

/// <summary>
/// Loads and saves <see cref="AppSettings"/> with schema migrations and corruption recovery.
/// A corrupt file is never silently discarded: it is preserved next to the original with a timestamp.
/// </summary>
public sealed class SettingsStore
{
    private readonly IReadOnlyList<ISettingsMigration> _migrations;

    public SettingsStore(string filePath, IReadOnlyList<ISettingsMigration>? migrations = null)
    {
        FilePath = filePath;
        _migrations = migrations ?? [];
    }

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.CreatedDefaults);

        if (TryRead(FilePath, out var settings, out var status))
            return new SettingsLoadResult(settings, status);

        var preserved = PreserveCorruptFile(FilePath);
        var backup = FilePath + ".bak";
        if (File.Exists(backup) && TryRead(backup, out var fromBackup, out _))
            return new SettingsLoadResult(fromBackup, SettingsLoadStatus.RecoveredFromBackup, preserved);

        return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.ResetAfterCorruption, preserved);
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        AtomicFile.WriteAllText(FilePath, Serialize(settings));
    }

    public static string Serialize(AppSettings settings)
        => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    public static void Export(AppSettings settings, string destinationPath)
        => AtomicFile.WriteAllText(destinationPath, Serialize(settings), keepBackup: false);

    /// <summary>Reads settings from an exported file. Throws <see cref="InvalidDataException"/> if it is not a valid settings file.</summary>
    public AppSettings Import(string sourcePath)
    {
        if (!TryRead(sourcePath, out var settings, out _))
            throw new InvalidDataException($"'{sourcePath}' is not a valid Navigator settings file.");
        return settings;
    }

    private bool TryRead(string path, out AppSettings settings, out SettingsLoadStatus status)
    {
        settings = null!;
        status = SettingsLoadStatus.Loaded;
        try
        {
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (node is not JsonObject root)
                return false;

            var version = ReadVersion(root);
            if (version > AppSettings.CurrentSchemaVersion)
            {
                status = SettingsLoadStatus.NewerSchema;
            }
            else if (version < AppSettings.CurrentSchemaVersion)
            {
                Migrate(root, version);
                status = SettingsLoadStatus.Migrated;
            }

            var result = root.Deserialize(SettingsJsonContext.Default.AppSettings);
            if (result is null)
                return false;

            Sanitize(result);
            settings = result;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private void Migrate(JsonObject root, int fromVersion)
    {
        for (var version = fromVersion; version < AppSettings.CurrentSchemaVersion; version++)
        {
            var migration = _migrations.FirstOrDefault(m => m.FromVersion == version);
            migration?.Apply(root);
            root["schemaVersion"] = version + 1;
        }
    }

    private static int ReadVersion(JsonObject root)
    {
        var node = root["schemaVersion"];
        return node is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
    }

    /// <summary>Clamps values that would break the UI if a user hand-edited the file.</summary>
    public static void Sanitize(AppSettings settings)
    {
        settings.Appearance ??= new AppearanceSettings();
        settings.General ??= new GeneralSettings();
        settings.Sidebar ??= new SidebarSettings();
        settings.FilesAndFolders ??= new FilesAndFoldersSettings();
        settings.Operations ??= new FileOperationSettings();
        settings.Search ??= new SearchSettings();
        settings.Performance ??= new PerformanceSettings();
        settings.Updates ??= new UpdateSettings();
        settings.Advanced ??= new AdvancedSettings();
        settings.KeyBindings ??= [];
        settings.Tags ??= [];

        var appearance = settings.Appearance;
        appearance.Transparency = Math.Clamp(appearance.Transparency, 0, 1);
        appearance.BackgroundImageOpacity = Math.Clamp(appearance.BackgroundImageOpacity, 0, 1);
        appearance.FontScale = Math.Clamp(appearance.FontScale, 0.75, 2.0);
        appearance.ToolbarItems ??= [];
        appearance.ToolbarItems.RemoveAll(item => item is null || string.IsNullOrWhiteSpace(item.Id));
        if (appearance.ToolbarItems.Count > 64)
            appearance.ToolbarItems.RemoveRange(64, appearance.ToolbarItems.Count - 64);
        settings.Sidebar.Width = Math.Clamp(settings.Sidebar.Width, 160, 480);
        settings.Operations.MaxConcurrentOperations = Math.Clamp(settings.Operations.MaxConcurrentOperations, 1, 8);
        settings.Search.MaxResults = Math.Clamp(settings.Search.MaxResults, 100, 100_000);
        settings.Performance.ThumbnailMemoryCacheMegabytes = Math.Clamp(settings.Performance.ThumbnailMemoryCacheMegabytes, 16, 2048);
        settings.Performance.ThumbnailDiskCacheMegabytes = Math.Clamp(settings.Performance.ThumbnailDiskCacheMegabytes, 0, 16384);
        settings.Performance.DirectoryCacheEntries = Math.Clamp(settings.Performance.DirectoryCacheEntries, 0, 256);
        settings.Performance.MaxBackgroundWorkers = Math.Clamp(settings.Performance.MaxBackgroundWorkers, 1, Environment.ProcessorCount);
        settings.Updates.CheckIntervalHours = Math.Clamp(settings.Updates.CheckIntervalHours, 0, 24 * 30);
        settings.FilesAndFolders.FolderViews ??= [];
        if (!Enum.IsDefined(settings.FilesAndFolders.QuickLook))
            settings.FilesAndFolders.QuickLook = Platform.QuickLookProvider.Automatic;
        settings.FilesAndFolders.Columns ??= new FilesAndFoldersSettings().Columns;
        settings.Search.History ??= [];
        if (settings.Search.History.Count > 50)
            settings.Search.History.RemoveRange(50, settings.Search.History.Count - 50);
        settings.General.StartupLocations ??= [];
        settings.General.AddressHistory ??= [];
        if (settings.General.AddressHistory.Count > 25)
            settings.General.AddressHistory.RemoveRange(25, settings.General.AddressHistory.Count - 25);
        settings.Sidebar.CollapsedSections ??= [];
    }

    private static string? PreserveCorruptFile(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var preserved = $"{path}.corrupt-{stamp}";
            File.Copy(path, preserved, overwrite: true);
            return preserved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
