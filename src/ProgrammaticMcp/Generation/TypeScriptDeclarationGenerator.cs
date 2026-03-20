using System.Text;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

/// <summary>
/// Generates TypeScript declarations from the registered capability catalog.
/// </summary>
public static class TypeScriptDeclarationGenerator
{
    /// <summary>
    /// Generates the declaration payload for the supplied capabilities.
    /// </summary>
    public static string Generate(IReadOnlyList<CapabilityDefinition> capabilities)
    {
        return Generate(capabilities, TypeScriptNamingPlan.Create(capabilities));
    }

    /// <summary>
    /// Generates the declaration payload for the supplied capabilities with a shared naming plan.
    /// </summary>
    internal static string Generate(IReadOnlyList<CapabilityDefinition> capabilities, TypeScriptNamingPlan namingPlan)
    {
        var builder = new StringBuilder();

        builder.AppendLine("declare namespace programmatic {");
        builder.AppendLine("  type ProgrammaticSamplingMessage = { role: \"assistant\" | \"user\"; text: string };");
        builder.AppendLine("  type ProgrammaticSamplingRequest = { messages: ProgrammaticSamplingMessage[]; systemPrompt?: string | null; enableTools?: boolean; allowedToolNames?: string[] | null; maxTokens?: number | null };");

        foreach (var capability in capabilities)
        {
            var names = namingPlan.Get(capability.ApiPath);
            EmitAlias(builder, names.InputTypeName, capability.Input.Schema);
            EmitAlias(builder, names.ResultTypeName, capability.Result.Schema);

            if (capability.IsMutation && capability.PreviewPayloadSchema is not null)
            {
                EmitAlias(builder, names.PreviewPayloadTypeName!, capability.PreviewPayloadSchema);

                if (capability.ApplyResultSchema is not null)
                {
                    EmitAlias(builder, names.ApplyResultTypeName!, capability.ApplyResultSchema);
                }
            }
        }

        foreach (var capability in capabilities)
        {
            var segments = ApiPathUtilities.SplitAndValidate(capability.ApiPath);
            for (var depth = 0; depth < segments.Count - 1; depth++)
            {
                builder.Append("  namespace ").Append(string.Join('.', segments.Take(depth + 1))).AppendLine(" { }");
            }

            var names = namingPlan.Get(capability.ApiPath);
            if (segments.Count == 1)
            {
                builder.Append("  function ").Append(segments[0]).Append("(input: ")
                    .Append(names.InputTypeName)
                    .Append("): Promise<")
                    .Append(names.ResultTypeName)
                    .AppendLine(">;");
                continue;
            }

            builder.Append("  namespace ").Append(string.Join('.', segments.Take(segments.Count - 1))).AppendLine(" {");
            builder.Append("    function ").Append(segments[^1]).Append("(input: ")
                .Append(names.InputTypeName)
                .Append("): Promise<")
                .Append(names.ResultTypeName)
                .AppendLine(">;");
            builder.AppendLine("  }");
        }

        builder.AppendLine("  namespace client {");
        builder.AppendLine("    function sample(request: ProgrammaticSamplingRequest): Promise<string>;");
        builder.AppendLine("  }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void EmitAlias(StringBuilder builder, string rootAliasName, JsonNode schema)
    {
        var schemaObject = schema.AsObject();
        var definitionAliases = BuildDefinitionAliasMap(rootAliasName, schemaObject);

        foreach (var definition in definitionAliases.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var definitionSchema = schemaObject["$defs"]![definition.Key]!;
            builder.Append("  type ").Append(definition.Value).Append(" = ")
                .Append(RenderTypeScript(definitionSchema, definitionAliases))
                .AppendLine(";");
        }

        builder.Append("  type ").Append(rootAliasName).Append(" = ")
            .Append(RenderTypeScript(schemaObject, definitionAliases))
            .AppendLine(";");
    }

    private static Dictionary<string, string> BuildDefinitionAliasMap(string rootAliasName, JsonObject schema)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (schema["$defs"] is not JsonObject definitions)
        {
            return aliases;
        }

        foreach (var definition in definitions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            aliases[definition.Key] = $"{rootAliasName}{definition.Key}";
        }

        return aliases;
    }

    private static string RenderTypeScript(JsonNode schema, IReadOnlyDictionary<string, string> definitionAliases)
    {
        var schemaObject = schema.AsObject();
        if (schemaObject["anyOf"] is JsonArray anyOfArray)
        {
            return string.Join(
                " | ",
                anyOfArray.Select(item => RenderTypeScript(item!, definitionAliases)));
        }

        if (schemaObject.TryGetPropertyValue("$ref", out var reference) && reference is JsonValue referenceValue)
        {
            var definitionName = referenceValue.GetValue<string>().Split('/').Last();
            return definitionAliases.TryGetValue(definitionName, out var alias) ? alias : definitionName;
        }

        if (schemaObject["enum"] is JsonArray enumArray)
        {
            return string.Join(" | ", enumArray.Select(static item => item!.ToJsonString()));
        }

        if (schemaObject["type"] is JsonArray unionArray)
        {
            return string.Join(" | ", unionArray.Select(typeNode => RenderScalarType(typeNode!.GetValue<string>(), schemaObject, definitionAliases)));
        }

        if (schemaObject["type"] is JsonValue typeValue)
        {
            return RenderScalarType(typeValue.GetValue<string>(), schemaObject, definitionAliases);
        }

        return "unknown";
    }

    private static string RenderScalarType(string type, JsonObject schema, IReadOnlyDictionary<string, string> definitionAliases)
    {
        return type switch
        {
            "object" => RenderObject(schema, definitionAliases),
            "array" => RenderArrayType(schema["items"]!, definitionAliases),
            "string" => "string",
            "integer" => "number",
            "number" => "number",
            "boolean" => "boolean",
            "null" => "null",
            _ => "unknown"
        };
    }

    private static string RenderArrayType(JsonNode itemsSchema, IReadOnlyDictionary<string, string> definitionAliases)
    {
        var itemType = RenderTypeScript(itemsSchema, definitionAliases);
        return itemType.Contains('|', StringComparison.Ordinal) ? $"({itemType})[]" : $"{itemType}[]";
    }

    private static string RenderObject(JsonObject schema, IReadOnlyDictionary<string, string> definitionAliases)
    {
        var properties = schema["properties"]?.AsObject();
        if (properties is null || properties.Count == 0)
        {
            if (schema["additionalProperties"] is JsonObject additionalProperties)
            {
                return $"Record<string, {RenderTypeScript(additionalProperties, definitionAliases)}>";
            }

            return "Record<string, unknown>";
        }

        var required = schema["required"]?.AsArray().Select(static node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var members = properties
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{RenderPropertyName(pair.Key)}{(required.Contains(pair.Key) ? string.Empty : "?")}: {RenderTypeScript(pair.Value!, definitionAliases)}");
        return "{ " + string.Join("; ", members) + " }";
    }

    private static string RenderPropertyName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return JsonValue.Create(propertyName)!.ToJsonString();
        }

        if ((char.IsLetter(propertyName[0]) || propertyName[0] is '_' or '$')
            && propertyName.Skip(1).All(static character => char.IsLetterOrDigit(character) || character is '_' or '$'))
        {
            return propertyName;
        }

        return JsonValue.Create(propertyName)!.ToJsonString();
    }
}

