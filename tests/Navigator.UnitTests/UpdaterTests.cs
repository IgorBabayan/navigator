using System.Net;
using System.Text;
using Navigator.Core.Hashing;
using Navigator.Core.Platform;
using Navigator.Core.Versioning;
using Navigator.ReleaseTool;
using Navigator.Tests;
using Navigator.Updater;

namespace Navigator.UnitTests;

[TestClass]
public sealed class UpdaterTests
{
    private static readonly string Sha = new('a', 64);

    private static UpdateManifest Manifest(string version, params (string Name, string Rid, string Kind)[] assets) => new()
    {
        Version = version,
        Commit = "abc",
        Assets = [.. assets.Select(a => new ManifestAsset { Name = a.Name, Rid = a.Rid, Kind = a.Kind, Size = 10, Sha256 = Sha })],
    };

    [TestMethod]
    public void Manifest_RejectsUnsafeOrIncompleteAssets()
    {
        var bad = Manifest("1.0.1", ("../evil.exe", "win-x64", AssetKinds.Installer));
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(bad.Serialize())));
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateManifest.Parse("""{"version":"1.0.0","assets":[]}"""u8));
        var good = Manifest("1.0.1", ("Navigator-1.0.1-win-x64-setup.exe", "win-x64", AssetKinds.Installer));
        Assert.AreEqual("1.0.1", UpdateManifest.Parse(Encoding.UTF8.GetBytes(good.Serialize())).Version);
    }

    [TestMethod]
    public void AssetSelection_MatchesRidKindAndExtension()
    {
        var manifest = Manifest("1.2.0",
            ("Navigator-1.2.0-win-x64-setup.exe", "win-x64", AssetKinds.Installer),
            ("Navigator-1.2.0-win-x64-portable.zip", "win-x64", AssetKinds.Portable),
            ("Navigator-1.2.0-win-arm64-setup.exe", "win-arm64", AssetKinds.Installer),
            ("Navigator-1.2.0-linux-x64.AppImage", "linux-x64", AssetKinds.AppImage),
            ("Navigator-1.2.0-linux-x64.tar.gz", "linux-x64", AssetKinds.Portable),
            ("Navigator-1.2.0-osx-arm64.zip", "osx-arm64", AssetKinds.AppArchive),
            ("Navigator-1.2.0-osx-arm64.dmg", "osx-arm64", AssetKinds.DiskImage));

        Assert.AreEqual("Navigator-1.2.0-win-arm64-setup.exe", UpdateAssetSelector.Select(manifest, "win-arm64", InstallationKind.WindowsInstaller)?.Name);
        Assert.AreEqual("Navigator-1.2.0-win-x64-portable.zip", UpdateAssetSelector.Select(manifest, "win-x64", InstallationKind.WindowsPortable)?.Name);
        Assert.AreEqual("Navigator-1.2.0-linux-x64.AppImage", UpdateAssetSelector.Select(manifest, "linux-x64", InstallationKind.LinuxAppImage)?.Name);
        Assert.AreEqual("Navigator-1.2.0-linux-x64.tar.gz", UpdateAssetSelector.Select(manifest, "linux-x64", InstallationKind.LinuxPortable)?.Name);
        Assert.AreEqual("Navigator-1.2.0-osx-arm64.zip", UpdateAssetSelector.Select(manifest, "osx-arm64", InstallationKind.MacAppBundle)?.Name);
        Assert.IsNull(UpdateAssetSelector.Select(manifest, "linux-arm64", InstallationKind.LinuxAppImage));
        Assert.IsNull(UpdateAssetSelector.Select(manifest, "linux-x64", InstallationKind.LinuxPackageManaged));
    }

    [TestMethod]
    public void Signature_VerifiesAndDetectsTampering()
    {
        var (privateKey, publicKey) = ManifestSignature.CreateKeyPair();
        var data = Encoding.UTF8.GetBytes("""{"version":"1.0.0"}""");
        var signature = ManifestSignature.Sign(data, privateKey);
        Assert.IsTrue(ManifestSignature.Verify(data, signature, publicKey));
        data[3] ^= 1;
        Assert.IsFalse(ManifestSignature.Verify(data, signature, publicKey));
        Assert.IsFalse(ManifestSignature.Verify(data, "not base64!", publicKey));
    }

    [TestMethod]
    public void InstallationDetection_RecognisesLayouts()
    {
        using var temp = new TempDirectory();
        var configured = new UpdateConfiguration("o/r", null, SemVersion.Parse("1.0.0"));
        var dev = new UpdateConfiguration(null, null, SemVersion.Parse("0.0.0-dev"));
        static string? NoEnv(string _) => null;

        Assert.AreEqual(InstallationKind.Development, InstallationDetector.Detect(dev, OperatingSystemKind.Windows, temp.Path, NoEnv));
        Assert.AreEqual(InstallationKind.Development, InstallationDetector.Detect(configured, OperatingSystemKind.Windows, temp.Path, NoEnv));
        temp.CreateFile(InstallationDetector.PortableMarker, string.Empty);
        Assert.AreEqual(InstallationKind.WindowsPortable, InstallationDetector.Detect(configured, OperatingSystemKind.Windows, temp.Path, NoEnv));
        Assert.AreEqual(InstallationKind.LinuxPortable, InstallationDetector.Detect(configured, OperatingSystemKind.Linux, temp.Path, NoEnv));
        temp.CreateFile("unins000.exe");
        Assert.AreEqual(InstallationKind.WindowsInstaller, InstallationDetector.Detect(configured, OperatingSystemKind.Windows, temp.Path, NoEnv));

        var appImage = temp.CreateFile("Navigator.AppImage");
        Assert.AreEqual(InstallationKind.LinuxAppImage, InstallationDetector.Detect(configured, OperatingSystemKind.Linux, temp.Path, k => k == "APPIMAGE" ? appImage : null));
        Assert.AreEqual(InstallationKind.LinuxPackageManaged, InstallationDetector.Detect(configured, OperatingSystemKind.Linux, "/usr/lib/navigator", NoEnv));
        Assert.AreEqual("/Applications/Navigator.app", InstallationDetector.GetMacBundle("/Applications/Navigator.app/Contents/MacOS/"));
    }

    [TestMethod]
    public void SafeExtraction_RejectsTraversal()
    {
        using var temp = new TempDirectory();
        Assert.ThrowsExactly<InvalidDataException>(() => DirectorySwapUpdate.SafeTarget(temp.Path, "../../etc/passwd"));
        Assert.ThrowsExactly<InvalidDataException>(() => DirectorySwapUpdate.SafeTarget(temp.Path, "a/../../x"));
        Assert.IsTrue(DirectorySwapUpdate.SafeTarget(temp.Path, "./app/Navigator").EndsWith("Navigator", StringComparison.Ordinal));
    }

    private static (FakeHttpHandler Handler, GitHubReleaseSource Source) CreateSource(string releasesJson, string? publicKey = null)
    {
        var handler = new FakeHttpHandler();
        handler.Add("https://api.github.com/repos/o/r/releases?per_page=20", Encoding.UTF8.GetBytes(releasesJson));
        return (handler, new GitHubReleaseSource(new HttpClient(handler), "o/r", publicKey));
    }

    private static string Release(string tag, bool prerelease = false, bool draft = false, bool signed = false)
        => $$"""
           { "tag_name": "{{tag}}", "html_url": "https://github.com/o/r/releases/tag/{{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
             "assets": [ { "name": "update-manifest.json", "browser_download_url": "https://dl/{{tag}}/update-manifest.json", "size": 1 }
                         {{(signed ? $", {{ \"name\": \"update-manifest.json.sig\", \"browser_download_url\": \"https://dl/{tag}/update-manifest.json.sig\", \"size\": 1 }}" : string.Empty)}} ] }
           """;

    [TestMethod]
    public async Task ReleaseSource_PicksNewestStableWithMatchingManifest()
    {
        var (handler, source) = CreateSource($"[{Release("v1.3.0-beta.1", prerelease: true)},{Release("v1.2.0")},{Release("v1.1.0")},{Release("v9.0.0", draft: true)}]");
        handler.Add("https://dl/v1.2.0/update-manifest.json", Encoding.UTF8.GetBytes(Manifest("1.2.0", ("Navigator-1.2.0-linux-x64.AppImage", "linux-x64", AssetKinds.AppImage)).Serialize()));

        var found = await source.FindNewerReleaseAsync(SemVersion.Parse("1.1.0"), includePrereleases: false, CancellationToken.None);
        Assert.AreEqual(SemVersion.Parse("1.2.0"), found?.Version);
        Assert.IsFalse(found!.SignatureVerified);

        var none = await source.FindNewerReleaseAsync(SemVersion.Parse("1.2.0"), includePrereleases: false, CancellationToken.None);
        Assert.IsNull(none, "never offers the same or an older version");
    }

    [TestMethod]
    public async Task ReleaseSource_RejectsManifestVersionMismatch()
    {
        var (handler, source) = CreateSource($"[{Release("v2.0.0")}]");
        handler.Add("https://dl/v2.0.0/update-manifest.json", Encoding.UTF8.GetBytes(Manifest("1.9.0", ("Navigator-1.9.0-linux-x64.AppImage", "linux-x64", AssetKinds.AppImage)).Serialize()));
        Assert.IsNull(await source.FindNewerReleaseAsync(SemVersion.Parse("1.0.0"), false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReleaseSource_SignedBuildsRequireValidSignature()
    {
        var (privateKey, publicKey) = ManifestSignature.CreateKeyPair();
        var manifest = Encoding.UTF8.GetBytes(Manifest("2.0.0", ("Navigator-2.0.0-linux-x64.AppImage", "linux-x64", AssetKinds.AppImage)).Serialize());

        var (unsignedHandler, unsignedSource) = CreateSource($"[{Release("v2.0.0")}]", publicKey);
        unsignedHandler.Add("https://dl/v2.0.0/update-manifest.json", manifest);
        Assert.IsNull(await unsignedSource.FindNewerReleaseAsync(SemVersion.Parse("1.0.0"), false, CancellationToken.None), "unsigned release rejected");

        var (handler, source) = CreateSource($"[{Release("v2.0.0", signed: true)}]", publicKey);
        handler.Add("https://dl/v2.0.0/update-manifest.json", manifest);
        handler.Add("https://dl/v2.0.0/update-manifest.json.sig", Encoding.ASCII.GetBytes(ManifestSignature.Sign(manifest, privateKey)));
        var found = await source.FindNewerReleaseAsync(SemVersion.Parse("1.0.0"), false, CancellationToken.None);
        Assert.IsTrue(found?.SignatureVerified);

        var (_, otherPublic) = ManifestSignature.CreateKeyPair();
        var (badHandler, badSource) = CreateSource($"[{Release("v2.0.0", signed: true)}]", otherPublic);
        badHandler.Add("https://dl/v2.0.0/update-manifest.json", manifest);
        badHandler.Add("https://dl/v2.0.0/update-manifest.json.sig", Encoding.ASCII.GetBytes(ManifestSignature.Sign(manifest, privateKey)));
        Assert.IsNull(await badSource.FindNewerReleaseAsync(SemVersion.Parse("1.0.0"), false, CancellationToken.None), "wrong key rejected");
    }

    [TestMethod]
    public async Task Downloader_VerifiesHashAndDeletesCorruptFiles()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("package-bytes");
        var asset = new ManifestAsset { Name = "pkg.zip", Size = payload.Length, Sha256 = await FileHasher.ComputeAsync(new MemoryStream(payload), HashKind.Sha256) };
        var handler = new FakeHttpHandler();
        handler.Add("https://dl/pkg.zip", payload);
        var downloader = new UpdateDownloader(new HttpClient(handler));

        var file = await downloader.DownloadAsync(new Uri("https://dl/pkg.zip"), asset, temp.Path, null, CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(file));

        var corrupt = new ManifestAsset { Name = "bad.zip", Size = payload.Length, Sha256 = new string('0', 64) };
        handler.Add("https://dl/bad.zip", payload);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => downloader.DownloadAsync(new Uri("https://dl/bad.zip"), corrupt, temp.Path, null, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "bad.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "bad.zip.part")));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => downloader.DownloadAsync(new Uri("http://dl/pkg.zip"), asset, temp.Path, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Downloader_ResumesPartialDownloads()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("0123456789abcdef");
        var asset = new ManifestAsset { Name = "p.bin", Size = payload.Length, Sha256 = await FileHasher.ComputeAsync(new MemoryStream(payload), HashKind.Sha256) };
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "p.bin.part"), payload[..6]);
        var handler = new FakeHttpHandler();
        handler.Add("https://dl/p.bin", request =>
        {
            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(payload[from..]) };
        });

        var file = await new UpdateDownloader(new HttpClient(handler)).DownloadAsync(new Uri("https://dl/p.bin"), asset, temp.Path, null, CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(file));
    }
}

