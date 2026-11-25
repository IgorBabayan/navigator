using System.Text.Json;
using System.Text.Json.Serialization;
using Navigator.Core.Settings;

namespace Navigator.Core.Platform;

/// <summary>
/// What the global shortcut agent (<c>Navigator --shortcut-agent</c>) does: hold the system-wide shortcut while Navigator is
/// not running and start or activate Navigator when it is pressed. Written by the app; the agent exits as soon as the
/// file changes and the app starts a fresh one, so the agent never needs to re-read settings.
/// </summary>
public sealed class ShortcutAgentConfig
{
    /// <summary>Resolved gesture as the command registry writes it ("Ctrl+Shift+Alt+E").</summary>
    public string Gesture { get; set; } = string.Empty;

    /// <summary>What the agent starts when no Navigator answers on the activation channel.</summary>
    public string Executable { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];
}

/// <summary>The agent's registration result for the gesture in <see cref="ShortcutAgentConfig"/>, read by Settings.</summary>
public sealed class ShortcutAgentStatus
{
    public string Gesture { get; set; } = string.Empty;

    public HotkeyRegistrationResult Result { get; set; }

    public int ProcessId { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ShortcutAgentConfig))]
[JsonSerializable(typeof(ShortcutAgentStatus))]
internal sealed partial class ShortcutAgentJsonContext : JsonSerializerContext;

/// <summary>Files shared by the app and the agent.</summary>
public sealed class ShortcutAgentFiles(AppDirectories directories)
{
    /// <summary>Command-line switch that runs the agent instead of the app.</summary>
    public const string AgentSwitch = "--shortcut-agent";

    /// <summary>Command-line switch that activates a running Navigator (or starts one).</summary>
    public const string ActivateSwitch = "--activate";

    public string ConfigFile { get; } = Path.Combine(directories.ConfigDirectory, "shortcut-agent.json");

    public string StatusFile { get; } = Path.Combine(directories.CacheDirectory, "shortcut-agent-status.json");

    /// <summary>Held open by the running agent: a second agent exits at once.</summary>
    public string LockFile { get; } = Path.Combine(directories.CacheDirectory, "shortcut-agent.lock");

    public ShortcutAgentConfig? ReadConfig() => Read(ConfigFile, ShortcutAgentJsonContext.Default.ShortcutAgentConfig);

    public ShortcutAgentStatus? ReadStatus() => Read(StatusFile, ShortcutAgentJsonContext.Default.ShortcutAgentStatus);

    /// <summary>Writes the config only when it differs (an unchanged file keeps the running agent).</summary>
    public bool WriteConfig(ShortcutAgentConfig config)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, ShortcutAgentJsonContext.Default.ShortcutAgentConfig);
        try
        {
            if (File.Exists(ConfigFile) && File.ReadAllBytes(ConfigFile).AsSpan().SequenceEqual(bytes))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rewrite below.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        AtomicFile.WriteAllBytes(ConfigFile, bytes, keepBackup: false);
        return true;
    }

    /// <summary>Removes the config: a running agent exits.</summary>
    public void DeleteConfig() => TryDelete(ConfigFile);

    public void WriteStatus(ShortcutAgentStatus status)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFile)!);
        AtomicFile.WriteAllBytes(StatusFile, JsonSerializer.SerializeToUtf8Bytes(status, ShortcutAgentJsonContext.Default.ShortcutAgentStatus), keepBackup: false);
    }

    public void DeleteStatus() => TryDelete(StatusFile);

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), type) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten next time.
        }
    }
}

/// <summary>Starts a command when the user signs in to the OS (Windows Run key, XDG autostart, macOS LaunchAgent).</summary>
public interface ILoginItemService
{
    bool IsSupported { get; }

    /// <summary>Creates or updates the login item (idempotent; unchanged entries are not rewritten).</summary>
    bool Register(string executable, IReadOnlyList<string> arguments);

    void Unregister();
}

/// <summary>
/// A shortcut registered with the desktop itself (GNOME custom keyboard shortcut): works on Wayland, where apps cannot
/// grab keys, and needs no running process. The desktop runs <c>Navigator --activate</c>.
/// </summary>
public interface IDesktopShortcutService
{
    bool IsSupported { get; }

    Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    Task UnregisterAsync(CancellationToken cancellationToken = default);
}