/// <summary>
/// Shared naming plan for generated TypeScript declarations and catalog signatures.
/// </summary>
internal sealed class TypeScriptNamingPlan
{
    private readonly Dictionary<string, CapabilityTypeNames> _capabilitiesByApiPath;

    private TypeScriptNamingPlan(Dictionary<string, CapabilityTypeNames> capabilitiesByApiPath)
    {
        _capabilitiesByApiPath = capabilitiesByApiPath;
    }

    public static TypeScriptNamingPlan Create(IReadOnlyList<CapabilityDefinition> capabilities)
    {
        var declaredNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new Dictionary<string, CapabilityTypeNames>(StringComparer.Ordinal);

        foreach (var capability in capabilities)
        {
            var identifierBase = ApiPathUtilities.ToPascalCaseIdentifier(capability.ApiPath);
            var inputTypeName = Reserve($"{identifierBase}Input", declaredNames);
            var resultTypeName = Reserve($"{identifierBase}Result", declaredNames);
            var previewPayloadTypeName = capability.IsMutation && capability.PreviewPayloadSchema is not null
                ? Reserve($"{identifierBase}PreviewPayload", declaredNames)
                : null;
            var applyResultTypeName = capability.IsMutation && capability.ApplyResultSchema is not null
                ? Reserve($"{identifierBase}ApplyResult", declaredNames)
                : null;

            items[capability.ApiPath] = new CapabilityTypeNames(
                inputTypeName,
                resultTypeName,
                previewPayloadTypeName,
                applyResultTypeName);
        }

        return new TypeScriptNamingPlan(items);
    }

    public CapabilityTypeNames Get(string apiPath)
    {
        return _capabilitiesByApiPath[apiPath];
    }

    private static string Reserve(string baseName, Dictionary<string, int> counts)
    {
        if (!counts.TryGetValue(baseName, out var count))
        {
            counts[baseName] = 1;
            return baseName;
        }

        count++;
        counts[baseName] = count;
        return $"{baseName}{count}";
    }
}

/// <summary>
/// Resolved TypeScript alias names for a single capability.
/// </summary>
internal sealed record CapabilityTypeNames(
    string InputTypeName,
    string ResultTypeName,
    string? PreviewPayloadTypeName,
    string? ApplyResultTypeName);