[TestClass]
public sealed class ReleaseToolTests
{
    [TestMethod]
    public void NextVersion_IncrementsPatchWithinSeries()
    {
        Assert.AreEqual("1.0.0", ReleaseLogic.ComputeNextVersion(1, 0, [], VersionBump.Patch).ToString());
        Assert.AreEqual("1.0.3", ReleaseLogic.ComputeNextVersion(1, 0, ["v1.0.0", "v1.0.2", "v1.0.1", "notes"], VersionBump.Patch).ToString());
        Assert.AreEqual("1.1.0", ReleaseLogic.ComputeNextVersion(1, 0, ["v1.0.7"], VersionBump.Minor).ToString());
        Assert.AreEqual("2.0.0", ReleaseLogic.ComputeNextVersion(1, 0, ["v1.4.2"], VersionBump.Major).ToString());
    }

    [TestMethod]
    public void NextVersion_NeverGoesBackwards()
    {
        // version.json still says 1.0 but 1.2.x already exists: never publish 1.0.x again.
        Assert.AreEqual("1.2.6", ReleaseLogic.ComputeNextVersion(1, 0, ["v1.0.9", "v1.2.5"], VersionBump.Patch).ToString());
        Assert.AreEqual("1.3.0", ReleaseLogic.ComputeNextVersion(1, 3, ["v1.2.5"], VersionBump.Patch).ToString());
        Assert.AreEqual("1.0.4", ReleaseLogic.ComputeNextVersion(1, 0, ["v1.0.3", "v1.0.4-beta.1"], VersionBump.Patch).ToString());
    }

