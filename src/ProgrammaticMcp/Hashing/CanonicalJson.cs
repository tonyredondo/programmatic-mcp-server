using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProgrammaticMcp;

/// <summary>
/// Canonical JSON helpers used for stable hashing.
/// </summary>
public static class CanonicalJson
{
    /// <summary>
    /// Serializes a JSON node into canonical JSON text.
    /// </summary>
    public static string Serialize(JsonNode? node)
    {
        return node switch
        {
            null => "null",
            JsonObject jsonObject => SerializeObject(jsonObject),
            JsonArray jsonArray => SerializeArray(jsonArray),
            JsonValue jsonValue => SerializeValue(jsonValue),
            _ => throw new InvalidOperationException($"Unsupported JSON node type '{node.GetType()}'.")
        };
    }

    /// <summary>
    /// Computes the canonical SHA-256 hash of a JSON node.
    /// </summary>
    public static string Sha256(JsonNode? node)
    {
        var canonical = Serialize(node);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SerializeObject(JsonObject jsonObject)
    {
        var ordered = jsonObject.OrderBy(static pair => pair.Key, StringComparer.Ordinal);
        return "{" + string.Join(",", ordered.Select(static pair => $"{JsonSerializer.Serialize(pair.Key)}:{Serialize(pair.Value)}")) + "}";
    }

    private static string SerializeArray(JsonArray jsonArray)
    {
        return "[" + string.Join(",", jsonArray.Select(Serialize)) + "]";
    }

    private static string SerializeValue(JsonValue jsonValue)
    {
        var raw = jsonValue.ToJsonString();
        if (raw.Length == 0)
        {
            return raw;
        }

        if (raw[0] == '"' || raw is "true" or "false" or "null")
        {
            return raw;
        }

        return NormalizeNumber(raw);
    }

    /// <summary>
    /// Normalizes a numeric literal into canonical JSON form.
    /// </summary>
    public static string NormalizeNumber(string raw)
    {
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            if (decimalValue == decimal.Zero)
            {
                return "0";
            }

            return TrimDecimal(decimalValue.ToString(CultureInfo.InvariantCulture));
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"'{raw}' is not a valid JSON number.");
        }

        if (value == 0d)
        {
            return "0";
        }

        var text = value.ToString("G17", CultureInfo.InvariantCulture)
            .Replace("E", "e", StringComparison.Ordinal);

        if (text.Contains('e'))
        {
            var pieces = text.Split('e');
            var exponent = pieces[1];
            var sign = exponent.StartsWith("-", StringComparison.Ordinal) ? "-" : string.Empty;
            exponent = exponent.TrimStart('+', '-').TrimStart('0');
            exponent = exponent.Length == 0 ? "0" : exponent;
            text = $"{TrimDecimal(pieces[0])}e{sign}{exponent}";
        }
        else
        {
            text = TrimDecimal(text);
        }

        return text;
    }

    private static string TrimDecimal(string value)
    {
        if (!value.Contains('.'))
        {
            return value;
        }

        value = value.TrimEnd('0');
        return value.EndsWith(".", StringComparison.Ordinal) ? value[..^1] : value;
    }
}

/// <summary>
/// Calculates capability and approval hashes from canonical payloads.
/// </summary>
public static class CapabilityVersionCalculator
{
    /// <summary>
    /// Calculates the capability version hash for a catalog snapshot.
    /// </summary>
    public static string Calculate(IReadOnlyList<CapabilityDefinition> capabilities, string generatedTypeScript)
    {
        var payload = new JsonObject
        {
            ["runtimeContractVersion"] = ProgrammaticContractConstants.GeneratedRuntimeContractVersion,
            ["generatedTypeScript"] = generatedTypeScript,
            ["capabilities"] = new JsonArray(capabilities.Select(ToNode).ToArray())
        };

        return CanonicalJson.Sha256(payload);
    }

    /// <summary>
    /// Calculates the canonical hash for mutation arguments.
    /// </summary>
    public static string CalculateArgsHash(JsonObject args)
    {
        return CanonicalJson.Sha256(args);
    }

    private static JsonNode ToNode(CapabilityDefinition capability)
    {
        var node = new JsonObject
        {
            ["apiPath"] = capability.ApiPath,
            ["description"] = capability.Description,
            ["signature"] = capability.Signature,
            ["isMutation"] = capability.IsMutation,
            ["usageGuidance"] = new JsonObject
            {
                ["useWhen"] = new JsonArray(capability.UsageGuidance.UseWhen.Select(static value => (JsonNode?)value).ToArray()),
                ["doNotUseWhen"] = new JsonArray(capability.UsageGuidance.DoNotUseWhen.Select(static value => (JsonNode?)value).ToArray()),
                ["notes"] = new JsonArray(capability.UsageGuidance.Notes.Select(static value => (JsonNode?)value).ToArray())
            },
            ["inputSchema"] = capability.Input.Schema.DeepClone(),
            ["resultSchema"] = capability.Result.Schema.DeepClone()
        };

        if (capability.ApplyResultSchema is not null)
        {
            node["applyResultSchema"] = capability.ApplyResultSchema.DeepClone();
        }

        if (capability.PreviewPayloadSchema is not null)
        {
            node["previewPayloadSchema"] = capability.PreviewPayloadSchema.DeepClone();
        }

        return node;
    }
}

/// <summary>
/// Internal JSON serializer contract used for stable round-tripping.
/// </summary>
internal static class JsonSerializerContract
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static JsonNode? SerializeToNode<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options);
    }

    public static T DeserializeFromNode<T>(JsonNode? node)
    {
        return node.Deserialize<T>(Options)
            ?? throw new InvalidOperationException($"Unable to deserialize JSON payload into '{typeof(T)}'.");
    }
}
