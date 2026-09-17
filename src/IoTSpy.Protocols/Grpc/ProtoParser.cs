using System.Text.Json;
using System.Text.RegularExpressions;

namespace IoTSpy.Protocols.Grpc;

/// <summary>
/// Parses a .proto file (proto3/proto2) and extracts field-number → field-name mappings
/// per message, plus a global flat map (field number → name) for schema-less decoding.
/// </summary>
public static partial class ProtoParser
{
    // Matches: [repeated|optional|required] <type> <name> = <number> [options] ;
    [GeneratedRegex(@"^\s*(?:repeated|optional|required|map\s*<[^>]+>)?\s*\w[\w.<>, ]*\s+(\w+)\s*=\s*(\d+)\s*[;\[{]",
        RegexOptions.Multiline)]
    private static partial Regex FieldLineRegex();

    [GeneratedRegex(@"message\s+(\w+)\s*\{", RegexOptions.Multiline)]
    private static partial Regex MessageHeaderRegex();

    /// <summary>
    /// Returns a flat dictionary mapping field number → field name, merged across all messages.
    /// Conflicts (same number in different messages) keep the first definition encountered.
    /// </summary>
    public static Dictionary<int, string> ParseFlatMap(string protoText)
    {
        var result = new Dictionary<int, string>();
        foreach (var (_, fields) in ParsePerMessage(protoText))
        {
            foreach (var (number, name) in fields)
                result.TryAdd(number, name);
        }
        return result;
    }

    /// <summary>
    /// Returns per-message field maps: message name → (field number → field name).
    /// </summary>
    public static Dictionary<string, Dictionary<int, string>> ParsePerMessage(string protoText)
    {
        var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        var messageMatches = MessageHeaderRegex().Matches(protoText);

        if (messageMatches.Count == 0)
        {
            // No message blocks — try parsing the whole file as a flat list of fields
            result["(default)"] = ExtractFields(protoText);
            return result;
        }

        for (var i = 0; i < messageMatches.Count; i++)
        {
            var msgName = messageMatches[i].Groups[1].Value;
            var bodyStart = messageMatches[i].Index + messageMatches[i].Length;
            var bodyEnd = i + 1 < messageMatches.Count
                ? messageMatches[i + 1].Index
                : protoText.Length;

            var body = protoText[bodyStart..bodyEnd];
            result[msgName] = ExtractFields(body);
        }

        return result;
    }

    private static Dictionary<int, string> ExtractFields(string block)
    {
        var fields = new Dictionary<int, string>();
        foreach (Match m in FieldLineRegex().Matches(block))
        {
            var name = m.Groups[1].Value;
            if (int.TryParse(m.Groups[2].Value, out var number) && number > 0)
                fields.TryAdd(number, name);
        }
        return fields;
    }

    /// <summary>
    /// Serialises a flat field map to a compact JSON string for storage.
    /// </summary>
    public static string ToJson(Dictionary<int, string> map) => JsonSerializer.Serialize(map);

    /// <summary>
    /// Deserialises the compact JSON produced by <see cref="ToJson"/>.
    /// </summary>
    public static Dictionary<int, string> FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, string>>(json) ?? new Dictionary<int, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, string>();
        }
    }
}
