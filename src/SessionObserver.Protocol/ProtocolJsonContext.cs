using System.Text.Json.Serialization;
using SessionObserver.Protocol.Messages;
using SessionObserver.Protocol.Models;

namespace SessionObserver.Protocol;

/// <summary>Source-generated serialization metadata for every model and message.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(SessionInfo))]
public partial class ProtocolJsonContext : JsonSerializerContext;
