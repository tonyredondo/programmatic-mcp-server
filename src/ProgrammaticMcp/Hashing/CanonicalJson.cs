using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProgrammaticMcp;

/// <summary>
/// Canonical JSON helpers used for stable hashing.
/// </summary>
public static class CanonicalJson
{
    private static readonly Regex JsonNumberExpression = new(
        "^(?<sign>-)?(?<int>0|[1-9]\\d*)(?:\\.(?<frac>\\d+))?(?:[eE](?<exp>[+-]?\\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        var match = JsonNumberExpression.Match(raw);
        if (!match.Success)
        {
            throw new InvalidOperationException($"'{raw}' is not a valid JSON number.");
        }

        var sign = match.Groups["sign"].Value;
        var integerDigits = match.Groups["int"].Value;
        var fractionalDigits = match.Groups["frac"].Success ? match.Groups["frac"].Value : string.Empty;
        var exponent = match.Groups["exp"].Success
            ? int.Parse(match.Groups["exp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : 0;

        var digits = integerDigits + fractionalDigits;
        var exponentBase10 = exponent - fractionalDigits.Length;

        var firstNonZero = digits.AsSpan().IndexOfAnyExcept('0');
        if (firstNonZero < 0)
        {
            return "0";
        }

        digits = digits[firstNonZero..];
        var trailingZeroCount = CountTrailingZeros(digits);
        if (trailingZeroCount > 0)
        {
            digits = digits[..^trailingZeroCount];
            exponentBase10 += trailingZeroCount;
        }

        var scientificExponent = exponentBase10 + digits.Length - 1;
        var normalized = scientificExponent is >= -6 and < 21
            ? ExpandToDecimal(digits, exponentBase10)
            : $"{digits[0]}{(digits.Length > 1 ? "." + digits[1..] : string.Empty)}e{scientificExponent.ToString(CultureInfo.InvariantCulture)}";

        return sign.Length == 0 ? normalized : "-" + normalized;
    }

    private static int CountTrailingZeros(string digits)
    {
        var count = 0;
        for (var index = digits.Length - 1; index >= 0 && digits[index] == '0'; index--)
        {
            count++;
        }

        return count;
    }

    private static string ExpandToDecimal(string digits, int exponentBase10)
    {
        if (exponentBase10 >= 0)
        {
            return digits + new string('0', exponentBase10);
        }

        var decimalIndex = digits.Length + exponentBase10;
        if (decimalIndex > 0)
        {
            return $"{digits[..decimalIndex]}.{digits[decimalIndex..]}";
        }

        return $"0.{new string('0', -decimalIndex)}{digits}";
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
