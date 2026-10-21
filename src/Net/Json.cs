using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Frd;

// NativeAOT cannot build converters for constructor-bound types at run time, so every type FRD reads from JSON has generated metadata.
// Anything not listed (the anonymous report objects, which are only written) falls back to reflection, which is rooted in the project file.
[JsonSourceGenerationOptions(IncludeFields = false)]
[JsonSerializable(typeof(ControlCommand))]
[JsonSerializable(typeof(ControlResult))]
[JsonSerializable(typeof(ClipboardManifest))]
[JsonSerializable(typeof(CursorUpdate))]
[JsonSerializable(typeof(InputRequest))]
[JsonSerializable(typeof(InputReply))]
[JsonSerializable(typeof(RemoteRequest))]
[JsonSerializable(typeof(RemoteReply))]
[JsonSerializable(typeof(RemoteChallenge))]
[JsonSerializable(typeof(AnnotationMessage))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(bool?))]
sealed partial class PlainJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(AppConfiguration))]
[JsonSerializable(typeof(CodecProbeReport))]
[JsonSerializable(typeof(CodecDecodeReport))]
[JsonSerializable(typeof(CodecDecodeRequest))]
sealed partial class ConfigJsonContext : JsonSerializerContext { }

static class FrdJson
{
    public static readonly JsonSerializerOptions Plain = new() { TypeInfoResolver = JsonTypeInfoResolver.Combine(PlainJsonContext.Default, new DefaultJsonTypeInfoResolver()) };
    public static readonly JsonSerializerOptions Config = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ConfigJsonContext.Default, new DefaultJsonTypeInfoResolver())
    };
}
