using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Navigator.App.Services;
using Navigator.Core.Caching;
using Navigator.Core.IO;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Core.Scheduling;
using Navigator.Core.Settings;
using Navigator.Core.Tags;
using Navigator.Platform;
using Navigator.Plugin.Network;
using Navigator.Plugins;
using Navigator.Presentation.Plugins;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Services;
using Navigator.Presentation.Settings;
using Navigator.Presentation.SettingsPages;
using Navigator.Presentation.Shell;
using Navigator.Presentation.Updates;
using Navigator.Updater;

namespace Navigator.App.Infrastructure;

/// <summary>
/// Dependency injection setup. Everything is a lazily created singleton except per-window view models,
/// so services that are not needed for the first frame (updater, thumbnails, trash) cost nothing at startup.
/// </summary>
internal static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var directories = AppDirectories.Resolve();
        directories.EnsureCreated();

        var settingsStore = new SettingsStore(directories.SettingsFile);
        var services = new ServiceCollection();

        services.AddSingleton(directories);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace); // filtered at runtime by LogLevelSwitch
            builder.AddProvider(new RotatingFileLoggerProvider(directories.LogDirectory));
        });

        services.AddSingleton(settingsStore);
        services.AddSingleton<SettingsService>();
        services.AddSingleton(provider =>
        {
            var localizer = Localizer.Current;
            localizer.SetLanguage(provider.GetRequiredService<SettingsService>().Current.General.Language);
            return localizer;
        });

        // Platform
        services.AddSingleton<IPlatformInfo, PlatformInfo>();
        services.AddSingleton<IKnownFolderProvider, KnownFolderProvider>();
        services.AddSingleton<VolumeProvider>();
        // One shared volume snapshot for Home, the sidebar's Drives section and the Network plugin:
        // a sidebar rebuild queries the drives once, not once per section.
        services.AddSingleton(provider => new VolumeSnapshotCache(provider.GetRequiredService<VolumeProvider>()));
        services.AddSingleton<IVolumeProvider>(provider => provider.GetRequiredService<VolumeSnapshotCache>());
        services.AddSingleton<IVolumeLocator, VolumeLocator>();
        services.AddSingleton(_ => PlatformServices.CreateTrash());
        services.AddSingleton(_ => PlatformServices.CreateLauncher());
        services.AddSingleton<INativeNotificationService, NativeNotificationService>();
        services.AddSingleton<IFileOwnershipProvider, FileOwnershipProvider>();
        services.AddSingleton(provider => PlatformServices.CreateSystemIcons(provider.GetRequiredService<IPlatformInfo>(), provider.GetRequiredService<IKnownFolderProvider>()));
        services.AddSingleton(_ => PlatformServices.CreateShellMenus());
        services.AddSingleton(provider => PlatformServices.CreateNewItemTemplates(provider.GetRequiredService<IPlatformInfo>()));
        services.AddSingleton(provider => PlatformServices.CreateGlobalHotkey(provider.GetRequiredService<IPlatformInfo>()));
        services.AddSingleton(_ => PlatformServices.CreateLoginItems());
        services.AddSingleton(provider => PlatformServices.CreateDesktopShortcut(provider.GetRequiredService<IPlatformInfo>()));
        // Refresh tokens of cloud plugins: DPAPI files (Windows, machine-local next to the plugins folder, which the plugin
        // host keeps clean), Keychain (macOS), Secret Service (Linux).
        services.AddSingleton(_ => PlatformServices.CreateSecretStore(Path.Combine(Path.GetDirectoryName(directories.PluginDirectory)!, "Secrets")));

        // Engines
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<SettingsService>().Current;
            return new FileOperationQueue(settings.Operations.MaxConcurrentOperations, new FileOperationOptions
            {
                VerifyMovedFileContents = settings.Operations.VerifyMovedFileContents,
                CopyNameSuffix = provider.GetRequiredService<Localizer>()["Item.CopySuffix"],
            });
        });
        services.AddSingleton<FileOperationService>();
        services.AddSingleton(provider => new DirectoryListingCache(provider.GetRequiredService<SettingsService>().Current.Performance.DirectoryCacheEntries));
        services.AddSingleton(provider => new BackgroundWorkScheduler(provider.GetRequiredService<SettingsService>().Current.Performance.MaxBackgroundWorkers));
        services.AddSingleton(_ => new TagStore(new JsonFileStore<TagAssignments>(directories.TagsFile, SettingsJsonContext.Default.TagAssignments, () => new TagAssignments())));
        services.AddSingleton(_ => new RecentItems(Path.Combine(directories.ConfigDirectory, "recent.json")));

        // UI services
        services.AddSingleton<IUiDispatcher, AvaloniaDispatcher>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowService>(provider => provider.GetRequiredService<WindowService>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ThumbnailProvider>();
        services.AddSingleton<IThumbnailProvider>(provider => provider.GetRequiredService<ThumbnailProvider>());
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<INotificationCenter>(provider => provider.GetRequiredService<NotificationCenter>());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<PerformanceMonitor>();
        services.AddSingleton<PinnedFolders>();
        services.AddSingleton<TrashStatus>();
        services.AddSingleton<RemoteFileService>();
        services.AddSingleton<PaneServices>();
        services.AddSingleton<GlobalHotkeyController>();
        services.AddSingleton<IGlobalHotkeyStatus>(provider => provider.GetRequiredService<GlobalHotkeyController>());
        services.AddSingleton<BackgroundModeService>();

        // Updates
        services.AddSingleton(_ => UpdateConfiguration.FromAssembly(Assembly.GetEntryAssembly()));
        services.AddSingleton(provider => new UpdateService(
            provider.GetRequiredService<UpdateConfiguration>(),
            provider.GetRequiredService<IPlatformInfo>(),
            directories.UpdateDirectory));
        services.AddSingleton<UpdateViewModel>();

        // Plugins. Built-in plugins (only Network) are compiled in and registered without assembly scanning;
        // installed ones (SSH, FTP, Google Drive, OneDrive… from the plugin shop) are loaded by PluginHost.StartAsync after the first frame.
        services.AddSingleton<PluginUi>();
        services.AddSingleton<PluginHostServices>();
        services.AddSingleton(provider => new PluginHost(
            directories.PluginDirectory,
            provider.GetRequiredService<PluginHostServices>(),
            [
                new BuiltInPlugin(
                    new PluginManifest
                    {
                        Id = NetworkPlugin.Id,
                        Name = NetworkPlugin.Name,
                        Description = NetworkPlugin.Description,
                        Author = "Navigator",
                        Version = provider.GetRequiredService<UpdateConfiguration>().CurrentVersion.ToString(),
                        ApiVersion = PluginApi.Version,
                        IconKey = NetworkPlugin.IconKey,
                    },
                    static () => new NetworkPlugin()),
            ]));
        services.AddSingleton(provider =>
        {
            // Created only when the plugin shop is opened: no HTTP client exists before that.
            var configuration = provider.GetRequiredService<UpdateConfiguration>();
            var options = PluginStoreOptions.Create(configuration.Repository, configuration.PublicKey,
                Path.Combine(directories.CacheDirectory, "plugin-downloads"), Environment.GetEnvironmentVariable);
            return new PluginStoreClient(UpdateService.CreateHttpClient(configuration.CurrentVersion), options);
        });
        services.AddTransient<PluginsViewModel>();

        // Per window (ShellViewModel is created by WindowService via ActivatorUtilities so the root provider does not track it)
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
