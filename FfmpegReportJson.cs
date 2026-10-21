using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Frd;

/// <summary>
/// Writes diagnostic and regression reports (mostly anonymous objects). System.Text.Json cannot build metadata for those under NativeAOT,
/// so this walks public properties itself; the assembly is rooted, so the property getters survive trimming.
/// </summary>
static class ReportJson
{
    public static string Serialize(object? value, JsonSerializerOptions? options = null)
    {
        var camel = options != null && ReferenceEquals(options, FrdJson.Config);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, value, camel, 0);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static JsonElement ToElement(object? value) { using var document = JsonDocument.Parse(Serialize(value)); return document.RootElement.Clone(); }

    static string Name(string name, bool camel) => camel && name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;

    static void Write(Utf8JsonWriter writer, object? value, bool camel, int depth)
    {
        if (depth > 24) { writer.WriteNullValue(); return; }
        switch (value)
        {
            case null: writer.WriteNullValue(); return;
            case string text: writer.WriteStringValue(text); return;
            case bool flag: writer.WriteBooleanValue(flag); return;
            case byte or sbyte or short or ushort or int or uint or long or ulong: writer.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!); return;
            case float single: if (float.IsFinite(single)) writer.WriteNumberValue(single); else writer.WriteNullValue(); return;
            case double number: if (double.IsFinite(number)) writer.WriteNumberValue(number); else writer.WriteNullValue(); return;
            case decimal money: writer.WriteNumberValue(money); return;
            case Enum item: writer.WriteStringValue(item.ToString()); return;
            case DateTime moment: writer.WriteStringValue(moment); return;
            case DateTimeOffset moment: writer.WriteStringValue(moment); return;
            case TimeSpan span: writer.WriteStringValue(span.ToString()); return;
            case Guid id: writer.WriteStringValue(id); return;
            case JsonElement element: element.WriteTo(writer); return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary) { writer.WritePropertyName(Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? ""); Write(writer, entry.Value, camel, depth + 1); }
                writer.WriteEndObject(); return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence) Write(writer, item, camel, depth + 1);
                writer.WriteEndArray(); return;
        }
        writer.WriteStartObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || property.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)) continue;
            writer.WritePropertyName(Name(property.Name, camel));
            Write(writer, property.GetValue(value), camel, depth + 1);
        }
        writer.WriteEndObject();
    }
}
