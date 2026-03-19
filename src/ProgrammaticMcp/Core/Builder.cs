using System.Reflection;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

public sealed class ProgrammaticMcpBuilder
{
    private readonly List<ICapabilityRegistration> _registrations = new();
    private IProgrammaticAuthorizationPolicy? _authorizationPolicy;
    private bool _allowAllBoundCallers;

    public ProgrammaticMcpBuilder AddCapability<TInput, TResult>(string apiPath, Action<CapabilityBuilder<TInput, TResult>> configure)
    {
        var builder = new CapabilityBuilder<TInput, TResult>(apiPath);
        configure(builder);
        _registrations.Add(builder);
        return this;
    }

    public ProgrammaticMcpBuilder AddMutation<TArgs, TPreview, TApplyResult>(string apiPath, Action<MutationBuilder<TArgs, TPreview, TApplyResult>> configure)
    {
        var builder = new MutationBuilder<TArgs, TPreview, TApplyResult>(apiPath);
        configure(builder);
        _registrations.Add(builder);
        return this;
    }

    public ProgrammaticMcpBuilder UseAuthorizationPolicy(IProgrammaticAuthorizationPolicy policy)
    {
        _authorizationPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public ProgrammaticMcpBuilder AllowAllBoundCallers()
    {
        _allowAllBoundCallers = true;
        return this;
    }

    public ProgrammaticCatalogSnapshot BuildCatalog()
    {
        if (_registrations.OfType<IMutationRegistration>().Any() && _authorizationPolicy is null && !_allowAllBoundCallers)
        {
            throw new InvalidOperationException(
                "Mutations require an explicit authorization policy or an explicit AllowAllBoundCallers opt-in.");
        }

        var schemaGenerator = new BuiltInSchemaGenerator();
        var capabilities = _registrations
            .Select(registration => registration.BuildDefinition(schemaGenerator))
            .OrderBy(static capability => capability.ApiPath, StringComparer.Ordinal)
            .ToArray();

        var generatedTypeScript = TypeScriptDeclarationGenerator.Generate(capabilities);
        var capabilityVersion = CapabilityVersionCalculator.Calculate(capabilities, generatedTypeScript);
        var authorizationPolicy = _authorizationPolicy ?? new AllowAllBoundCallersAuthorizationPolicy();
        return new ProgrammaticCatalogSnapshot(capabilities, capabilityVersion, generatedTypeScript, authorizationPolicy);
    }
}

public sealed class CapabilityBuilder<TInput, TResult> : ICapabilityRegistration
{
    private readonly string _apiPath;
    private string? _description;
    private readonly List<string> _useWhen = new();
    private readonly List<string> _doNotUseWhen = new();
    private readonly List<string> _notes = new();
    private Func<TInput, ProgrammaticCapabilityContext, ValueTask<TResult>>? _handler;
    private readonly List<CapabilityExample> _examples = new();
    private JsonNode? _inputSchemaOverride;
    private JsonNode? _resultSchemaOverride;

    internal CapabilityBuilder(string apiPath)
    {
        _apiPath = apiPath;
    }

    public CapabilityBuilder<TInput, TResult> WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public CapabilityBuilder<TInput, TResult> UseWhen(string useWhen)
    {
        _useWhen.Add(useWhen);
        return this;
    }

    public CapabilityBuilder<TInput, TResult> DoNotUseWhen(string doNotUseWhen)
    {
        _doNotUseWhen.Add(doNotUseWhen);
        return this;
    }

    public CapabilityBuilder<TInput, TResult> AddGuidanceNote(string note)
    {
        _notes.Add(note);
        return this;
    }

    public CapabilityBuilder<TInput, TResult> WithHandler(Func<TInput, ProgrammaticCapabilityContext, ValueTask<TResult>> handler)
    {
        _handler = handler;
        return this;
    }

    public CapabilityBuilder<TInput, TResult> AddExample(string description, TInput input, TResult result)
    {
        _examples.Add(
            new CapabilityExample(
                description,
                JsonSerializerContract.SerializeToNode(input),
                JsonSerializerContract.SerializeToNode(result)));
        return this;
    }

    public CapabilityBuilder<TInput, TResult> WithInputSchemaOverride(JsonNode schema)
    {
        _inputSchemaOverride = schema.DeepClone();
        return this;
    }

    public CapabilityBuilder<TInput, TResult> WithResultSchemaOverride(JsonNode schema)
    {
        _resultSchemaOverride = schema.DeepClone();
        return this;
    }

