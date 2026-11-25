using System.Text;
using Navigator.Core.IO;
using Navigator.Core.Settings;
using Navigator.Platform;
using Navigator.Platform.DataTransfer;
using Navigator.Platform.Launch;
using Navigator.Platform.Trash;
using Navigator.Presentation.Commands;
using Navigator.Presentation.Details;
using Navigator.Presentation.Items;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;
using Navigator.Tests;

namespace Navigator.UnitTests;

[TestClass]
public sealed class ItemSelectionTests
{
    private static readonly Localizer Text = new();

    private static List<FileItemViewModel> Items(int count)
    {
        var context = new ItemDisplayContext(Text, showExtensions: true);
        return [.. Enumerable.Range(0, count).Select(i => new FileItemViewModel(
            new FileEntry($"item{i:00}.txt", $"/d/item{i:00}.txt", false, i, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal), context))];
    }

    [TestMethod]
    public void Click_CtrlClick_ShiftClick()
    {
        var items = Items(10);
        var selection = new ItemSelection();
        selection.SetItems(items);

        selection.Click(items[2], control: false, shift: false);
        Assert.AreEqual(1, selection.Count);
        selection.Click(items[5], control: false, shift: true);
        CollectionAssert.AreEqual(items[2..6], selection.SelectedItems.ToList());
        selection.Click(items[8], control: true, shift: false);
        Assert.AreEqual(5, selection.Count);
        selection.Click(items[3], control: true, shift: false);
        Assert.IsFalse(items[3].IsSelected);
        Assert.AreEqual(4, selection.Count);
    }

    [TestMethod]
    public void Keyboard_MovesAndExtends()
    {
        var items = Items(5);
        var selection = new ItemSelection();
        selection.SetItems(items);
        selection.MoveFocus(1, shift: false, control: false);
        Assert.AreSame(items[0], selection.FocusedItem);
        selection.MoveFocus(2, shift: true, control: false);
        Assert.AreEqual(3, selection.Count);
        selection.MoveFocus(int.MaxValue, shift: false, control: false);
        Assert.AreSame(items[4], selection.SelectedItems.Single());
        selection.MoveFocus(-1, shift: false, control: true);
        Assert.AreSame(items[3], selection.FocusedItem);
        Assert.AreSame(items[4], selection.SelectedItems.Single(), "Ctrl+arrow moves focus only");
        selection.ToggleFocused();
        Assert.AreEqual(2, selection.Count);
    }

    [TestMethod]
    public void SelectAll_Invert_Clear()
    {
        var items = Items(4);
        var selection = new ItemSelection();
        selection.SetItems(items);
        selection.Click(items[0], false, false);
        selection.Invert();
        Assert.AreEqual(3, selection.Count);
        selection.SelectAll();
        Assert.IsTrue(items.All(i => i.IsSelected));
        selection.Clear();
        Assert.IsFalse(items.Any(i => i.IsSelected));
    }

    [TestMethod]
    public void SetItems_KeepsSelectionByPath()
    {
        var items = Items(3);
        var selection = new ItemSelection();
        selection.SetItems(items);
        selection.Click(items[1], false, false);
        var reloaded = Items(3);
        selection.SetItems(reloaded, ["/d/item01.txt"]);
        Assert.AreSame(reloaded[1], selection.SelectedItems.Single());
        Assert.IsFalse(items[1].IsSelected, "stale view models are deselected");
    }

    [TestMethod]
    public void RubberBand_WithBaselineToggles()
    {
        var items = Items(6);
        var selection = new ItemSelection();
        selection.SetItems(items);
        selection.SelectRectangle(items[1..4], baseline: [items[0], items[2]]);
        CollectionAssert.AreEquivalent(new[] { items[0], items[1], items[3] }, selection.SelectedItems.ToList());
    }

    [TestMethod]
    public void TypeToSelect_CyclesMatches()
    {
        var items = Items(12);
        var selection = new ItemSelection();
        selection.SetItems(items);
        Assert.AreSame(items[10], selection.JumpToPrefix("item1"));
        Assert.AreSame(items[10], selection.SelectedItems.Single());
    }

