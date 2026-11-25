using System.Text.Json.Serialization;

namespace CleanSlate.Models;

/// <summary>
/// Compile-time System.Text.Json source-generation context for zero-reflection startup deserialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(DockItem))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}
