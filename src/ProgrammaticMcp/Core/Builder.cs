using System.Reflection;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

/// <summary>
/// Entry point for registering capabilities and mutations.
/// </summary>
public sealed class ProgrammaticMcpBuilder
{
    private readonly List<ICapabilityRegistration> _registrations = new();
    private IProgrammaticAuthorizationPolicy? _authorizationPolicy;
    private bool _allowAllBoundCallers;

    /// <summary>
    /// Registers a capability with the supplied API path.
    /// </summary>
    public ProgrammaticMcpBuilder AddCapability<TInput, TResult>(string apiPath, Action<CapabilityBuilder<TInput, TResult>> configure)
    {
        var builder = new CapabilityBuilder<TInput, TResult>(apiPath);
        configure(builder);
        _registrations.Add(builder);
        return this;
    }

    /// <summary>
    /// Registers a mutation with the supplied API path.
    /// </summary>
    public ProgrammaticMcpBuilder AddMutation<TArgs, TPreview, TApplyResult>(string apiPath, Action<MutationBuilder<TArgs, TPreview, TApplyResult>> configure)
    {
        var builder = new MutationBuilder<TArgs, TPreview, TApplyResult>(apiPath);
        configure(builder);
        _registrations.Add(builder);
        return this;
    }

    /// <summary>
    /// Uses the supplied authorization policy for mutations.
    /// </summary>
    public ProgrammaticMcpBuilder UseAuthorizationPolicy(IProgrammaticAuthorizationPolicy policy)
    {
        _authorizationPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Allows all callers that have a stable caller binding.
    /// </summary>
    public ProgrammaticMcpBuilder AllowAllBoundCallers()
    {
        _allowAllBoundCallers = true;
        return this;
    }

    /// <summary>
    /// Builds the immutable catalog snapshot from the registered capabilities.
    /// </summary>
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

/// <summary>
/// Builder for a non-mutating capability.
/// </summary>
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

    /// <summary>
    /// Creates a capability builder for the specified API path.
    /// </summary>
    internal CapabilityBuilder(string apiPath)
    {
        _apiPath = apiPath;
    }

    /// <summary>
    /// Sets the human-readable description.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>
    /// Adds a usage hint describing when the capability should be used.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> UseWhen(string useWhen)
    {
        _useWhen.Add(useWhen);
        return this;
    }

    /// <summary>
    /// Adds a usage hint describing when the capability should not be used.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> DoNotUseWhen(string doNotUseWhen)
    {
        _doNotUseWhen.Add(doNotUseWhen);
        return this;
    }

    /// <summary>
    /// Adds a free-form usage note.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> AddGuidanceNote(string note)
    {
        _notes.Add(note);
        return this;
    }

    /// <summary>
    /// Sets the capability handler.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> WithHandler(Func<TInput, ProgrammaticCapabilityContext, ValueTask<TResult>> handler)
    {
        _handler = handler;
        return this;
    }

    /// <summary>
    /// Adds an example input and result pair.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> AddExample(string description, TInput input, TResult result)
    {
        _examples.Add(
            new CapabilityExample(
                description,
                JsonSerializerContract.SerializeToNode(input),
                JsonSerializerContract.SerializeToNode(result)));
        return this;
    }

    /// <summary>
    /// Overrides the generated input schema.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> WithInputSchemaOverride(JsonNode schema)
    {
        _inputSchemaOverride = schema.DeepClone();
        return this;
    }

    /// <summary>
    /// Overrides the generated result schema.
    /// </summary>
    public CapabilityBuilder<TInput, TResult> WithResultSchemaOverride(JsonNode schema)
    {
        _resultSchemaOverride = schema.DeepClone();
        return this;
    }

    /// <inheritdoc />
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

/// <summary>
/// Builder for a mutation capability.
/// </summary>
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

    /// <summary>
    /// Creates a mutation builder for the specified API path.
    /// </summary>
    internal MutationBuilder(string apiPath)
    {
        _apiPath = apiPath;
    }

    /// <summary>
    /// Sets the human-readable description.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>
    /// Adds a usage hint describing when the mutation should be used.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> UseWhen(string useWhen)
    {
        _useWhen.Add(useWhen);
        return this;
    }

    /// <summary>
    /// Adds a usage hint describing when the mutation should not be used.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> DoNotUseWhen(string doNotUseWhen)
    {
        _doNotUseWhen.Add(doNotUseWhen);
        return this;
    }

    /// <summary>
    /// Adds a free-form usage note.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> AddGuidanceNote(string note)
    {
        _notes.Add(note);
        return this;
    }

    /// <summary>
    /// Sets the preview handler.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithPreviewHandler(Func<TArgs, ProgrammaticMutationContext, ValueTask<TPreview>> handler)
    {
        _previewHandler = handler;
        return this;
    }

    /// <summary>
    /// Sets the summary factory used for approval previews.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithSummaryFactory(Func<TArgs, TPreview, ProgrammaticMutationContext, ValueTask<string>> factory)
    {
        _summaryFactory = factory;
        return this;
    }

    /// <summary>
    /// Sets the apply handler.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithApplyHandler(
        Func<TArgs, ProgrammaticMutationContext, ValueTask<MutationApplyResult<TApplyResult>>> handler)
    {
        _applyHandler = handler;
        return this;
    }

    /// <summary>
    /// Overrides the generated args schema.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithArgsSchemaOverride(JsonNode schema)
    {
        _argsSchemaOverride = schema.DeepClone();
        return this;
    }

    /// <summary>
    /// Overrides the generated preview schema.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithPreviewSchemaOverride(JsonNode schema)
    {
        _previewSchemaOverride = schema.DeepClone();
        return this;
    }

    /// <summary>
    /// Overrides the generated apply-result schema.
    /// </summary>
    public MutationBuilder<TArgs, TPreview, TApplyResult> WithApplyResultSchemaOverride(JsonNode schema)
    {
        _applyResultSchemaOverride = schema.DeepClone();
        return this;
    }

    /// <inheritdoc />
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

/// <summary>
/// Internal contract for capability registrations.
/// </summary>
internal interface ICapabilityRegistration
{
    CapabilityDefinition BuildDefinition(BuiltInSchemaGenerator schemaGenerator);
}

/// <summary>
/// Marker interface for mutation registrations.
/// </summary>
internal interface IMutationRegistration
{
}

/// <summary>
/// Formats signature strings for the generated catalog.
/// </summary>
internal static class SignatureFormatter
{
    /// <summary>
    /// Formats a stable signature string for the supplied API path.
    /// </summary>
    public static string Format(string apiPath)
    {
        var identifierBase = ApiPathUtilities.ToPascalCaseIdentifier(apiPath);
        return $"{apiPath}(input: {identifierBase}Input) -> Promise<{identifierBase}Result>";
    }
}

/// <summary>
/// Creates the JSON schema for mutation preview envelopes.
/// </summary>
internal static class MutationEnvelopeSchemaFactory
{
    /// <summary>
    /// Creates the JSON schema for the preview envelope emitted during mutation registration.
    /// </summary>
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
