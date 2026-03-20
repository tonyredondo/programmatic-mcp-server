using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProgrammaticMcp;

/// <summary>
/// Generates JSON Schema documents from CLR types.
/// </summary>
public sealed class BuiltInSchemaGenerator
{
    private readonly NullabilityInfoContext _nullabilityInfoContext = new();

    /// <summary>
    /// Generates a JSON Schema document for the supplied type.
    /// </summary>
    public JsonNode Generate(Type type)
    {
        var objectCounts = CountObjectReferences(type, new Dictionary<Type, int>());
        var generated = GenerateNode(
            type,
            isRoot: true,
            objectCounts,
            new Dictionary<Type, string>(),
            new Dictionary<string, Type>(StringComparer.Ordinal),
            new SortedDictionary<string, JsonNode>(StringComparer.Ordinal));
        generated["$schema"] = ProgrammaticContractConstants.JsonSchemaDialect;
        return generated;
    }

    private Dictionary<Type, int> CountObjectReferences(Type type, Dictionary<Type, int> counts)
    {
        VisitForCounts(type, counts, new HashSet<Type>());
        return counts;
    }

    private void VisitForCounts(Type type, Dictionary<Type, int> counts, HashSet<Type> visiting)
    {
        type = UnwrapNullable(type);

        if (IsUnsupported(type))
        {
            throw new UnsupportedSchemaTypeException(type, "explicit schema override is required");
        }

        if (TryGetCollectionElementType(type, out var elementType))
        {
            VisitForCounts(elementType!, counts, visiting);
            return;
        }

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            VisitForCounts(valueType!, counts, visiting);
            return;
        }

        if (!IsObjectShape(type))
        {
            return;
        }

        counts[type] = counts.TryGetValue(type, out var count) ? count + 1 : 1;
        if (!visiting.Add(type))
        {
            return;
        }

        foreach (var property in GetSerializableProperties(type))
        {
            VisitForCounts(property.PropertyType, counts, visiting);
        }

