using CommunityToolkit.Mvvm.Input;
using Navigator.Core.Platform;

namespace Navigator.Presentation.Services;

/// <summary>
/// Eject, disconnect and "map network drive" for the sidebar and Home: the system work is done by
/// <see cref="IDriveService"/>, this adds the questions and messages around it. Shared by every window.
/// </summary>
public sealed class DriveActions(PaneServices services)
{
    private IDriveService Drives => services.ItemActions.Drives;

    public bool CanEject(VolumeInfo volume) => Drives.CanEject(volume);

    public bool CanDisconnect(VolumeInfo volume) => Drives.CanDisconnect(volume);

    public bool CanConnectNetworkDrive => Drives.CanConnectNetworkDrive;

    /// <summary>"Map network drive" on Windows (the system dialog), "Connect to server" elsewhere.</summary>
    public string ConnectTitle => services.Localizer[Drives.AsksForNetworkAddress ? "Command.ConnectToServer" : "Command.MapNetworkDrive"];

    /// <summary>The context menu actions of a drive row (sidebar).</summary>
    public IReadOnlyList<Shell.SidebarCommandViewModel> CommandsFor(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var commands = new List<Shell.SidebarCommandViewModel>(2);
        if (Drives.CanEject(volume))
            commands.Add(new(services.Localizer["Command.Eject"], "Eject", false, new AsyncRelayCommand(() => EjectAsync(volume))));
        if (Drives.CanDisconnect(volume))
            commands.Add(new(services.Localizer["Command.DisconnectNetworkDrive"], "LanDisconnect", true, new AsyncRelayCommand(() => DisconnectAsync(volume))));
        return commands;
    }

    /// <summary>Ejects, then confirms or explains; true when the drive is gone.</summary>
    public async Task<bool> EjectAsync(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var name = DisplayName(volume);
        var text = services.Localizer;
        if (await Drives.EjectAsync(volume).ConfigureAwait(true))
        {
            services.Notifications.Show(new InAppNotification(text["Notify.Ejected"], text.Format("Notify.SafeToRemove", name), NotificationSeverity.Success));
            return true;
        }

        await services.Dialogs.ShowMessageAsync(text["Error.EjectFailedTitle"], text.Format("Error.EjectFailed", name)).ConfigureAwait(true);
        return false;
    }

    public async Task<bool> DisconnectAsync(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var text = services.Localizer;
        if (await Drives.DisconnectAsync(volume).ConfigureAwait(true))
            return true;
        await services.Dialogs.ShowMessageAsync(text["Error.DisconnectFailedTitle"], text.Format("Error.DisconnectFailed", DisplayName(volume))).ConfigureAwait(true);
        return false;
    }

    /// <summary>
    /// Windows shows its own "Map Network Drive" dialog. Elsewhere Navigator asks for the address (smb://, afp://,
    /// nfs://, sftp://, dav://…) and, when the server wants them, for a user name and password (passed on once, never kept).
    /// </summary>
    public async Task ConnectNetworkDriveAsync()
    {
        var text = services.Localizer;
        string? address = null;
        if (Drives.AsksForNetworkAddress)
        {
            address = await services.Dialogs.PromptTextAsync(new TextInputRequest(
                text["Command.ConnectToServer"], text["Dialog.ServerAddressLabel"], "smb://", text["Dialog.Connect"], ValidateAddress)).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(address))
                return;
        }

        var result = await Drives.ConnectNetworkDriveAsync(address, null, services.Windows.ActiveWindowHandle).ConfigureAwait(true);
        if (result == NetworkConnectResult.NeedsCredentials)
        {
            var values = await services.Dialogs.ShowFormAsync(new FormRequest(text["Dialog.SignInTitle"], text["Dialog.Connect"],
            [
                new FormField("user", text["Dialog.UserNameLabel"], FormFieldKind.Text, Environment.UserName, IsRequired: true, [], null, null),
                new FormField("password", text["Dialog.PasswordLabel"], FormFieldKind.Password, string.Empty, IsRequired: false, [], null, null),
                new FormField("domain", text["Dialog.DomainLabel"], FormFieldKind.Text, string.Empty, IsRequired: false, [], null, null),
            ])).ConfigureAwait(true);
            if (values is null)
                return;
            var credentials = new NetworkCredentials(values.GetValueOrDefault("user", string.Empty), values.GetValueOrDefault("password", string.Empty),
                values.GetValueOrDefault("domain") is { Length: > 0 } domain ? domain : null);
            result = await Drives.ConnectNetworkDriveAsync(address, credentials, services.Windows.ActiveWindowHandle).ConfigureAwait(true);
        }

        if (result is NetworkConnectResult.Failed or NetworkConnectResult.NeedsCredentials)
            await services.Dialogs.ShowMessageAsync(text["Error.ConnectFailedTitle"], text.Format("Error.ConnectFailed", address ?? string.Empty)).ConfigureAwait(true);
    }

    private string? ValidateAddress(string value)
    {
        var trimmed = value.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && !uri.IsFile
            ? null
            : services.Localizer["Dialog.ServerAddressInvalid"];
    }

    private string DisplayName(VolumeInfo volume)
        => OperatingSystem.IsWindows()
            ? Panes.HomeViewModel.WindowsVolumeName(volume, services.Localizer)
            : string.IsNullOrWhiteSpace(volume.Label) ? volume.RootPath : volume.Label;
}