    CapabilityDefinition ICapabilityRegistration.BuildDefinition(BuiltInSchemaGenerator schemaGenerator)
    {
        var inputSchema = _inputSchemaOverride ?? schemaGenerator.Generate(typeof(TInput));
        var resultSchema = _resultSchemaOverride ?? schemaGenerator.Generate(typeof(TResult));
        BuilderValidation.ValidateApiPath(_apiPath);
        BuilderValidation.ValidateRootObjectSchema(inputSchema, "Capability input");
        BuilderValidation.ValidateRequiredText(_description, nameof(_description));
        BuilderValidation.ValidateRequiredItems(_useWhen, nameof(_useWhen));
        BuilderValidation.ValidateRequiredItems(_doNotUseWhen, nameof(_doNotUseWhen));
        ArgumentNullException.ThrowIfNull(_handler);

        return new CapabilityDefinition
        {
            ApiPath = _apiPath,
            Description = _description!,
            Signature = SignatureFormatter.Format(_apiPath),
            UsageGuidance = new CapabilityUsageGuidance(_useWhen.ToArray(), _doNotUseWhen.ToArray(), _notes.ToArray()),
            Input = new CapabilityResult(typeof(TInput), inputSchema),
            Result = new CapabilityResult(typeof(TResult), resultSchema),
            Examples = _examples.ToArray(),
            IsMutation = false,
            InputType = typeof(TInput),
            ResultType = typeof(TResult),
            CapabilityHandler = async (input, context) =>
            {
                var typedInput = JsonSerializerContract.DeserializeFromNode<TInput>(input);
                var result = await _handler(typedInput, context);
                return JsonSerializerContract.SerializeToNode(result);
            }
        };
    }
}

public sealed class MutationBuilder<TArgs, TPreview, TApplyResult> : ICapabilityRegistration, IMutationRegistration
{
    private readonly string _apiPath;
    private string? _description;
    private readonly List<string> _useWhen = new();
    private readonly List<string> _doNotUseWhen = new();
    private readonly List<string> _notes = new();
    private Func<TArgs, ProgrammaticMutationContext, ValueTask<TPreview>>? _previewHandler;
    private Func<TArgs, TPreview, ProgrammaticMutationContext, ValueTask<string>>? _summaryFactory;
    private Func<TArgs, ProgrammaticMutationContext, ValueTask<MutationApplyResult<TApplyResult>>>? _applyHandler;
    private JsonNode? _argsSchemaOverride;
    private JsonNode? _previewSchemaOverride;
    private JsonNode? _applyResultSchemaOverride;