    [TestMethod]
    public void ContextMenu_KeepsMultiSelection()
    {
        var items = Items(3);
        var selection = new ItemSelection();
        selection.SetItems(items);
        selection.SelectAll();
        selection.PrepareContextMenu(items[1]);
        Assert.AreEqual(3, selection.Count);
    }
}

[TestClass]
public sealed class PresentationTests
{
    [TestMethod]
    public void Localization_LanguagesHaveTheSameKeys()
    {
        var english = Localizer.Keys("en");
        Assert.IsTrue(english.Count > 300);
        foreach (var language in Localizer.AvailableLanguages)
            CollectionAssert.AreEquivalent(english.ToList(), Localizer.Keys(language).ToList(), $"'{language}' must translate every key");
    }

    [TestMethod]
    public void Localization_SwitchesAndFallsBack()
    {
        var localizer = new Localizer();
        Assert.AreEqual("New folder", localizer["Item.NewFolderName"]);
        localizer.SetLanguage("uk");
        Assert.AreEqual("Нова папка", localizer["Item.NewFolderName"]);
        Assert.AreEqual("Missing.Key", localizer["Missing.Key"]);
        localizer.SetLanguage("xx");
        Assert.AreEqual("en", localizer.Language);
    }

    [TestMethod]
    public void WindowsVolumeName_UsesLabelOrDriveType()
    {
        var localizer = new Localizer();
        Assert.AreEqual("Local Disk (C:)", HomeViewModel.WindowsVolumeName(new Navigator.Core.Platform.VolumeInfo(@"C:\", string.Empty, Navigator.Core.Platform.VolumeKind.Fixed, "NTFS", 100, 50, true), localizer));
        Assert.AreEqual("Games (D:)", HomeViewModel.WindowsVolumeName(new Navigator.Core.Platform.VolumeInfo(@"D:\", "Games", Navigator.Core.Platform.VolumeKind.Fixed, "NTFS", 100, 50, true), localizer));
        Assert.AreEqual("Removable Disk (E:)", HomeViewModel.WindowsVolumeName(new Navigator.Core.Platform.VolumeInfo(@"E:\", " ", Navigator.Core.Platform.VolumeKind.Removable, "FAT32", 0, 0, false), localizer));
    }

    [TestMethod]
    public void EveryCommandHasAName()
    {
        var localizer = new Localizer();
        Assert.AreEqual(CommandRegistry.All.Count, CommandRegistry.All.Select(c => c.Id).Distinct().Count());
        foreach (var command in CommandRegistry.All)
            Assert.IsTrue(localizer.HasKey(command.NameKey), command.NameKey);
    }

    [TestMethod]
    public void DefaultShortcuts_DoNotConflict()
    {
        Assert.AreEqual(0, CommandRegistry.FindConflicts(new AppSettings(), isMac: false).Count);
        Assert.AreEqual(0, CommandRegistry.FindConflicts(new AppSettings(), isMac: true).Count);
        var settings = new AppSettings();
        settings.KeyBindings["tab.new"] = ["Ctrl+W"];
        Assert.AreEqual(1, CommandRegistry.FindConflicts(settings, isMac: false).Count);
    }

    [TestMethod]
    public void DefaultShortcuts_AreAllValidGestures()
    {
        foreach (var command in CommandRegistry.All)
        {
            foreach (var gesture in command.DefaultGestures)
            {
                Assert.IsNotNull(CommandRegistry.NormalizeGesture(gesture, isMac: false), gesture);
                Assert.IsNotNull(CommandRegistry.NormalizeGesture(gesture, isMac: true), gesture);
            }
        }
    }

    [TestMethod]
    [DataRow("ctrl + shift + 1", false, "Primary+Shift+D1")]
    [DataRow("Shift+Ctrl+t", false, "Primary+Shift+T")]
    [DataRow("Cmd+,", true, "Primary+OemComma")]
    [DataRow("Ctrl+T", true, "Ctrl+T")]
    [DataRow("Ctrl++", false, "Primary+OemPlus")]
    [DataRow("Primary+Shift+`", false, "Primary+Shift+OemTilde")]
    [DataRow("alt+left", false, "Alt+Left")]
    [DataRow("F5", false, "F5")]
    [DataRow("Win+E", false, "Meta+E")]
    public void NormalizeGesture_ProducesCanonicalForm(string text, bool isMac, string expected)
        => Assert.AreEqual(expected, CommandRegistry.NormalizeGesture(text, isMac));

    [TestMethod]
    [DataRow("")]
    [DataRow("Ctrl+Shift")]
    [DataRow("Ctrl+A+B")]
    [DataRow("Ctrl+12")]
    [DataRow("Ctrl+")]
    [DataRow("Hyper+A")]
    public void NormalizeGesture_RejectsInvalidText(string text)
        => Assert.IsNull(CommandRegistry.NormalizeGesture(text, isMac: false));

    [TestMethod]
    public void FormatForDisplay_UsesFriendlyKeyNames()
    {
        Assert.AreEqual("Ctrl+Shift+1", CommandRegistry.FormatForDisplay("Ctrl+Shift+D1"));
        Assert.AreEqual("Cmd+,", CommandRegistry.FormatForDisplay("Cmd+OemComma"));
        Assert.AreEqual("Alt+Left", CommandRegistry.FormatForDisplay("Alt+Left"));
        Assert.AreEqual("Delete", CommandRegistry.FormatForDisplay("Delete"));
    }

    [TestMethod]
    public void DisplayedGestures_RoundTripToTheSameBinding()
    {
        foreach (var command in CommandRegistry.All)
        {
            foreach (var gesture in CommandRegistry.GesturesFor(command.Id, new AppSettings(), isMac: false))
            {
                var shown = CommandRegistry.FormatForDisplay(gesture);
                Assert.AreEqual(CommandRegistry.NormalizeGesture(gesture, isMac: false), CommandRegistry.NormalizeGesture(shown, isMac: false), shown);
            }
        }
    }

    [TestMethod]
    public void GestureList_KeepsCommaKeys()
    {
        CollectionAssert.AreEqual(new[] { "Ctrl+R", "F5" }, Navigator.Presentation.SettingsPages.SettingsViewModel.SplitGestureList("Ctrl+R, F5"));
        CollectionAssert.AreEqual(new[] { "Ctrl+,", "F1" }, Navigator.Presentation.SettingsPages.SettingsViewModel.SplitGestureList("Ctrl+, , F1"));
        CollectionAssert.AreEqual(new[] { "Ctrl+," }, Navigator.Presentation.SettingsPages.SettingsViewModel.SplitGestureList("Ctrl+,"));
        Assert.AreEqual(0, Navigator.Presentation.SettingsPages.SettingsViewModel.SplitGestureList("  ").Count);
    }

    [TestMethod]
    public void BulkRename_ValidatesEverythingFirst()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateFile("a.jpg");
        var b = temp.CreateFile("b.png");
        temp.CreateFile("Trip 2.jpg");
        var plan = BulkRenamePlanner.Plan([(a, false), (b, false)], "Trip #");
        CollectionAssert.AreEqual(new[] { "Trip 1.jpg", "Trip 2.png" }, plan.Renames.Select(r => r.NewName).ToArray());

        var conflict = BulkRenamePlanner.Plan([(a, false), (b, false)], "Trip #");
        Assert.IsNull(conflict.Error);
        var clash = BulkRenamePlanner.Plan([(a, false)], "Trip 2");
        Assert.AreEqual("AlreadyExists", clash.Error);
        var invalid = BulkRenamePlanner.Plan([(a, false)], "a/b #");
        Assert.AreEqual("InvalidCharacters", invalid.Error);
    }

    [TestMethod]
    public void PermissionText_FormatsModes()
    {
        Assert.AreEqual("drwxr-xr-x", PermissionText.Format((UnixFileMode)0b111_101_101, isDirectory: true));
        Assert.AreEqual("-rw-------", PermissionText.Format(UnixFileMode.UserRead | UnixFileMode.UserWrite, isDirectory: false));
        Assert.AreEqual("drwxrwxrwt", PermissionText.Format((UnixFileMode)0b111_111_111 | UnixFileMode.StickyBit, isDirectory: true));
    }

    [TestMethod]
    public void TextPreview_RejectsBinary()
    {
        using var temp = new TempDirectory();
        var text = temp.CreateFile("t.txt", "hello");
        var binary = temp.Combine("b.bin");
        File.WriteAllBytes(binary, [1, 0, 2]);
        Assert.AreEqual("hello", PreviewViewModel.ReadTextPreview(text));
        Assert.IsNull(PreviewViewModel.ReadTextPreview(binary));
    }

    [TestMethod]
    public void ItemDisplay_HidesExtensionsWhenAsked()
    {
        var entry = new FileEntry("photo.JPG", "/p/photo.JPG", false, 2048, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal);
        var hidden = new FileItemViewModel(entry, new ItemDisplayContext(new Localizer(), showExtensions: false));
        Assert.AreEqual("photo", hidden.DisplayName);
        Assert.AreEqual(ItemCategory.Image, hidden.Category);
        Assert.AreEqual("JPG file", hidden.TypeText);
        var dot = new FileItemViewModel(new FileEntry(".bashrc", "/p/.bashrc", false, 1, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Hidden), new ItemDisplayContext(new Localizer(), false));
        Assert.AreEqual(".bashrc", dot.DisplayName);
    }
}

[TestClass]
public sealed class PlatformParsingTests
{
    [TestMethod]
    public void StreamedClipboardNames_OnlyPlainRelativeNamesPass()
    {
        CollectionAssert.AreEqual(new[] { "report.pdf" }, StreamedNames.Split("report.pdf"));
        CollectionAssert.AreEqual(new[] { "Folder", "sub", "file.txt" }, StreamedNames.Split(@"Folder\sub\file.txt"));
        CollectionAssert.AreEqual(new[] { "Folder", "file.txt" }, StreamedNames.Split("Folder/file.txt"));
        foreach (var hostile in new[] { "", " ", @"..\evil.exe", @"a\..\..\b", @"\\server\share\x", "/etc/passwd", @"C:\x", "C:x", "file.txt:stream", @"a\\b", "dot.", "a\0b" })
            Assert.IsNull(StreamedNames.Split(hostile), hostile);
    }

    [TestMethod]
    public void UserDirs_ParsesXdgFile()
    {
        string[] lines = ["# comment", "XDG_DOWNLOAD_DIR=\"$HOME/Завантаження\"", "XDG_MUSIC_DIR=\"$HOME/\"", "XDG_PICTURES_DIR=\"/data/pics\""];
        Assert.AreEqual("/home/u/Завантаження", KnownFolderProvider.ParseUserDirs(lines, "XDG_DOWNLOAD_DIR", "/home/u"));
        Assert.IsNull(KnownFolderProvider.ParseUserDirs(lines, "XDG_MUSIC_DIR", "/home/u"), "$HOME/ means disabled");
        Assert.AreEqual("/data/pics", KnownFolderProvider.ParseUserDirs(lines, "XDG_PICTURES_DIR", "/home/u"));
    }

    [TestMethod]
    public void TrashInfo_RoundTripsWithEscaping()
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
        var text = TrashInfoFile.Build("/home/u/My file%#.txt", when);
        StringAssert.Contains(text, "Path=/home/u/My%20file%25%23.txt");
        StringAssert.Contains(text, "DeletionDate=2026-03-04T05:06:07");
        Assert.IsTrue(TrashInfoFile.TryParse(text.Split('\n'), out var original, out var deleted));
        Assert.AreEqual("/home/u/My file%#.txt", original);
        Assert.AreEqual(when, deleted!.Value.LocalDateTime);
        Assert.IsFalse(TrashInfoFile.TryParse(["[Other]", "Path=/x"], out _, out _));
    }

    [TestMethod]
    public void RecycleBinInfo_ReadsVersion2()
    {
        const string original = @"C:\Users\u\doc.txt";
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(1234L));
        bytes.AddRange(BitConverter.GetBytes(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()));
        bytes.AddRange(BitConverter.GetBytes(original.Length + 1));
        bytes.AddRange(Encoding.Unicode.GetBytes(original + "\0"));
        Assert.IsTrue(RecycleBinInfoFile.TryRead(bytes.ToArray(), out var path, out var length, out var deleted));
        Assert.AreEqual(original, path);
        Assert.AreEqual(1234, length);
        Assert.AreEqual(2026, deleted.UtcDateTime.Year);
        Assert.IsFalse(RecycleBinInfoFile.TryRead(new byte[10], out _, out _, out _));
    }

    [TestMethod]
    public void DesktopEntry_ParsesAndExpandsExec()
    {
        string[] lines = ["[Desktop Entry]", "Type=Application", "Name=Text Editor", "Exec=\"/usr/bin/my editor\" --new-window %U", "MimeType=text/plain;text/markdown;", "[Desktop Action new]", "Name=Ignored"];
        var entry = LinuxDesktopEntries.Parse("editor.desktop", "/x/editor.desktop", lines)!;
        Assert.AreEqual("Text Editor", entry.Name);
        CollectionAssert.AreEqual(new[] { "text/plain", "text/markdown" }, entry.MimeTypes.ToArray());

        var (program, arguments) = LinuxDesktopEntries.ExpandExec(entry.Exec, "/tmp/a b.txt", entry.Name)!.Value;
        Assert.AreEqual("/usr/bin/my editor", program);
        CollectionAssert.AreEqual(new[] { "--new-window", "file:///tmp/a%20b.txt" }, arguments);

        var (_, appended) = LinuxDesktopEntries.ExpandExec("viewer --flag", "/tmp/x", "v")!.Value;
        CollectionAssert.AreEqual(new[] { "--flag", "/tmp/x" }, appended, "a file argument is appended when Exec has no field code");
        Assert.IsNull(LinuxDesktopEntries.Parse("x", "x", ["[Desktop Entry]", "Type=Link", "Name=x", "Exec=x"]));
    }

    [TestMethod]
    public void RuntimeIdentifiers_AreBuiltPerPlatform()
    {
        Assert.AreEqual("win-arm64", PlatformInfo.BuildRuntimeIdentifier(Core.Platform.OperatingSystemKind.Windows, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.AreEqual("osx-x64", PlatformInfo.BuildRuntimeIdentifier(Core.Platform.OperatingSystemKind.MacOS, System.Runtime.InteropServices.Architecture.X64));
        Assert.AreEqual("linux-x64", PlatformInfo.BuildRuntimeIdentifier(Core.Platform.OperatingSystemKind.Linux, System.Runtime.InteropServices.Architecture.X64));
    }

    [TestMethod]
    public void VolumeFilter_HidesVirtualLinuxMounts()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Linux-specific mount filtering.");
        Assert.IsTrue(VolumeProvider.ShouldShow("/", DriveType.Fixed, "ext4"));
        Assert.IsTrue(VolumeProvider.ShouldShow("/run/media/u/USB", DriveType.Removable, "vfat"));
        Assert.IsTrue(VolumeProvider.ShouldShow("/home", DriveType.Fixed, "btrfs"));
        Assert.IsFalse(VolumeProvider.ShouldShow("/proc", DriveType.Ram, "proc"));
        Assert.IsFalse(VolumeProvider.ShouldShow("/snap/core/1", DriveType.Fixed, "squashfs"));
        Assert.IsFalse(VolumeProvider.ShouldShow("/boot/efi", DriveType.Fixed, "vfat"));
        Assert.IsTrue(VolumeProvider.ShouldShow("/net/share", DriveType.Network, "nfs4"));
    }
}
