using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Navigator.App.Services;
using Navigator.Core.IO;
using Navigator.Core.Operations;
using Navigator.Core.Platform;
using Navigator.Core.Scheduling;
using Navigator.Core.Settings;
using Navigator.Core.Tags;
using Navigator.Platform;
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
        services.AddSingleton<IVolumeProvider>(provider => provider.GetRequiredService<VolumeProvider>());
        services.AddSingleton<IVolumeLocator, VolumeLocator>();
        services.AddSingleton(_ => PlatformServices.CreateTrash());
        services.AddSingleton(_ => PlatformServices.CreateLauncher());
        services.AddSingleton<INativeNotificationService, NativeNotificationService>();
        services.AddSingleton<IFileOwnershipProvider, FileOwnershipProvider>();

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
        services.AddSingleton<PinnedFolders>();
        services.AddSingleton<PaneServices>();

        // Updates
        services.AddSingleton(_ => UpdateConfiguration.FromAssembly(Assembly.GetEntryAssembly()));
        services.AddSingleton(provider => new UpdateService(
            provider.GetRequiredService<UpdateConfiguration>(),
            provider.GetRequiredService<IPlatformInfo>(),
            directories.UpdateDirectory));
        services.AddSingleton<UpdateViewModel>();

        // Per window
        services.AddTransient<ShellViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