    internal MutationBuilder(string apiPath)
    {
        _apiPath = apiPath;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> UseWhen(string useWhen)
    {
        _useWhen.Add(useWhen);
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> DoNotUseWhen(string doNotUseWhen)
    {
        _doNotUseWhen.Add(doNotUseWhen);
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> AddGuidanceNote(string note)
    {
        _notes.Add(note);
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithPreviewHandler(Func<TArgs, ProgrammaticMutationContext, ValueTask<TPreview>> handler)
    {
        _previewHandler = handler;
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithSummaryFactory(Func<TArgs, TPreview, ProgrammaticMutationContext, ValueTask<string>> factory)
    {
        _summaryFactory = factory;
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithApplyHandler(
        Func<TArgs, ProgrammaticMutationContext, ValueTask<MutationApplyResult<TApplyResult>>> handler)
    {
        _applyHandler = handler;
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithArgsSchemaOverride(JsonNode schema)
    {
        _argsSchemaOverride = schema.DeepClone();
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithPreviewSchemaOverride(JsonNode schema)
    {
        _previewSchemaOverride = schema.DeepClone();
        return this;
    }

    public MutationBuilder<TArgs, TPreview, TApplyResult> WithApplyResultSchemaOverride(JsonNode schema)
    {
        _applyResultSchemaOverride = schema.DeepClone();
        return this;
    }

    CapabilityDefinition ICapabilityRegistration.BuildDefinition(BuiltInSchemaGenerator schemaGenerator)
    {
        var argsSchema = _argsSchemaOverride ?? schemaGenerator.Generate(typeof(TArgs));
        var previewSchema = _previewSchemaOverride ?? schemaGenerator.Generate(typeof(TPreview));
        var applyResultSchema = _applyResultSchemaOverride ?? schemaGenerator.Generate(typeof(TApplyResult));
        BuilderValidation.ValidateApiPath(_apiPath);
        BuilderValidation.ValidateRootObjectSchema(argsSchema, "Mutation args");
        BuilderValidation.ValidateRequiredText(_description, nameof(_description));
        BuilderValidation.ValidateRequiredItems(_useWhen, nameof(_useWhen));
        BuilderValidation.ValidateRequiredItems(_doNotUseWhen, nameof(_doNotUseWhen));
        ArgumentNullException.ThrowIfNull(_previewHandler);
        ArgumentNullException.ThrowIfNull(_summaryFactory);
        ArgumentNullException.ThrowIfNull(_applyHandler);

        return new CapabilityDefinition
        {
            ApiPath = _apiPath,
            Description = _description!,
            Signature = SignatureFormatter.Format(_apiPath),
            UsageGuidance = new CapabilityUsageGuidance(_useWhen.ToArray(), _doNotUseWhen.ToArray(), _notes.ToArray()),
            Input = new CapabilityResult(typeof(TArgs), argsSchema),
            Result = new CapabilityResult(typeof(TPreview), MutationEnvelopeSchemaFactory.CreatePreviewEnvelopeSchema(_apiPath, argsSchema, previewSchema)),
            Examples = Array.Empty<CapabilityExample>(),
            IsMutation = true,
            InputType = typeof(TArgs),
            ResultType = typeof(TPreview),
            PreviewPayloadSchema = previewSchema,
            ApplyResultSchema = applyResultSchema,
            ApplyResultType = typeof(TApplyResult),
            MutationPreviewHandler = async (args, context) =>
            {
                var typedArgs = JsonSerializerContract.DeserializeFromNode<TArgs>(args);
                var preview = await _previewHandler(typedArgs, context);
                return JsonSerializerContract.SerializeToNode(preview);
            },
            MutationSummaryFactory = async (args, preview, context) =>
            {
                var typedArgs = JsonSerializerContract.DeserializeFromNode<TArgs>(args);
                var typedPreview = JsonSerializerContract.DeserializeFromNode<TPreview>(preview);
                return await _summaryFactory(typedArgs, typedPreview, context);
            },
            MutationApplyHandler = async (args, context) =>
            {
                var typedArgs = JsonSerializerContract.DeserializeFromNode<TArgs>(args);
                var applyResult = await _applyHandler(typedArgs, context);
                return applyResult.IsSuccess
                    ? MutationApplyResult<JsonNode?>.Success(JsonSerializerContract.SerializeToNode(applyResult.Value))
                    : applyResult.FailureKind == MutationApplyFailureKind.Retryable
                        ? MutationApplyResult<JsonNode?>.RetryableFailure(applyResult.FailureCode!, applyResult.Message!)
                        : MutationApplyResult<JsonNode?>.TerminalFailure(applyResult.FailureCode!, applyResult.Message!);
            }
        };
    }
}

internal interface ICapabilityRegistration
{
    CapabilityDefinition BuildDefinition(BuiltInSchemaGenerator schemaGenerator);
}

internal interface IMutationRegistration
{
}

internal static class SignatureFormatter
{
    public static string Format(string apiPath)
    {
        var identifierBase = ApiPathUtilities.ToPascalCaseIdentifier(apiPath);
        return $"{apiPath}(input: {identifierBase}Input) -> Promise<{identifierBase}Result>";
    }
}

internal static class MutationEnvelopeSchemaFactory
{
    public static JsonNode CreatePreviewEnvelopeSchema(string apiPath, JsonNode argsSchema, JsonNode previewSchema)
    {
        return new JsonObject
        {
            ["$schema"] = ProgrammaticContractConstants.JsonSchemaDialect,
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mutation_preview") },
                ["approvalId"] = new JsonObject { ["type"] = "string" },
                ["approvalNonce"] = new JsonObject { ["type"] = "string" },
                ["mutationName"] = new JsonObject { ["type"] = "string", ["const"] = apiPath },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["args"] = argsSchema.DeepClone(),
                ["preview"] = previewSchema.DeepClone(),
                ["actionArgsHash"] = new JsonObject { ["type"] = "string" },
                ["expiresAt"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("kind", "approvalId", "approvalNonce", "mutationName", "summary", "args", "preview", "actionArgsHash", "expiresAt")
        };
    }
}
