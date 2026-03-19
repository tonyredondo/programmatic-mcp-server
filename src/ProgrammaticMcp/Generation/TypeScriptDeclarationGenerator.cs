using System.Text;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

public static class TypeScriptDeclarationGenerator
{
    public static string Generate(IReadOnlyList<CapabilityDefinition> capabilities)
    {
        var builder = new StringBuilder();
        var declaredNames = new Dictionary<string, int>(StringComparer.Ordinal);

        builder.AppendLine("declare namespace programmatic {");

        foreach (var capability in capabilities)
        {
            var identifierBase = ApiPathUtilities.ToPascalCaseIdentifier(capability.ApiPath);
            var inputTypeName = Reserve($"{identifierBase}Input", declaredNames);
            var resultTypeName = Reserve($"{identifierBase}Result", declaredNames);

            builder.Append("  type ").Append(inputTypeName).Append(" = ").Append(RenderTypeScript(capability.Input.Schema)).AppendLine(";");
            builder.Append("  type ").Append(resultTypeName).Append(" = ").Append(RenderTypeScript(capability.Result.Schema)).AppendLine(";");

            if (capability.IsMutation && capability.PreviewPayloadSchema is not null)
            {
                var previewPayloadName = Reserve($"{identifierBase}PreviewPayload", declaredNames);
                builder.Append("  type ").Append(previewPayloadName).Append(" = ").Append(RenderTypeScript(capability.PreviewPayloadSchema)).AppendLine(";");

                if (capability.ApplyResultSchema is not null)
                {
                    var applyResultName = Reserve($"{identifierBase}ApplyResult", declaredNames);
                    builder.Append("  type ").Append(applyResultName).Append(" = ").Append(RenderTypeScript(capability.ApplyResultSchema)).AppendLine(";");
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

            var baseName = ApiPathUtilities.ToPascalCaseIdentifier(capability.ApiPath);
            builder.Append("  namespace ").Append(string.Join('.', segments.Take(segments.Count - 1))).AppendLine(" {");
            builder.Append("    function ").Append(segments[^1]).Append("(input: ")
                .Append($"{baseName}Input")
                .Append("): Promise<")
                .Append($"{baseName}Result")
                .AppendLine(">;");
            builder.AppendLine("  }");
        }

        builder.AppendLine("}");
        return builder.ToString();
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

    private static string RenderTypeScript(JsonNode schema)
    {
        var schemaObject = schema.AsObject();
        if (schemaObject.TryGetPropertyValue("$ref", out var reference) && reference is JsonValue referenceValue)
        {
            return referenceValue.GetValue<string>().Split('/').Last();
        }

        if (schemaObject["enum"] is JsonArray enumArray)
        {
            return string.Join(" | ", enumArray.Select(static item => item!.ToJsonString()));
        }

        if (schemaObject["type"] is JsonArray unionArray)
        {
            return string.Join(" | ", unionArray.Select(typeNode => RenderScalarType(typeNode!.GetValue<string>(), schemaObject)));
        }

        if (schemaObject["type"] is JsonValue typeValue)
        {
            return RenderScalarType(typeValue.GetValue<string>(), schemaObject);
        }

        return "unknown";
    }

    private static string RenderScalarType(string type, JsonObject schema)
    {
        return type switch
        {
            "object" => RenderObject(schema),
            "array" => $"{RenderTypeScript(schema["items"]!)}[]",
            "string" => "string",
            "integer" => "number",
            "number" => "number",
            "boolean" => "boolean",
            "null" => "null",
            _ => "unknown"
        };
    }

    private static string RenderObject(JsonObject schema)
    {
        var properties = schema["properties"]?.AsObject();
        if (properties is null)
        {
            return "Record<string, unknown>";
        }

        var required = schema["required"]?.AsArray().Select(static node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var members = properties
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}{(required.Contains(pair.Key) ? string.Empty : "?")}: {RenderTypeScript(pair.Value!)}");
        return "{ " + string.Join("; ", members) + " }";
    }
}
