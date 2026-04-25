using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

/// <summary>
/// Exception thrown when a JSON value does not satisfy a schema.
/// </summary>
public sealed class JsonSchemaValidationException : Exception
{
    /// <summary>
    /// Creates a validation exception with the supplied message.
    /// </summary>
    public JsonSchemaValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Validates JSON payloads against the subset of JSON Schema used by the library.
/// </summary>
public static class JsonSchemaValidator
{
    /// <summary>
    /// Validates a JSON value against a JSON schema document.
    /// </summary>
    public static void Validate(JsonNode? value, JsonNode schema)
    {
        ValidateNode(value, schema, schema, "$");
    }

    private static void ValidateNode(JsonNode? value, JsonNode schema, JsonNode rootSchema, string path)
    {
        var schemaObject = schema.AsObject();

        if (schemaObject.TryGetPropertyValue("const", out var constNode))
        {
            EnsureConst(value, constNode, path);
        }

        if (schemaObject["anyOf"] is JsonArray anyOfArray)
        {
            foreach (var candidate in anyOfArray)
            {
                try
                {
                    ValidateNode(value, candidate!, rootSchema, path);
                    return;
                }
                catch (JsonSchemaValidationException)
                {
                }
            }

            throw new JsonSchemaValidationException($"Value at {path} does not match any allowed schema.");
        }

        if (schemaObject.TryGetPropertyValue("$ref", out var referenceNode) && referenceNode is JsonValue referenceValue)
        {
            ValidateNode(value, ResolveReference(referenceValue.GetValue<string>(), rootSchema), rootSchema, path);
            return;
        }

        var declaredTypes = ReadTypes(schemaObject);
        if (value is null)
        {
            if (!declaredTypes.Contains("null"))
            {
                throw new JsonSchemaValidationException($"Value at {path} must not be null.");
            }

            return;
        }

        if (value is JsonObject jsonObject)
        {
            EnsureType(declaredTypes, "object", path);

            var properties = schemaObject["properties"]?.AsObject() ?? new JsonObject();
            var required = schemaObject["required"]?.AsArray().Select(static item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var propertyName in required)
            {
                if (!jsonObject.ContainsKey(propertyName))
                {
                    throw new JsonSchemaValidationException($"Required property '{propertyName}' is missing at {path}.");
                }
            }

            foreach (var property in jsonObject)
            {
                if (properties[property.Key] is JsonNode propertySchema)
                {
                    ValidateNode(property.Value, propertySchema, rootSchema, $"{path}.{property.Key}");
                    continue;
                }

                if (schemaObject.TryGetPropertyValue("additionalProperties", out var additionalProperties) && additionalProperties is JsonNode additionalSchema)
                {
                    if (additionalSchema is JsonValue booleanValue && booleanValue.TryGetValue<bool>(out var allowed) && !allowed)
                    {
                        throw new JsonSchemaValidationException($"Unexpected property '{property.Key}' at {path}.");
                    }

                    if (additionalSchema is not JsonValue)
                    {
                        ValidateNode(property.Value, additionalSchema, rootSchema, $"{path}.{property.Key}");
                    }
                }
                else
                {
                    throw new JsonSchemaValidationException($"Unexpected property '{property.Key}' at {path}.");
                }
            }

            return;
        }

        if (value is JsonArray jsonArray)
        {
            EnsureType(declaredTypes, "array", path);
            var itemsSchema = schemaObject["items"] ?? throw new JsonSchemaValidationException($"Missing array items schema at {path}.");
            for (var index = 0; index < jsonArray.Count; index++)
            {
                ValidateNode(jsonArray[index], itemsSchema, rootSchema, $"{path}[{index}]");
            }

            return;
        }

        if (TryGetScalarKind(value, out var kind, out var scalar))
        {
            EnsureType(declaredTypes, kind, path);

            if (schemaObject.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumArray)
            {
                var jsonScalar = JsonSerializerContract.SerializeToNode(scalar)?.ToJsonString();
                if (!enumArray.Any(item => item?.ToJsonString() == jsonScalar))
                {
                    throw new JsonSchemaValidationException($"Value at {path} is not part of the enum set.");
                }
            }

            return;
        }

        throw new JsonSchemaValidationException($"Unsupported JSON shape at {path}.");
    }

    private static void EnsureConst(JsonNode? value, JsonNode? constNode, string path)
    {
        if (!string.Equals(CanonicalJson.Serialize(value), CanonicalJson.Serialize(constNode), StringComparison.Ordinal))
        {
            throw new JsonSchemaValidationException($"Value at {path} does not match the required constant.");
        }
    }

    private static JsonNode ResolveReference(string reference, JsonNode rootSchema)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new JsonSchemaValidationException($"Unsupported schema reference '{reference}'.");
        }

        JsonNode? current = rootSchema;
        foreach (var segment in reference[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decodedSegment = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current?[decodedSegment];
            if (current is null)
            {
                throw new JsonSchemaValidationException($"Schema reference '{reference}' could not be resolved.");
            }
        }

        return current;
    }

    private static HashSet<string> ReadTypes(JsonObject schema)
    {
        return schema["type"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var typeName) => new HashSet<string>(StringComparer.Ordinal) { typeName },
            JsonArray array => array.Select(static item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
    }

    private static void EnsureType(HashSet<string> declaredTypes, string actualType, string path)
    {
        if (declaredTypes.Count == 0
            || declaredTypes.Contains(actualType)
            || (actualType == "integer" && declaredTypes.Contains("number")))
        {
            return;
        }

        var expectedTypes = string.Join(" or ", declaredTypes.OrderBy(static type => type, StringComparer.Ordinal));
        throw new JsonSchemaValidationException($"Value at {path} must be of type {expectedTypes}; actual type was {actualType}.");
    }

    private static bool TryGetScalarKind(JsonNode value, out string kind, out object? scalar)
    {
        if (value is not JsonValue jsonValue)
        {
            kind = string.Empty;
            scalar = null;
            return false;
        }

        if (jsonValue.TryGetValue<string>(out var stringValue))
        {
            kind = "string";
            scalar = stringValue;
            return true;
        }

        if (jsonValue.TryGetValue<bool>(out var boolValue))
        {
            kind = "boolean";
            scalar = boolValue;
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            kind = "integer";
            scalar = intValue;
            return true;
        }

        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            kind = "integer";
            scalar = longValue;
            return true;
        }

        if (jsonValue.TryGetValue<double>(out var doubleValue))
        {
            kind = Math.Abs(doubleValue % 1) < double.Epsilon ? "integer" : "number";
            scalar = doubleValue;
            return true;
        }

        if (jsonValue.TryGetValue<decimal>(out var decimalValue))
        {
            kind = decimal.Truncate(decimalValue) == decimalValue ? "integer" : "number";
            scalar = decimalValue;
            return true;
        }

        kind = string.Empty;
        scalar = null;
        return false;
    }
}
