using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
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
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"'{raw}' is not a valid JSON number.");
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException($"'{raw}' is not a valid finite JSON number.");
        }

        if (value == 0d)
        {
            return "0";
        }

        var text = SerializeDouble(value).Replace("E", "e", StringComparison.Ordinal);
        return NormalizeEcmaNumberText(text);
    }
    private static string SerializeDouble(double value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       SkipValidation = false
                   }))
        {
            writer.WriteNumberValue(value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string NormalizeEcmaNumberText(string text)
    {
        if (!text.Contains('e'))
        {
            return TrimDecimal(text);
        }

        var pieces = text.Split('e');
        var mantissa = TrimDecimal(pieces[0]);
        var exponent = int.Parse(pieces[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        if (exponent is >= -6 and < 21)
        {
            return ExpandExponent(mantissa, exponent);
        }

        return $"{mantissa}e{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string ExpandExponent(string mantissa, int exponent)
    {
        var sign = mantissa.StartsWith("-", StringComparison.Ordinal) ? "-" : string.Empty;
        var unsignedMantissa = sign.Length == 0 ? mantissa : mantissa[1..];
        var decimalIndex = unsignedMantissa.IndexOf('.');
        var digits = unsignedMantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var digitsBeforeDecimal = decimalIndex >= 0 ? decimalIndex : unsignedMantissa.Length;
        var newDecimalIndex = digitsBeforeDecimal + exponent;

        if (newDecimalIndex <= 0)
        {
            return sign + TrimDecimal($"0.{new string('0', -newDecimalIndex)}{digits}");
        }

        if (newDecimalIndex >= digits.Length)
        {
            return sign + digits + new string('0', newDecimalIndex - digits.Length);
        }

        return sign + TrimDecimal($"{digits[..newDecimalIndex]}.{digits[newDecimalIndex..]}");
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
