using Navigator.Plugin.Ftp;
using Navigator.Plugins;

namespace Navigator.UnitTests;

/// <summary>Parsing of FTP replies and listings (the FTP plugin is BCL-only, so its protocol code is tested here).</summary>
[TestClass]
public sealed class FtpProtocolTests
{
    private static readonly DateTime Now = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Mlsd_ReadsFactsAndSkipsCurrentAndParentEntries()
    {
        var file = FtpListParser.ParseMlsd("type=file;size=1024;modify=20240131120500;perm=r; report 2024.pdf")!;
        Assert.AreEqual("report 2024.pdf", file.Name);
        Assert.AreEqual(1024, file.Length);
        Assert.AreEqual(new DateTime(2024, 1, 31, 12, 5, 0, DateTimeKind.Utc), file.LastWriteTimeUtc);
        Assert.IsFalse(file.IsDirectory);

        Assert.IsTrue(FtpListParser.ParseMlsd("Type=Dir;Modify=20240131120500; docs")!.IsDirectory);
        Assert.IsTrue(FtpListParser.ParseMlsd("type=OS.unix=slink:/data;size=4; link")!.IsSymbolicLink);
        Assert.IsNull(FtpListParser.ParseMlsd("type=cdir;modify=20240131120500; ."));
        Assert.IsNull(FtpListParser.ParseMlsd("type=pdir; .."));
        Assert.IsNull(FtpListParser.ParseMlsd("garbage"));
    }

    [TestMethod]
    public void UnixList_ParsesNamesWithSpacesLinksAndBothDateForms()
    {
        var recent = FtpListParser.ParseList("-rw-r--r--   1 user  group  12345 Mar 10 09:30 my file.txt", Now)!;
        Assert.AreEqual("my file.txt", recent.Name);
        Assert.AreEqual(12345, recent.Length);
        Assert.AreEqual(new DateTime(2026, 3, 10, 9, 30, 0, DateTimeKind.Utc), recent.LastWriteTimeUtc);

        // "Dec 20 10:00" seen in March is last year's December.
        Assert.AreEqual(2025, FtpListParser.ParseList("-rw-r--r-- 1 u g 1 Dec 20 10:00 old", Now)!.LastWriteTimeUtc.Year);
        Assert.AreEqual(new DateTime(2023, 1, 31, 0, 0, 0, DateTimeKind.Utc), FtpListParser.ParseList("drwxr-xr-x 2 1000 1000 4096 Jan 31  2023 dir", Now)!.LastWriteTimeUtc);

        var link = FtpListParser.ParseList("lrwxrwxrwx    1 30033    30033           4 Nov 24 19:55 link-to-docs -> docs", Now)!;
        Assert.AreEqual("link-to-docs", link.Name);
        Assert.IsTrue(link.IsSymbolicLink);
        Assert.IsFalse(link.IsDirectory);
        Assert.IsTrue(FtpListParser.ParseList("drwxr-xr-x    2 30033    30033        4096 Nov 24 19:55 docs", Now)!.IsDirectory);
    }

    [TestMethod]
    public void WindowsList_ParsesIisFormat()
    {
        var folder = FtpListParser.ParseList("01-31-24  12:00PM       <DIR>          Shared Docs", Now)!;
        Assert.IsTrue(folder.IsDirectory);
        Assert.AreEqual("Shared Docs", folder.Name);
        var file = FtpListParser.ParseList("01-31-24  01:05PM               1234 notes.txt", Now)!;
        Assert.AreEqual(1234, file.Length);
        Assert.AreEqual(new DateTime(2024, 1, 31, 13, 5, 0, DateTimeKind.Utc), file.LastWriteTimeUtc);
    }

    [TestMethod]
    public void List_IgnoresTotalsAndDotEntries_ButNotUnknownLines()
    {
        Assert.IsTrue(FtpListParser.IsIgnorable("total 12"));
        Assert.IsTrue(FtpListParser.IsIgnorable("drwxr-xr-x    3 30033    30033        4096 Nov 24 19:55 ."));
        Assert.IsNull(FtpListParser.ParseList("drwxr-xr-x    3 30033    30033        4096 Nov 24 19:55 ..", Now));
        Assert.IsFalse(FtpListParser.IsIgnorable("something the parser does not understand"));
        Assert.IsNull(FtpListParser.ParseList("something the parser does not understand", Now));
    }

    [TestMethod]
    public void PassiveReplies_YieldOnlyThePort()
    {
        Assert.IsTrue(FtpSession.TryParseEpsv("Entering Extended Passive Mode (|||6446|)", out var epsv));
        Assert.AreEqual(6446, epsv);
        Assert.IsFalse(FtpSession.TryParseEpsv("Entering Extended Passive Mode", out _));

        // The address in a PASV reply is ignored (data always goes to the control connection's host).
        Assert.IsTrue(FtpSession.TryParsePasv("Entering Passive Mode (10,0,0,9,117,48).", out var pasv));
        Assert.AreEqual((117 << 8) | 48, pasv);
        Assert.IsFalse(FtpSession.TryParsePasv("Entering Passive Mode (1,2,3)", out _));
        Assert.IsFalse(FtpSession.TryParsePasv("Entering Passive Mode (1,2,3,4,999,1)", out _));
    }

    [TestMethod]
    public void Arguments_WithLineBreaksAreRejected()
    {
        Assert.AreEqual("/a b/c", FtpSession.Argument("/a b/c"));
        var error = Assert.ThrowsExactly<RemoteFileSystemException>(() => FtpSession.Argument("x\r\nDELE /important"));
        Assert.AreEqual(RemoteErrorKind.Other, error.Kind);
    }

    [TestMethod]
    public void Replies_MapToErrorKinds()
    {
        Assert.AreEqual(RemoteErrorKind.NotFound, FtpSession.ReplyError(new FtpReply(550, "No such file or directory.", [])).Kind);
        Assert.AreEqual(RemoteErrorKind.NotFound, FtpSession.ReplyError(new FtpReply(501, "No such directory.", [])).Kind);
        Assert.AreEqual(RemoteErrorKind.AccessDenied, FtpSession.ReplyError(new FtpReply(550, "Permission denied.", [])).Kind);
        Assert.AreEqual(RemoteErrorKind.AuthenticationFailed, FtpSession.ReplyError(new FtpReply(530, "Login incorrect.", [])).Kind);
        Assert.AreEqual(RemoteErrorKind.ConnectionFailed, FtpSession.ReplyError(new FtpReply(522, "SSL connection failed: session reuse required", [])).Kind);
    }

    [TestMethod]
    public void TlsName_IsAReservedNameForIpAddresses()
    {
        Assert.AreEqual("ftp.example.com", FtpSession.TlsNameFor("ftp.example.com"));
        Assert.AreEqual("ip-192-168-1-5.navigator.invalid", FtpSession.TlsNameFor("192.168.1.5"));
        Assert.IsTrue(FtpSession.TlsNameFor("::1").EndsWith(".invalid", StringComparison.Ordinal));
    }
}
