using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Navigator.Core.Archives;
using Navigator.Core.IO;
using Navigator.Core.Platform;
using Navigator.Core.Settings;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Menus;

namespace Navigator.UnitTests;

[TestClass]
public sealed class PaneMenuTests
{
    private static readonly Localizer Text = new();
    private static readonly ItemDisplayContext Display = new(Text, showExtensions: true);

    /// <summary>A command whose availability the test decides; records executions.</summary>
    private sealed class Probe(bool runnable = true) : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => runnable;

        public void Execute(object? parameter) => Runs++;
    }

    private static readonly Probe Blocked = new(runnable: false);

    private static PaneMenuCommands Commands(Probe? everything = null)
    {
        var any = everything ?? new Probe();
        return new PaneMenuCommands
        {
            Open = any, OpenInNewTab = any, OpenInNewWindow = any, OpenInOtherPane = any, OpenFileLocation = any,
            RunAsAdministrator = any, RunAsOtherUser = any, Cut = any, Copy = any, Paste = any, PasteIntoFolder = any,
            CopyPath = any, CreateFolderWithSelection = any, CreateShortcut = any, Rename = any, Delete = any,
            DeletePermanently = any, Properties = any, Restore = any, Download = any, Upload = any, TogglePin = any,
            Compress = any, CompressEach = any, ExtractFiles = any, ExtractSmart = any, ExtractHere = any,
            ExtractToFolder = any, OpenInCodeEditor = any, OpenTerminal = any, ToggleTag = any, RemoveTag = any, Duplicate = any,
            RevealInSystem = any, SetLayout = any, SortBy = any, ToggleSortDirection = any, SetGroupBy = any,
            Refresh = any, NewFolder = any, NewFile = any, EmptyTrash = any, SelectAll = any,
        };
    }

    private static Task<IReadOnlyList<MenuEntry>> Nothing(CancellationToken token) => Task.FromResult<IReadOnlyList<MenuEntry>>([]);

    private static FileItemViewModel File(string name) => new(new FileEntry(name, "/data/" + name, false, 1, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal), Display);

    private static FileItemViewModel Folder(string name) => new(new FileEntry(name, "/data/" + name, true, 0, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Directory), Display);

    private static ElevationOptions WindowsRules(string path)
        => Path.GetExtension(path) is ".exe" or ".msi" ? ElevationOptions.Administrator | ElevationOptions.OtherUser : ElevationOptions.None;

    private static PaneMenuInput Input(FileItemViewModel[] items, MenuScope scope = MenuScope.Folder, bool shift = false, GeneralSettings? settings = null, PaneMenuCommands? commands = null)
        => new()
        {
            Text = Text,
            Commands = commands ?? Commands(),
            Settings = settings ?? new GeneralSettings(),
            Scope = scope,
            ShiftHeld = shift,
            CanCreate = scope == MenuScope.Folder,
            Selection = SelectionTraits.Collect(items, ArchiveService.IsSupportedArchive, WindowsRules),
            OpenWithEntries = Nothing,
            SendToEntries = Nothing,
        };

    private static List<string> Labels(IEnumerable<MenuEntry> entries) => [.. entries.Where(e => !e.IsSeparator).Select(e => e.Label)];

    private static List<string> Rows(List<MenuEntry> entries) => Labels(entries.Where(e => !e.InStrip));

    private static MenuEntry? Slot(List<MenuEntry> entries, string slot) => entries.Find(e => e.Slot == slot);

    [TestMethod]
    public void Program_RunsElevated_AndHasNoOpenWith()
    {
        var rows = Rows(PaneMenuComposer.ForItems(Input([File("setup.exe")])));

        CollectionAssert.Contains(rows, Text["Command.RunAsAdministrator"]);
        CollectionAssert.Contains(rows, Text["Command.RunAsOtherUser"]);
        CollectionAssert.DoesNotContain(rows, Text["Command.OpenWithMenu"]);
    }

    [TestMethod]
    public void Document_GetsOpenWith_AndNoElevation()
    {
        var menu = PaneMenuComposer.ForItems(Input([File("report.pdf")]));

        Assert.IsNotNull(Slot(menu, MenuSlots.OpenWith)?.LoadChildren);
        CollectionAssert.DoesNotContain(Rows(menu), Text["Command.RunAsAdministrator"]);
    }

    [TestMethod]
    public void Shortcut_GetsNeitherOpenWithNorAnotherShortcut()
    {
        var rows = Rows(PaneMenuComposer.ForItems(Input([File("tool.lnk")])));

        CollectionAssert.DoesNotContain(rows, Text["Command.OpenWithMenu"]);
        CollectionAssert.DoesNotContain(rows, Text["Command.CreateShortcut"]);
    }

    [TestMethod]
    public void Folder_OpensElsewhere_AndPinLabelFollowsState()
    {
        var rows = Rows(PaneMenuComposer.ForItems(Input([Folder("Projects")])));
        CollectionAssert.Contains(rows, Text["Command.OpenInNewTab"]);
        CollectionAssert.Contains(rows, Text["Command.OpenInOtherPane"]);
        CollectionAssert.Contains(rows, Text["Command.PinToSidebar"]);

        var pinned = Rows(PaneMenuComposer.ForItems(Input([Folder("Projects")]) with { IsPinned = true }));
        CollectionAssert.Contains(pinned, Text["Command.UnpinFromSidebar"]);
    }

    [TestMethod]
    public void Archive_HasAllFourExtractions_MixedSelectionDropsOwnFolder()
    {
        var single = PaneMenuComposer.ForItems(Input([File("photos.zip")]) with { ExtractFolderName = "photos" });
        var extract = single.Single(e => e.Label == Text["Command.ExtractMenu"]);
        Assert.AreEqual(4, extract.Children!.Count);
        Assert.AreEqual(Text.Format("Command.ExtractToFolder", "photos"), extract.Children[3].Label);

        var mixed = PaneMenuComposer.ForItems(Input([File("photos.zip"), File("notes.txt")]));
        Assert.AreEqual(3, mixed.Single(e => e.Label == Text["Command.ExtractMenu"]).Children!.Count);
    }

    [TestMethod]
    public void Strip_KeepsItsShape_AndDisablesWhatCannotRun()
    {
        var commands = Commands() with { Paste = Blocked, PasteIntoFolder = Blocked };
        var strip = PaneMenuComposer.ForItems(Input([File("a.txt")], commands: commands)).Where(e => e.InStrip).ToList();

        CollectionAssert.AreEqual(
            new[] { "Command.Cut", "Command.Copy", "Command.Paste", "Command.Rename", "Command.Delete", "Command.Properties" }.Select(k => Text[k]).ToList(),
            Labels(strip));
        Assert.IsFalse(strip.Single(e => e.Label == Text["Command.Paste"]).IsEnabled);
    }

    [TestMethod]
    public void HiddenCommands_AreLeftOut()
    {
        var commands = Commands() with { RunAsAdministrator = Blocked, CreateShortcut = Blocked };
        var rows = Rows(PaneMenuComposer.ForItems(Input([File("setup.exe")], commands: commands)));

        CollectionAssert.DoesNotContain(rows, Text["Command.RunAsAdministrator"]);
        CollectionAssert.DoesNotContain(rows, Text["Command.CreateShortcut"]);
        CollectionAssert.Contains(rows, Text["Command.RunAsOtherUser"]);
    }

    [TestMethod]
    public void WithoutShift_RareEntriesWaitUnderMoreOptions()
    {
        var menu = PaneMenuComposer.ForItems(Input([File("a.txt")]));
        var more = Slot(menu, MenuSlots.MoreOptions);

        Assert.IsNotNull(more);
        CollectionAssert.Contains(Labels(more.Children!), Text["Command.Duplicate"]);
        CollectionAssert.Contains(Labels(more.Children!), Text["Command.DeletePermanently"]);
        CollectionAssert.DoesNotContain(Rows(menu), Text["Command.Duplicate"]);
        Assert.AreSame(more, menu[^1]);
    }

    [TestMethod]
    public void WithShift_RareEntriesJoinTheMainList()
    {
        var menu = PaneMenuComposer.ForItems(Input([File("a.txt")], shift: true));

        CollectionAssert.Contains(Rows(menu), Text["Command.Duplicate"]);
        Assert.IsNull(Slot(menu, MenuSlots.MoreOptions)); // nothing left in it and no shell entries to load
    }

    [TestMethod]
    public void ShellEntries_KeepMoreOptions_EvenWhenOtherwiseEmpty()
    {
        var menu = PaneMenuComposer.ForItems(Input([File("a.txt")], shift: true) with { ShellEntries = Nothing });
        Assert.IsNotNull(Slot(menu, MenuSlots.MoreOptions)?.LoadChildren);
    }

    [TestMethod]
    public void Settings_HideTheirEntries()
    {
        var settings = new GeneralSettings
        {
            ShowSendToInContextMenu = false,
            ShowCompressionInContextMenu = false,
            ShowEditTagsInContextMenu = false,
            ShowCreateFolderWithSelectionInContextMenu = false,
            ShowCopyPathInContextMenu = false,
        };
        var rows = Rows(PaneMenuComposer.ForItems(Input([File("photos.zip")], settings: settings)));

        foreach (var key in new[] { "Command.SendTo", "Command.CompressMenu", "Command.ExtractMenu", "Command.EditTags", "Command.CreateFolderWithSelection", "Command.CopyPath" })
            CollectionAssert.DoesNotContain(rows, Text[key]);
    }

    [TestMethod]
    public void Locations_ShowOnlyTheirEntries()
    {
        var trash = PaneMenuComposer.ForItems(Input([File("old.txt")], MenuScope.Trash));
        CollectionAssert.AreEqual(new[] { Text["Command.Delete"], Text["Command.Properties"] }, Labels(trash.Where(e => e.InStrip)));
        CollectionAssert.AreEqual(new[] { Text["Command.Restore"] }, Rows(trash));

        var remote = Rows(PaneMenuComposer.ForItems(Input([File("log.txt")], MenuScope.Remote) with { OpenWithEntries = null, SendToEntries = null }));
        CollectionAssert.Contains(remote, Text["Remote.Download"]);
        CollectionAssert.DoesNotContain(remote, Text["Command.CompressMenu"]);

        var search = Rows(PaneMenuComposer.ForItems(Input([File("a.txt")], MenuScope.Search)));
        CollectionAssert.Contains(search, Text["Command.OpenFileLocation"]);
        CollectionAssert.DoesNotContain(Rows(PaneMenuComposer.ForItems(Input([File("a.txt")]))), Text["Command.OpenFileLocation"]);
    }

    [TestMethod]
    public void Separators_NeverLeadTrailOrRepeat_AtAnyDepth()
    {
        FileItemViewModel[][] selections = [[File("setup.exe")], [File("a.txt")], [Folder("x")], [File("p.zip")], [File("a.txt"), Folder("x"), File("p.tar.gz")]];
        foreach (var scope in new[] { MenuScope.Folder, MenuScope.Search, MenuScope.Trash, MenuScope.Remote })
        {
            foreach (var shift in new[] { false, true })
            {
                foreach (var selection in selections)
                    AssertTidy(PaneMenuComposer.ForItems(Input(selection, scope, shift)));
                AssertTidy(PaneMenuComposer.ForBackground(Input([], scope, shift)));
            }
        }

        static void AssertTidy(List<MenuEntry> entries)
        {
            if (entries.Count == 0)
                return;
            Assert.IsFalse(entries[0].IsSeparator);
            Assert.IsFalse(entries[^1].IsSeparator);
            for (var i = 1; i < entries.Count; i++)
                Assert.IsFalse(entries[i].IsSeparator && entries[i - 1].IsSeparator);
            foreach (var nested in entries.Select(e => e.Children).OfType<List<MenuEntry>>())
                AssertTidy(nested);
        }
    }

    [TestMethod]
    public void Background_DependsOnLocation()
    {
        var folder = PaneMenuComposer.ForBackground(Input([]));
        CollectionAssert.AreEqual(new[] { Text["Command.Paste"], Text["Command.Properties"] }, Labels(folder.Where(e => e.InStrip)));
        CollectionAssert.Contains(Rows(folder), Text["Command.New"]);

        var trash = Rows(PaneMenuComposer.ForBackground(Input([], MenuScope.Trash)));
        CollectionAssert.Contains(trash, Text["Command.EmptyTrash"]);
        CollectionAssert.DoesNotContain(trash, Text["Command.New"]);

        var remote = Rows(PaneMenuComposer.ForBackground(Input([], MenuScope.Remote)));
        CollectionAssert.Contains(remote, Text["Remote.Upload"]);
        CollectionAssert.Contains(remote, Text["Command.NewFolder"]);
    }

    [TestMethod]
    public void Background_MarksTheCurrentView_AndOnlyTheOtherDirectionToggles()
    {
        var input = Input([]) with { View = new ViewState(FolderLayout.List, Core.Sorting.SortColumn.Size, SortDescending: true, Core.Sorting.GroupBy.None) };
        var sort = PaneMenuComposer.ForBackground(input).Single(e => e.Label == Text["Command.Sort"]).Children!;

        Assert.IsTrue(sort.Single(e => e.Label == Text["Sort.Size"]).IsChecked);
        Assert.IsNull(sort.Single(e => e.Label == Text["Sort.Descending"]).Command);
        Assert.IsNotNull(sort.Single(e => e.Label == Text["Sort.Ascending"]).Command);
    }

    [TestMethod]
    public void Traits_OnePass_ElevationAskedOnlyForOneFile()
    {
        var asked = 0;
        ElevationOptions Counting(string path)
        {
            asked++;
            return WindowsRules(path);
        }

        var many = SelectionTraits.Collect([File("a.png"), File("b.png"), Folder("c"), File("d.zip"), File("e.lnk")], ArchiveService.IsSupportedArchive, Counting);
        Assert.AreEqual(0, asked);
        Assert.AreEqual((5, 1, 1, 1), (many.Count, many.FolderCount, many.ArchiveCount, many.ShortcutCount));
        Assert.IsNull(many.SharedCategory);

        Assert.IsTrue(SelectionTraits.Collect([File("tool.exe")], ArchiveService.IsSupportedArchive, Counting).IsElevatableProgram);
        Assert.AreEqual(1, asked);
        Assert.IsTrue(SelectionTraits.Collect([File("main.cs")], ArchiveService.IsSupportedArchive, Counting).IsEditableText);
    }

    // ── Edit tags / Clear tag(s) ──

    private static readonly Probe Remover = new();

    private static List<MenuEntry> TagMenu(params TagChoice[] tags)
    {
        var input = Input([File("a.txt")], commands: Commands() with { RemoveTag = Remover }) with { Tags = tags };
        return PaneMenuComposer.ForItems(input).Single(e => e.Slot == MenuSlots.Tags).Children!;
    }

    private static MenuEntry ClearEntry(List<MenuEntry> tagMenu) => tagMenu[^1];

    [TestMethod]
    public void ClearTag_IsDisabled_WhenNoSelectedItemHasATag()
    {
        var menu = TagMenu(new TagChoice("w", "Work", "#FF0000", IsChecked: false, IsPresent: false));
        var clear = ClearEntry(menu);

        Assert.IsTrue(menu[^2].IsSeparator);
        Assert.AreEqual(Text["Command.ClearTag"], clear.Label);
        Assert.IsFalse(clear.IsEnabled);
        Assert.IsNull(clear.Children);
    }

    [TestMethod]
    public void ClearTag_RemovesTheOnlyTag_Directly()
    {
        var clear = ClearEntry(TagMenu(
            new TagChoice("w", "Work", "#FF0000", IsChecked: true, IsPresent: true),
            new TagChoice("h", "Home", "#00FF00", IsChecked: false, IsPresent: false)));

        Assert.IsTrue(clear.IsEnabled);
        Assert.IsNull(clear.Children);
        Assert.AreSame(Remover, clear.Command);
        Assert.AreEqual("w", clear.Parameter);
        Assert.AreEqual(Text.Format("Command.ClearTagNamed", "Work"), clear.Label);
    }

    [TestMethod]
    public void ClearTags_OffersEachPresentTag()
    {
        var clear = ClearEntry(TagMenu(
            new TagChoice("w", "Work", "#FF0000", IsChecked: true, IsPresent: true),
            new TagChoice("h", "Home", "#00FF00", IsChecked: false, IsPresent: false),
            new TagChoice("u", "Urgent", "#0000FF", IsChecked: false, IsPresent: true)));

        Assert.AreEqual(Text["Command.ClearTags"], clear.Label);
        CollectionAssert.AreEqual(new[] { "Work", "Urgent" }, clear.Children!.Select(c => c.Label).ToArray());
        CollectionAssert.AreEqual(new object[] { "w", "u" }, clear.Children!.Select(c => c.Parameter!).ToArray());
        Assert.IsTrue(clear.Children!.All(c => ReferenceEquals(c.Command, Remover)));
        Assert.AreEqual("#0000FF", clear.Children![1].Tint);
    }

    [TestMethod]
    public void TagToggles_ShowTheTagColor()
    {
        var toggle = TagMenu(new TagChoice("w", "Work", "#FF0000", IsChecked: true, IsPresent: true))[0];
        Assert.AreEqual(("Tag", "#FF0000", true), (toggle.Glyph, toggle.Tint, toggle.IsChecked));
    }

    [TestMethod]
    public void EveryBackgroundRow_HasAnIcon()
    {
        foreach (var entry in PaneMenuComposer.ForBackground(Input([])))
        {
            if (entry.IsSeparator)
                continue;
            Assert.IsNotNull(entry.Glyph, entry.Label);
            foreach (var child in entry.Children ?? [])
                Assert.IsTrue(child.IsSeparator || child.Glyph is not null, child.Label);
        }
    }

    // ── Shell menu bridge ──

    private sealed class FakeShellMenu(params ShellMenuItem[] items) : IShellMenu
    {
        public List<int> Invoked { get; } = [];

        public IReadOnlyList<ShellMenuItem> Items { get; } = items;

        public Task<IReadOnlyList<ShellMenuItem>> GetChildrenAsync(ShellMenuItem parent, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ShellMenuItem>>([new ShellMenuItem(100 + parent.Key, "child of " + parent.Label, null, false, true, false, false, null)]);

        public Task<bool> InvokeAsync(ShellMenuItem item, nint ownerWindow, CancellationToken cancellationToken = default)
        {
            Invoked.Add(item.Key);
            return Task.FromResult(result: true);
        }

        public void Dispose()
        {
        }
    }

    private static ShellMenuItem Leaf(int key, string label, string? verb = null) => new(key, label, verb, false, true, false, false, null);

    private static ShellMenuItem Branch(int key, string label, string? verb) => new(key, label, verb, false, true, false, true, null);

    [TestMethod]
    public async Task Bridge_SplitsOpenWithAndSendTo_FromTheRest()
    {
        var shell = new FakeShellMenu(
            ShellMenuItem.Separator(1), Branch(2, "Open with", "openas"), Leaf(3, "Scan with Defender"), ShellMenuItem.Separator(4),
            ShellMenuItem.Separator(5), Branch(6, "Send to", "sendto"), Branch(7, "7-Zip", null), ShellMenuItem.Separator(8));
        var bridge = new ShellMenuBridge(shell, () => 0, NullLogger.Instance);

        var rest = bridge.Remainder();
        CollectionAssert.AreEqual(new[] { "Scan with Defender", "", "7-Zip" }, rest.Select(e => e.IsSeparator ? "" : e.Label).ToArray());
        Assert.IsNotNull(rest[2].LoadChildren);

        var sendTo = bridge.FindSubmenu(ShellMenuBridge.SendToVerb);
        Assert.IsNotNull(sendTo);
        var children = await bridge.ChildrenOfAsync(sendTo, CancellationToken.None);
        Assert.AreEqual("child of Send to", children.Single().Label);
    }

    [TestMethod]
    public async Task Bridge_EntriesInvokeTheShell()
    {
        var shell = new FakeShellMenu(Leaf(3, "Scan with Defender"));
        var entry = new ShellMenuBridge(shell, () => 42, NullLogger.Instance).Remainder().Single();

        entry.Command!.Execute(null);
        await Task.Yield();
        CollectionAssert.AreEqual(new[] { 3 }, shell.Invoked);
    }

    [TestMethod]
    public void Bridge_HidesNavigatorsOwnVerbs_CaseInsensitively()
    {
        Assert.IsTrue(ShellMenuBridge.NavigatorVerbs.Contains("RunAs"));
        Assert.IsTrue(ShellMenuBridge.NavigatorVerbs.Contains("properties"));
        Assert.IsFalse(ShellMenuBridge.NavigatorVerbs.Contains("sendto"));
        Assert.IsFalse(ShellMenuBridge.NavigatorVerbs.Contains("openas"));
    }
}

[TestClass]
public sealed class ExtractDestinationTests
{
    [TestMethod]
    public void ArchiveStem_DropsArchiveSuffixesOnly()
    {
        Assert.AreEqual("photos", ArchiveService.ArchiveStem("/x/photos.tar.gz"));
        Assert.AreEqual("photos", ArchiveService.ArchiveStem("photos.zip"));
        Assert.AreEqual("notes.txt", ArchiveService.ArchiveStem("notes.txt"));
        Assert.AreEqual(".zip", ArchiveService.ArchiveStem(".zip"));
    }

    [TestMethod]
    public void ChooseFolderDestination_NeverReusesAnExistingName()
    {
        var root = Directory.CreateTempSubdirectory("nav-extract-").FullName;
        try
        {
            Assert.AreEqual(Path.Combine(root, "photos"), ArchiveService.ChooseFolderDestination(Path.Combine(root, "photos.zip"), root));
            Directory.CreateDirectory(Path.Combine(root, "photos"));
            Assert.AreNotEqual(Path.Combine(root, "photos"), ArchiveService.ChooseFolderDestination(Path.Combine(root, "photos.zip"), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
