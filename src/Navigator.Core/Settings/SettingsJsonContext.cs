using System.Text.Json.Serialization;

namespace Navigator.Core.Settings;

/// <summary>
/// Source-generated serializers: no reflection at startup (faster first load, trimming/AOT safe).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(TagAssignments))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
