using Microsoft.Extensions.Logging;
using Navigator.Core.Platform;
using Navigator.Plugins;
using Navigator.Presentation.Localization;
using Navigator.Presentation.Panes;

namespace Navigator.Presentation.Plugins;

/// <summary>Connects the plugin host to the application: dialogs, volumes, localization and logging.</summary>
public sealed class PluginHostServices(PluginUi ui, IVolumeProvider volumes, IPlatformInfo platform, Localizer localizer, ILoggerFactory loggers) : IPluginHostServices
{
    public IPluginUi Ui { get; } = ui;

    public IVolumeProvider Volumes { get; } = volumes;

    public IPlatformInfo Platform { get; } = platform;

    public string Language => localizer.Language;

    public string GetString(string key) => localizer[key];

    public VolumeDisplay DescribeVolume(VolumeInfo volume)
    {
        var item = HomeViewModel.CreateVolumeItem(volume, localizer);
        return new VolumeDisplay(item.Name, item.Detail, item.IconKey, item.SystemIconKey);
    }

    public IPluginLog CreateLog(string pluginId) => new PluginLog(loggers.CreateLogger("Plugin." + pluginId));

    private sealed class PluginLog(ILogger logger) : IPluginLog
    {
        public void Info(string message)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("{Message}", message);
        }

        public void Warning(string message, Exception? exception = null) => logger.LogWarning(exception, "{Message}", message);

        public void Error(string message, Exception? exception = null) => logger.LogError(exception, "{Message}", message);
    }
}
