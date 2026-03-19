using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProgrammaticMcp;

/// <summary>
/// Validates conversation identifiers used by the public contract.
/// </summary>
public static class ConversationIdValidator
{
    private static readonly Regex ConversationIdExpression = new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Determines whether the supplied conversation identifier matches the allowed pattern.
    /// </summary>
    public static bool IsValid(string conversationId) => ConversationIdExpression.IsMatch(conversationId);

    /// <summary>
    /// Throws when the supplied conversation identifier does not match the allowed pattern.
    /// </summary>
    public static void EnsureValid(string conversationId)
    {
        if (!IsValid(conversationId))
        {
            throw new ArgumentException("ConversationId is invalid.", nameof(conversationId));
        }
    }
}

/// <summary>
/// Validates public schema version numbers.
/// </summary>
public static class SchemaVersionValidator
{
    /// <summary>
    /// Determines whether the supplied schema version is supported.
    /// </summary>
    public static bool IsSupported(int schemaVersion) => schemaVersion == ProgrammaticContractConstants.SchemaVersion;

    /// <summary>
    /// Throws when the supplied schema version is not supported.
    /// </summary>
    public static void EnsureSupported(int schemaVersion)
    {
        if (!IsSupported(schemaVersion))
        {
            throw new InvalidOperationException(
                $"Schema version '{schemaVersion}' is not supported. Expected '{ProgrammaticContractConstants.SchemaVersion}'.");
        }
    }
}

/// <summary>
/// Utility helpers for API path validation and naming.
/// </summary>
public static class ApiPathUtilities
{
    private static readonly HashSet<string> ReservedSegments = new(StringComparer.Ordinal)
    {
        "__meta",
        "__internal",
        "constructor",
        "prototype",
        "__proto__",
        "class",
        "function",
        "default",
        "new",
        "return"
    };

    private static readonly Regex SegmentExpression = new("^[a-z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits and validates an API path.
    /// </summary>
    public static IReadOnlyList<string> SplitAndValidate(string apiPath)
    {
        BuilderValidation.ValidateApiPath(apiPath);
        return apiPath.Split('.');
    }

    internal static bool IsReserved(string segment) => ReservedSegments.Contains(segment);

    internal static bool IsValidSegment(string segment) => SegmentExpression.IsMatch(segment);

    /// <summary>
    /// Converts an API path to a PascalCase identifier.
    /// </summary>
    public static string ToPascalCaseIdentifier(string apiPath)
    {
        var segments = SplitAndValidate(apiPath);
        return string.Concat(segments.Select(static segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
    }
}

internal static class BuilderValidation
{
    public static void ValidateApiPath(string apiPath)
    {
        if (string.IsNullOrWhiteSpace(apiPath) || apiPath.Length > 80)
        {
            throw new InvalidOperationException("API path must be present and at most 80 characters long.");
        }

        var segments = apiPath.Split('.');
        if (segments.Length is 0 or > 4)
        {
            throw new InvalidOperationException("API path must contain between 1 and 4 segments.");
        }

        foreach (var segment in segments)
        {
            if (!ApiPathUtilities.IsValidSegment(segment) || ApiPathUtilities.IsReserved(segment))
            {
                throw new InvalidOperationException($"API path segment '{segment}' is invalid or reserved.");
            }
        }
    }

    public static void ValidateRootObjectSchema(JsonNode schema, string label)
    {
        var schemaObject = schema.AsObject();
        var typeNode = schemaObject["type"];
        if (typeNode is JsonValue value && value.TryGetValue<string>(out var type) && type == "object")
        {
            return;
        }

        throw new InvalidOperationException($"{label} must have a root schema of type 'object'.");
    }

    public static void ValidateRequiredText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be provided.");
        }
    }

    public static void ValidateRequiredItems(IReadOnlyCollection<string> values, string name)
    {
        if (values.Count == 0 || values.All(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{name} must contain at least one non-empty value.");
        }
    }
}

/// <summary>
/// Exception thrown when automatic schema generation cannot represent a CLR type.
/// </summary>
public sealed class UnsupportedSchemaTypeException : Exception
{
    /// <summary>
    /// Creates a new unsupported schema type exception.
    /// </summary>
    public UnsupportedSchemaTypeException(Type type, string reason)
        : base($"Type '{type}' is not supported for automatic schema generation: {reason}")
    {
        Type = type;
    }

    /// <summary>
    /// Gets the unsupported type.
    /// </summary>
    public Type Type { get; }
}