        visiting.Remove(type);
    }

    private JsonObject GenerateNode(
        Type type,
        bool isRoot,
        IReadOnlyDictionary<Type, int> objectCounts,
        Dictionary<Type, string> assignedDefinitionNames,
        Dictionary<string, Type> assignedDefinitionTypes,
        SortedDictionary<string, JsonNode> definitions)
    {
        var node = GenerateSchema(type, isRoot, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions);
        if (isRoot && definitions.Count > 0)
        {
            var definitionsObject = new JsonObject();
            foreach (var definition in definitions)
            {
                definitionsObject[definition.Key] = definition.Value.DeepClone();
            }

            node["$defs"] = definitionsObject;
        }

        return node;
    }

    private JsonObject GenerateSchema(
        Type type,
        bool isRoot,
        IReadOnlyDictionary<Type, int> objectCounts,
        Dictionary<Type, string> assignedDefinitionNames,
        Dictionary<string, Type> assignedDefinitionTypes,
        SortedDictionary<string, JsonNode> definitions)
    {
        var effectiveType = UnwrapNullable(type);
        if (TryGeneratePrimitiveSchema(type, out var primitive))
        {
            return primitive;
        }

        if (TryGetCollectionElementType(effectiveType, out var elementType))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = GenerateSchema(elementType!, false, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions)
            };
        }

        if (TryGetDictionaryValueType(effectiveType, out var valueType))
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = GenerateSchema(valueType!, false, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions)
            };
        }

        if (!IsObjectShape(effectiveType))
        {
            throw new UnsupportedSchemaTypeException(effectiveType, "explicit schema override is required");
        }

        if (!isRoot && objectCounts.TryGetValue(effectiveType, out var count) && count > 1)
        {
            if (!assignedDefinitionNames.TryGetValue(effectiveType, out var definitionName))
            {
                definitionName = SchemaNaming.ResolveDefinitionName(effectiveType, assignedDefinitionTypes);
                assignedDefinitionNames[effectiveType] = definitionName;
                definitions[definitionName] = GenerateObjectSchema(effectiveType, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions);
            }

            return new JsonObject { ["$ref"] = $"#/$defs/{definitionName}" };
        }

        return GenerateObjectSchema(effectiveType, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions);
    }

    private JsonObject GenerateObjectSchema(
        Type type,
        IReadOnlyDictionary<Type, int> objectCounts,
        Dictionary<Type, string> assignedDefinitionNames,
        Dictionary<string, Type> assignedDefinitionTypes,
        SortedDictionary<string, JsonNode> definitions)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in GetSerializableProperties(type))
        {
            var propertySchema = GenerateSchema(property.PropertyType, false, objectCounts, assignedDefinitionNames, assignedDefinitionTypes, definitions);
            properties[property.Name] = propertySchema;

            if (property.Required)
            {
                required.Add(property.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private bool TryGeneratePrimitiveSchema(Type type, out JsonObject schema)
    {
        var effectiveType = UnwrapNullable(type);
        var includesNull = IsNullable(type);

        JsonObject? primitive = effectiveType switch
        {
            var t when t == typeof(string) => new JsonObject { ["type"] = "string" },
            var t when t == typeof(Guid) => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            var t when t == typeof(Uri) => new JsonObject { ["type"] = "string", ["format"] = "uri" },
            var t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            var t when t == typeof(DateOnly) => new JsonObject { ["type"] = "string", ["format"] = "date" },
            var t when t == typeof(TimeOnly) => new JsonObject { ["type"] = "string", ["format"] = "time" },
            var t when t == typeof(TimeSpan) => new JsonObject { ["type"] = "string", ["x-dotnet-format"] = "c" },
            var t when t == typeof(bool) => new JsonObject { ["type"] = "boolean" },
            var t when IsInteger(t) => new JsonObject { ["type"] = "integer" },
            var t when IsNumber(t) => new JsonObject { ["type"] = "number" },
            var t when t.IsEnum => GenerateEnumSchema(t),
            _ => null
        };

        if (primitive is null)
        {
            schema = null!;
            return false;
        }

        if (includesNull)
        {
            primitive["type"] = new JsonArray(primitive["type"]!.GetValue<string>(), "null");
        }

        schema = primitive;
        return true;
    }

    private JsonObject GenerateEnumSchema(Type type)
    {
        var names = Enum.GetNames(type)
            .Select(JsonNamingPolicy.CamelCase.ConvertName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(names.Select(static name => (JsonNode?)name).ToArray())
        };
    }

    private static bool IsUnsupported(Type type)
    {
        type = UnwrapNullable(type);

        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonDocument))
        {
            return true;
        }

        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
        {
            return true;
        }

        if (type.IsInterface && !TryGetCollectionElementType(type, out _) && !TryGetDictionaryValueType(type, out _))
        {
            return true;
        }

        return false;
    }

    private IEnumerable<GeneratedProperty> GetSerializableProperties(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var nullability = _nullabilityInfoContext.Create(property);
            var isNullableReference = property.PropertyType.IsClass && nullability.ReadState != NullabilityState.NotNull;
            var required = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() is not null
                || (!isNullableReference && !IsNullable(property.PropertyType));

            yield return new GeneratedProperty(jsonName, property.PropertyType, required);
        }
    }

    private static bool TryGetCollectionElementType(Type type, out Type? elementType)
    {
        type = UnwrapNullable(type);
        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }

        var enumerable = type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = null;
        return false;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type? valueType)
    {
        type = UnwrapNullable(type);
        var dictionary = type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(
                i => i.IsGenericType
                    && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                    && i.GetGenericArguments()[0] == typeof(string));
        if (dictionary is not null)
        {
            valueType = dictionary.GetGenericArguments()[1];
            return true;
        }

        valueType = null;
        return false;
    }

    private static bool IsObjectShape(Type type)
    {
        type = UnwrapNullable(type);
        return type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum);
    }

    private static bool IsNullable(Type type) => Nullable.GetUnderlyingType(type) is not null;

    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsInteger(Type type)
    {
        type = UnwrapNullable(type);
        return type == typeof(byte)
               || type == typeof(sbyte)
               || type == typeof(short)
               || type == typeof(ushort)
               || type == typeof(int)
               || type == typeof(uint)
               || type == typeof(long)
               || type == typeof(ulong);
    }

    private static bool IsNumber(Type type)
    {
        type = UnwrapNullable(type);
        return type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    private readonly record struct GeneratedProperty(string Name, Type PropertyType, bool Required);
}

internal static class SchemaNaming
{
    public static string ResolveDefinitionName(Type type, IDictionary<string, Type> assignedDefinitionTypes)
    {
        var baseName = ToDefinitionName(type);
        if (!assignedDefinitionTypes.TryGetValue(baseName, out var existingType))
        {
            assignedDefinitionTypes[baseName] = type;
            return baseName;
        }

        if (existingType == type)
        {
            return baseName;
        }

        var suffix = 2;
        while (true)
        {
            var candidate = $"{baseName}{suffix}";
            if (!assignedDefinitionTypes.TryGetValue(candidate, out existingType))
            {
                assignedDefinitionTypes[candidate] = type;
                return candidate;
            }

            if (existingType == type)
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static string ToDefinitionName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`')];
        var arguments = string.Concat(type.GetGenericArguments().Select(ToDefinitionName));
        return $"{name}{arguments}";
    }
}
