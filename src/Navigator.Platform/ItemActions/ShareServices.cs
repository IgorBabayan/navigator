using System.Runtime.Versioning;
using Navigator.Core.Platform;
using Navigator.Platform.Native;

namespace Navigator.Platform.ItemActions;

/// <summary>
/// macOS: the services the system offers for the files (AirDrop, Mail, Messages, Notes…), from NSSharingService.
/// AppKit wants the main thread, which is the UI thread: both members run synchronously on the caller's thread.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacShareService : IShareService
{
    public bool IsSupported => true;

    public Task<IReadOnlyList<ShareTarget>> GetTargetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var pool = MacObjC.Pool();
        var targets = new List<ShareTarget>();
        var services = Services(paths);
        var title = MacObjC.Selector("title");
        var count = services == 0 ? 0 : MacObjC.Count(services);
        for (nuint i = 0; i < count; i++)
        {
            if (MacObjC.ToManaged(MacObjC.Send(MacObjC.At(services, i), title)) is { Length: > 0 } name)
                targets.Add(new ShareTarget(name, name, "Share"));
        }

        return Task.FromResult<IReadOnlyList<ShareTarget>>(targets);
    }

    public Task<bool> ShareAsync(IReadOnlyList<string> paths, ShareTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(target);
        using var pool = MacObjC.Pool();
        var items = MacObjC.FileUrls(paths);
        var services = MacObjC.Send(MacObjC.Class("NSSharingService"), MacObjC.Selector("sharingServicesForItems:"), items);
        var title = MacObjC.Selector("title");
        var count = services == 0 ? 0 : MacObjC.Count(services);
        for (nuint i = 0; i < count; i++)
        {
            var service = MacObjC.At(services, i);
            if (MacObjC.ToManaged(MacObjC.Send(service, title)) == target.Id)
            {
                MacObjC.Send(service, MacObjC.Selector("performWithItems:"), items);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private static nint Services(IReadOnlyList<string> paths)
        => MacObjC.Send(MacObjC.Class("NSSharingService"), MacObjC.Selector("sharingServicesForItems:"), MacObjC.FileUrls(paths));
}

/// <summary>Linux has no system share sheet: files go to the user's mail composer as attachments (xdg-email).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxShareService : IShareService
{
    /// <summary>Id of the mail target (the presentation layer shows it under a localized name).</summary>
    public const string MailTarget = "mail";

    private readonly Lazy<string?> _mail = new(() => ProcessRunner.FindOnPath("xdg-email"));

    public bool IsSupported => _mail.Value is not null;

    public Task<IReadOnlyList<ShareTarget>> GetTargetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ShareTarget>>(_mail.Value is null ? [] : [new ShareTarget(MailTarget, "Email", "Email")]);

    public Task<bool> ShareAsync(IReadOnlyList<string> paths, ShareTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(target);
        if (_mail.Value is not { } mail || target.Id != MailTarget)
            return Task.FromResult(false);
        var arguments = new List<string>(paths.Count * 2);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                arguments.Add("--attach");
                arguments.Add(Path.GetFullPath(path));
            }
        }

        return Task.FromResult(arguments.Count > 0 && ProcessRunner.Start(mail, arguments));
    }
}