    [TestMethod]
    public void Labels_SelectBump()
    {
        Assert.AreEqual(VersionBump.Patch, ReleaseLogic.BumpFromLabels(["bug"]));
        Assert.AreEqual(VersionBump.Minor, ReleaseLogic.BumpFromLabels(["Release:Minor"]));
        Assert.AreEqual(VersionBump.Major, ReleaseLogic.BumpFromLabels(["release:minor", "release:major"]));
    }

    [TestMethod]
    [DataRow("Navigator-1.0.0-win-x64-setup.exe", "win-x64", AssetKinds.Installer)]
    [DataRow("Navigator-1.0.0-win-arm64-portable.zip", "win-arm64", AssetKinds.Portable)]
    [DataRow("Navigator-1.0.0-linux-x64.AppImage", "linux-x64", AssetKinds.AppImage)]
    [DataRow("Navigator-1.0.0-linux-arm64.tar.gz", "linux-arm64", AssetKinds.Portable)]
    [DataRow("Navigator-1.0.0-osx-arm64.zip", "osx-arm64", AssetKinds.AppArchive)]
    [DataRow("Navigator-1.0.0-osx-x64.dmg", "osx-x64", AssetKinds.DiskImage)]
    public void ClassifyAsset_RecognisesPackages(string name, string rid, string kind)
        => Assert.AreEqual((rid, kind), ReleaseLogic.ClassifyAsset(name, "1.0.0"));

