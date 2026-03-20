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
        "return",
        "client"
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
    private static readonly Regex SamplingToolNameExpression = new("^[A-Za-z0-9_.-]{1,128}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void ValidateCatalogPaths(IReadOnlyList<string> apiPaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var namespacePrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var apiPath in apiPaths)
        {
            if (!seen.Add(apiPath))
            {
                throw new InvalidOperationException($"API path '{apiPath}' is registered more than once.");
            }

            if (namespacePrefixes.Contains(apiPath))
            {
                throw new InvalidOperationException($"API path '{apiPath}' collides with an existing namespace path.");
            }

            var segments = apiPath.Split('.');
            for (var index = 1; index < segments.Length; index++)
            {
                var prefix = string.Join('.', segments.Take(index));
                if (seen.Contains(prefix))
                {
                    throw new InvalidOperationException($"API path '{apiPath}' collides with existing leaf path '{prefix}'.");
                }

                namespacePrefixes.Add(prefix);
            }
        }
    }

    public static void ValidateResourceUris(IReadOnlyList<string> uris)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in uris)
        {
            if (!seen.Add(uri))
            {
                throw new InvalidOperationException($"Resource URI '{uri}' is registered more than once.");
            }
        }
    }

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

    public static void ValidateResourceUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Resource URI must be an absolute URI.");
        }
    }

    public static void ValidateMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            throw new InvalidOperationException("mimeType must be provided.");
        }

        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException("mimeType must be text/* or application/json.");
    }

    public static void ValidateExactlyOneResourceContentSource(string? text, Delegate? reader)
    {
        var configuredCount = 0;
        if (text is not null)
        {
            configuredCount++;
        }

        if (reader is not null)
        {
            configuredCount++;
        }

        if (configuredCount != 1)
        {
            throw new InvalidOperationException("Exactly one resource content source must be configured.");
        }
    }

    public static void ValidateSamplingToolNames(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"Sampling tool '{name}' is registered more than once.");
            }
        }
    }

    public static void ValidateSamplingToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SamplingToolNameExpression.IsMatch(name))
        {
            throw new InvalidOperationException("Sampling tool name must match ^[A-Za-z0-9_.-]{1,128}$.");
        }
    }

    public static void ValidateSamplingRequest(ProgrammaticSamplingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null)
        {
            throw new ArgumentException("Messages must be provided.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.SystemPrompt) && request.Messages.Count == 0)
        {
            throw new ArgumentException("Messages may be empty only when systemPrompt is provided.", nameof(request));
        }

        if (request.SystemPrompt is not null && string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            throw new ArgumentException("systemPrompt must be non-whitespace when provided.", nameof(request));
        }

        foreach (var message in request.Messages)
        {
            if (message is null)
            {
                throw new ArgumentException("Messages must not contain null items.", nameof(request));
            }

            if (!string.Equals(message.Role, "user", StringComparison.Ordinal)
                && !string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            {
                throw new ArgumentException("Sampling message role must be 'user' or 'assistant'.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(message.Text))
            {
                throw new ArgumentException("Sampling message text must be non-whitespace.", nameof(request));
            }
        }

        if (request.MaxTokens is <= 0)
        {
            throw new ArgumentException("maxTokens must be positive when provided.", nameof(request));
        }

        if (!request.EnableTools)
        {
            if (request.AllowedToolNames is not null)
            {
                throw new ArgumentException("allowedToolNames must be null when enableTools is false.", nameof(request));
            }

            return;
        }

        if (request.AllowedToolNames is null)
        {
            return;
        }

        if (request.AllowedToolNames.Count == 0)
        {
            throw new ArgumentException("allowedToolNames must not be empty when provided.", nameof(request));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in request.AllowedToolNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("allowedToolNames must contain only non-whitespace names.", nameof(request));
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException($"Duplicate sampling tool name '{name}' is not allowed.", nameof(request));
            }
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
