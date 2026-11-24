using System.Text.Json.Nodes;
using Navigator.Core.Settings;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class SettingsTests
{
    [TestMethod]
    public void MissingFile_GivesDefaults()
    {
        using var temp = new TempDirectory();
        var result = new SettingsStore(temp.Combine("settings.json")).Load();
        Assert.AreEqual(SettingsLoadStatus.CreatedDefaults, result.Status);
        Assert.AreEqual(ThemeMode.System, result.Settings.Appearance.Theme);
    }

    [TestMethod]
    public void SaveAndLoad_RoundTrips()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Combine("settings.json"));
        var settings = new AppSettings();
        settings.Appearance.Theme = ThemeMode.Dark;
        settings.General.Language = "uk";
        settings.KeyBindings["tab.new"] = ["Ctrl+Alt+T"];
        store.Save(settings);

        var loaded = store.Load();
        Assert.AreEqual(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.AreEqual(ThemeMode.Dark, loaded.Settings.Appearance.Theme);
        Assert.AreEqual("uk", loaded.Settings.General.Language);
        Assert.AreEqual("Ctrl+Alt+T", loaded.Settings.KeyBindings["tab.new"][0]);
        StringAssert.Contains(File.ReadAllText(temp.Combine("settings.json")), "\"theme\": \"Dark\"");
    }

    [TestMethod]
    public void CorruptFile_IsPreservedAndBackupUsed()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        var store = new SettingsStore(path);
        var good = new AppSettings();
        good.Appearance.Theme = ThemeMode.Light;
        store.Save(good);
        store.Save(good); // creates settings.json.bak
        File.WriteAllText(path, "{ this is not json");

        var result = store.Load();
        Assert.AreEqual(SettingsLoadStatus.RecoveredFromBackup, result.Status);
        Assert.AreEqual(ThemeMode.Light, result.Settings.Appearance.Theme);
        Assert.IsNotNull(result.PreservedCopyPath);
        Assert.IsTrue(File.Exists(result.PreservedCopyPath));
    }

    [TestMethod]
    public void CorruptWithoutBackup_ResetsToDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", "[1,2,3]");
        var result = new SettingsStore(path).Load();
        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, result.Status);
    }

    private sealed class RenameThemeMigration : ISettingsMigration
    {
        public int FromVersion => 0;

        public void Apply(JsonObject root) => root["appearance"] = new JsonObject { ["theme"] = root["legacyTheme"]?.GetValue<string>() };
    }

    [TestMethod]
    public void Migrations_RunForOlderSchemas()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """{ "schemaVersion": 0, "legacyTheme": "Dark" }""");
        var result = new SettingsStore(path, [new RenameThemeMigration()]).Load();
        Assert.AreEqual(SettingsLoadStatus.Migrated, result.Status);
        Assert.AreEqual(ThemeMode.Dark, result.Settings.Appearance.Theme);
    }

    [TestMethod]
    public void NewerSchema_IsLoadedTolerantly()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """{ "schemaVersion": 99, "futureOption": true, "general": { "language": "uk" } }""");
        var result = new SettingsStore(path).Load();
        Assert.AreEqual(SettingsLoadStatus.NewerSchema, result.Status);
        Assert.AreEqual("uk", result.Settings.General.Language);
    }

    [TestMethod]
    public void Sanitize_ClampsHandEditedValues()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """{ "schemaVersion": 1, "appearance": { "transparency": 7, "fontScale": 0.1 }, "operations": { "maxConcurrentOperations": 500 }, "sidebar": null }""");
        var settings = new SettingsStore(path).Load().Settings;
        Assert.AreEqual(1.0, settings.Appearance.Transparency);
        Assert.AreEqual(0.75, settings.Appearance.FontScale);
        Assert.AreEqual(8, settings.Operations.MaxConcurrentOperations);
        Assert.IsNotNull(settings.Sidebar);
    }

    [TestMethod]
    public void ImportRejectsInvalidFiles()
    {
        using var temp = new TempDirectory();
        var bad = temp.CreateFile("bad.json", "not json");
        Assert.ThrowsExactly<InvalidDataException>(() => new SettingsStore(temp.Combine("s.json")).Import(bad));
    }

    [TestMethod]
    public void SessionStore_SurvivesCorruption()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("session.json", "{{{");
        var store = new JsonFileStore<SessionState>(path, SettingsJsonContext.Default.SessionState, () => new SessionState());
        Assert.AreEqual(0, store.Load().Windows.Count);
        store.Save(new SessionState { Windows = [new WindowSession { Tabs = [new TabSession { Location = "folder:/x" }] }] });
        Assert.AreEqual("folder:/x", store.Load().Windows[0].Tabs[0].Location);
    }
}