    [TestMethod]
    public void ClassifyAsset_IgnoresOtherFiles()
    {
        Assert.IsNull(ReleaseLogic.ClassifyAsset("SHA256SUMS.txt", "1.0.0"));
        Assert.IsNull(ReleaseLogic.ClassifyAsset("Navigator-0.9.0-win-x64-setup.exe", "1.0.0"));
        Assert.IsNull(ReleaseLogic.ClassifyAsset("Navigator-1.0.0-win-x64.zip", "1.0.0"));
    }

    [TestMethod]
    public void HighestStable_IgnoresPrereleases()
        => Assert.AreEqual("1.4.0", ReleaseLogic.HighestStable(["v1.2.0", "v1.4.0", "v2.0.0-rc.1", "junk"])!.ToString());

    [TestMethod]
    public async Task Manifest_BuildAndVerify()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Navigator-1.0.0-linux-x64.AppImage", "appimage");
        temp.CreateFile("Navigator-1.0.0-win-x64-setup.exe", "setup");
        temp.CreateFile("README.txt", "ignored");

        var manifest = await ReleaseLogic.BuildManifestAsync("1.0.0", "sha", "stable", temp.Path, "notes", DateTimeOffset.UnixEpoch);
        Assert.AreEqual(2, manifest.Assets.Count);
        Assert.AreEqual(0, (await ReleaseLogic.VerifyAsync(manifest, temp.Path, ["linux-x64", "win-x64"])).Count);

        var missingRid = await ReleaseLogic.VerifyAsync(manifest, temp.Path, ["osx-arm64"]);
        Assert.AreEqual(1, missingRid.Count);

        File.AppendAllText(temp.Combine("Navigator-1.0.0-win-x64-setup.exe"), "tampered");
        Assert.IsTrue((await ReleaseLogic.VerifyAsync(manifest, temp.Path, [])).Any(p => p.Contains("win-x64-setup", StringComparison.Ordinal)));

        var sums = await ReleaseLogic.BuildChecksumsAsync(temp.Path);
        StringAssert.Contains(sums, "  README.txt\n");
    }
}
