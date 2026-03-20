using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

/// <summary>
/// Constants shared by the public programmatic MCP contracts.
/// </summary>
public static class ProgrammaticContractConstants
{
    /// <summary>
    /// Runtime contract version embedded into capability hashes and generated outputs.
    /// </summary>
    public const string GeneratedRuntimeContractVersion = "programmatic-runtime-v2";

    /// <summary>
    /// Schema version used by the public response envelopes.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// JSON Schema dialect emitted by the built-in schema generator.
    /// </summary>
    public const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";
}

/// <summary>
/// Controls the level of detail returned by capability search.
/// </summary>
public enum CapabilityDetailLevel
{
    /// <summary>Return only capability names and structural information.</summary>
    Names,
    /// <summary>Return capability signatures and structural information.</summary>
    Signatures,
    /// <summary>Return the full searchable capability payload.</summary>
    Full
}

/// <summary>
/// Represents the lifecycle state of a stored approval.
/// </summary>
public enum ApprovalState
{
    /// <summary>The approval is pending user or host action.</summary>
    Pending,
    /// <summary>The approval is currently being applied.</summary>
    Applying,
    /// <summary>The approval completed successfully.</summary>
    Completed,
    /// <summary>The approval was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The approval failed terminally and should not be retried.</summary>
    FailedTerminal
}

/// <summary>
/// Describes whether a mutation apply failure can be retried.
/// </summary>
public enum MutationApplyFailureKind
{
    /// <summary>The apply failure can be retried safely.</summary>
    Retryable,
    /// <summary>The apply failure is terminal.</summary>
    Terminal
}

/// <summary>
/// Result of attempting to transition an approval to a new state.
/// </summary>
public enum ApprovalTransitionStatus
{
    /// <summary>The transition succeeded.</summary>
    Success,
    /// <summary>The approval was not found.</summary>
    NotFound,
    /// <summary>The approval existed, but not in the expected state.</summary>
    UnexpectedState
}

/// <summary>
/// Guidance for when a capability should or should not be used.
/// </summary>
public sealed record CapabilityUsageGuidance(
    IReadOnlyList<string> UseWhen,
    IReadOnlyList<string> DoNotUseWhen,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// An empty guidance payload with no examples or notes.
    /// </summary>
    public static CapabilityUsageGuidance Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Concrete capability example input and output.
/// </summary>
public sealed record CapabilityExample(string Description, JsonNode? Input, JsonNode? Result);

/// <summary>
/// Describes a single capability parameter.
/// </summary>
public sealed record CapabilityParameter(string Name, JsonNode Schema, bool Required);

/// <summary>
/// Describes the CLR type and JSON schema for a capability payload.
/// </summary>
public sealed record CapabilityResult(Type ClrType, JsonNode Schema);

/// <summary>
/// Immutable definition of a registered MCP resource.
/// </summary>
public sealed record ProgrammaticResourceDefinition(
    string Uri,
    string Name,
    string Description,
    string MimeType);

/// <summary>
/// Runtime context passed to resource readers.
/// </summary>
public sealed record ProgrammaticResourceContext(
    string Uri,
    IServiceProvider Services,
    CancellationToken CancellationToken);

/// <summary>
/// Result of reading a registered MCP resource.
/// </summary>
public sealed record ProgrammaticResourceReadResult(
    string Uri,
    string MimeType,
    string Text);

/// <summary>
/// Single text message sent through the public sampling API.
/// </summary>
public sealed record ProgrammaticSamplingMessage(
    string Role,
    string Text);

/// <summary>
/// Request envelope for transport-neutral sampling.
/// </summary>
public sealed record ProgrammaticSamplingRequest(
    string? SystemPrompt,
    IReadOnlyList<ProgrammaticSamplingMessage> Messages,
    bool EnableTools = false,
    IReadOnlyList<string>? AllowedToolNames = null,
    int? MaxTokens = null);

/// <summary>
/// Result envelope for transport-neutral sampling.
/// </summary>
public sealed record ProgrammaticSamplingResult(
    string Text,
    string Model,
    string? StopReason);

/// <summary>
/// Runtime context passed to sampling-tool handlers.
/// </summary>
public sealed record ProgrammaticSamplingToolContext(
    string ConversationId,
    string? CallerBindingId,
    IServiceProvider Services,
    CancellationToken CancellationToken);

/// <summary>
/// Public host-facing abstraction for client sampling.
/// </summary>
public interface IProgrammaticSamplingClient
{
    /// <summary>Gets whether sampling is available in the current execution context.</summary>
    bool IsSupported { get; }

    /// <summary>Gets whether the current execution context supports tool-enabled sampling.</summary>
    bool SupportsToolUse { get; }

    /// <summary>Creates a sampled assistant message.</summary>
    ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception raised when sampling is blocked, unavailable, or fails with a structured programmatic code.
/// </summary>
public sealed class ProgrammaticSamplingException : Exception
{
    /// <summary>
    /// Creates a new sampling exception.
    /// </summary>
    public ProgrammaticSamplingException(string code, string message, JsonObject? data = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Data = data;
    }

    /// <summary>Structured programmatic error code.</summary>
    public string Code { get; }

    /// <summary>Optional structured error data.</summary>
    public new JsonObject? Data { get; }
}

/// <summary>
/// Request envelope for capability search.
/// </summary>
public sealed record CapabilitySearchRequest(
    string? Query = null,
    CapabilityDetailLevel DetailLevel = CapabilityDetailLevel.Full,
    int Limit = 20,
    string? Cursor = null);

/// <summary>
/// Single capability search result item.
/// </summary>
public sealed record CapabilitySearchItem(
    string ApiPath,
    string? Signature,
    string? Description,
    JsonNode? InputSchema,
    JsonNode? ResultSchema,
    JsonNode? PreviewPayloadSchema,
    JsonNode? ApplyResultSchema,
    IReadOnlyList<CapabilityExample> Examples,
    CapabilityUsageGuidance? Guidance);

/// <summary>
/// Search response envelope for the capability catalog.
/// </summary>
public sealed record CapabilitySearchResponse(
    int SchemaVersion,
    string CapabilityVersion,
    CapabilityDetailLevel DetailLevel,
    IReadOnlyList<CapabilitySearchItem> Items,
    string? NextCursor);

/// <summary>
/// Request to execute code against the generated programmatic namespace.
/// </summary>
public sealed record CodeExecutionRequest(
    string ConversationId,
    string Code,
    string Entrypoint = "main",
    JsonObject? Args = null,
    IReadOnlyList<string>? VisibleApiPaths = null,
    int? TimeoutMs = null,
    int? MaxApiCalls = null,
    int? MaxResultBytes = null,
    int? MaxStatements = null,
    int? MemoryBytes = null,
    string? CallerBindingId = null,
    IServiceProvider? Services = null,
    object? Principal = null);

/// <summary>
/// Diagnostic emitted while executing programmatic code.
/// </summary>
public sealed record ExecutionDiagnostic(string Code, string Message, JsonObject? Data = null);

/// <summary>
/// Captured console entry emitted during execution.
/// </summary>
public sealed record ExecutionConsoleEntry(string Level, string Message);

/// <summary>
/// Descriptor for an artifact created during execution.
/// </summary>
public sealed record ExecutionArtifactDescriptor(
    string ArtifactId,
    string Kind,
    string Name,
    string MimeType,
    int TotalBytes,
    int TotalChunks,
    string ExpiresAt);

/// <summary>
/// Runtime statistics for a programmatic execution.
/// </summary>
public sealed record ExecutionStats(
    int ApiCalls,
    int ElapsedMs,
    int StatementsExecuted,
    int PeakMemoryBytes,
    int ConsoleLinesEmitted);

/// <summary>
/// Result envelope for code execution.
/// </summary>
public sealed record CodeExecutionResult(
    int SchemaVersion,
    string CapabilityVersion,
    JsonNode? Result,
    IReadOnlyList<ExecutionConsoleEntry> Console,
    IReadOnlyList<ExecutionDiagnostic> Diagnostics,
    IReadOnlyList<ExecutionArtifactDescriptor> Artifacts,
    IReadOnlyList<MutationPreviewEnvelope> ApprovalsRequested,
    string? ResultArtifactId,
    IReadOnlyList<string>? EffectiveVisibleApiPaths,
    ExecutionStats Stats);

/// <summary>
/// Executes programmatic code against a catalog snapshot.
/// </summary>
public interface ICodeExecutor
{
    /// <summary>
    /// Executes the supplied request and returns the full execution envelope.
    /// </summary>
    ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-level abstraction for code execution.
/// </summary>
public interface ICodeExecutionService
{
    /// <summary>
    /// Executes the supplied request and returns the full execution envelope.
    /// </summary>
    ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime context passed to capability handlers.
/// </summary>
public sealed record ProgrammaticCapabilityContext(
    string ConversationId,
    string? CallerBindingId,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    IArtifactWriter? Artifacts = null);

/// <summary>
/// Helpers for retrieving contextual services from capability execution.
/// </summary>
public static class ProgrammaticCapabilityContextSamplingExtensions
{
    /// <summary>
    /// Resolves the contextual sampling client for the supplied capability context.
    /// </summary>
    public static IProgrammaticSamplingClient GetSamplingClient(this ProgrammaticCapabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ProgrammaticSamplingServiceResolver.ResolvePublic(context.Services);
    }
}

/// <summary>
/// Runtime context passed to mutation handlers.
/// </summary>
public sealed record ProgrammaticMutationContext(
    string ConversationId,
    string? CallerBindingId,
    string? ApprovalId,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    IArtifactWriter? Artifacts = null);

/// <summary>
/// Authorization context for a programmatic operation.
/// </summary>
public sealed record ProgrammaticAuthorizationContext(
    string Operation,
    string ConversationId,
    string CallerBindingId,
    string? MutationName,
    object? Principal);

/// <summary>
/// Policy interface for authorizing programmatic operations.
/// </summary>
public interface IProgrammaticAuthorizationPolicy
{
    /// <summary>
    /// Determines whether the requested operation is authorized.
    /// </summary>
    ValueTask<bool> AuthorizeAsync(ProgrammaticAuthorizationContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorization policy that allows all bound callers.
/// </summary>
public sealed class AllowAllBoundCallersAuthorizationPolicy : IProgrammaticAuthorizationPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> AuthorizeAsync(ProgrammaticAuthorizationContext context, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(true);
    }
}

/// <summary>
/// Definition of a registered capability or mutation.
/// </summary>
public sealed class CapabilityDefinition
{
    /// <summary>Capability API path.</summary>
    public required string ApiPath { get; init; }

    /// <summary>Human-readable capability description.</summary>
    public required string Description { get; init; }

    /// <summary>Generated signature string.</summary>
    public required string Signature { get; set; }

    /// <summary>Usage guidance for the capability.</summary>
    public required CapabilityUsageGuidance UsageGuidance { get; init; }

    /// <summary>Input payload metadata.</summary>
    public required CapabilityResult Input { get; init; }

    /// <summary>Result payload metadata.</summary>
    public required CapabilityResult Result { get; init; }

    /// <summary>Examples attached to the capability.</summary>
    public required IReadOnlyList<CapabilityExample> Examples { get; init; }

    /// <summary>Indicates whether the capability represents a mutation.</summary>
    public required bool IsMutation { get; init; }

    /// <summary>CLR input type.</summary>
    public required Type InputType { get; init; }

    /// <summary>CLR result type.</summary>
    public required Type ResultType { get; init; }

    /// <summary>Optional apply-result schema.</summary>
    public JsonNode? ApplyResultSchema { get; init; }

    /// <summary>Optional apply-result CLR type.</summary>
    public Type? ApplyResultType { get; init; }

    /// <summary>Optional preview payload schema.</summary>
    public JsonNode? PreviewPayloadSchema { get; init; }

    /// <summary>Capability execution handler.</summary>
    public Func<JsonObject, ProgrammaticCapabilityContext, ValueTask<JsonNode?>>? CapabilityHandler { get; init; }

    /// <summary>Mutation preview handler.</summary>
    public Func<JsonObject, ProgrammaticMutationContext, ValueTask<JsonNode?>>? MutationPreviewHandler { get; init; }

    /// <summary>Mutation summary factory.</summary>
    public Func<JsonObject, JsonNode?, ProgrammaticMutationContext, ValueTask<string>>? MutationSummaryFactory { get; init; }

    /// <summary>Mutation apply handler.</summary>
    public Func<JsonObject, ProgrammaticMutationContext, ValueTask<MutationApplyResult<JsonNode?>>>? MutationApplyHandler { get; init; }
}

/// <summary>
/// Envelope describing a pending mutation approval.
/// </summary>
public sealed record MutationPreviewEnvelope(
    string Kind,
    string ApprovalId,
    string ApprovalNonce,
    string MutationName,
    string Summary,
    JsonObject Args,
    JsonNode? Preview,
    string ActionArgsHash,
    string ExpiresAt);

/// <summary>
/// Item returned from mutation rediscovery.
/// </summary>
public sealed record MutationListItem(
    string Kind,
    string ApprovalId,
    string MutationName,
    string Summary,
    JsonObject Args,
    JsonNode? Preview,
    string ActionArgsHash,
    string ExpiresAt);

/// <summary>
/// Item returned from artifact reads.
/// </summary>
public sealed record ArtifactReadItem(int Index, string Text, int Bytes);

/// <summary>
/// Response envelope for artifact reads.
/// </summary>
public sealed record ArtifactReadResponse(
    int SchemaVersion,
    string CapabilityVersion,
    bool Found,
    string? ArtifactId,
    string? Kind,
    string? Name,
    string? MimeType,
    IReadOnlyList<ArtifactReadItem> Items,
    string? NextCursor,
    int? TotalChunks,
    int? TotalBytes,
    string? ExpiresAt);

/// <summary>
/// Response envelope for mutation list operations.
/// </summary>
public sealed record MutationListResponse(
    int SchemaVersion,
    string CapabilityVersion,
    IReadOnlyList<MutationListItem> Items,
    string? NextCursor);

/// <summary>
/// Response envelope for mutation apply operations.
/// </summary>
public sealed record MutationApplyResponse(
    int SchemaVersion,
    string CapabilityVersion,
    string Status,
    string ApprovalId,
    string? ActionArgsHash,
    JsonNode? Result,
    IReadOnlyList<ExecutionArtifactDescriptor> Artifacts,
    string? ResultArtifactId,
    string? FailureCode,
    bool? Retryable,
    string? Message);

/// <summary>
/// Response envelope for mutation cancel operations.
/// </summary>
public sealed record MutationCancelResponse(
    int SchemaVersion,
    string CapabilityVersion,
    string Status,
    string ApprovalId,
    string? ActionArgsHash);

/// <summary>
/// Represents the result of a mutation apply operation.
/// </summary>
public sealed class MutationApplyResult<TApplyResult>
{
    /// <summary>
    /// Creates a new mutation apply result.
    /// </summary>
    private MutationApplyResult(bool isSuccess, TApplyResult? value, string? failureCode, string? message, MutationApplyFailureKind? failureKind)
    {
        IsSuccess = isSuccess;
        Value = value;
        FailureCode = failureCode;
        Message = message;
        FailureKind = failureKind;
    }

    /// <summary>
    /// Gets a value indicating whether the apply succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the success value when the apply succeeded.
    /// </summary>
    public TApplyResult? Value { get; }

    /// <summary>
    /// Gets the failure code when the apply failed.
    /// </summary>
    public string? FailureCode { get; }

    /// <summary>
    /// Gets the human-readable failure message when the apply failed.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets the failure kind when the apply failed.
    /// </summary>
    public MutationApplyFailureKind? FailureKind { get; }

    /// <summary>
    /// Creates a successful apply result.
    /// </summary>
    public static MutationApplyResult<TApplyResult> Success(TApplyResult value) => new(true, value, null, null, null);

    /// <summary>
    /// Creates a retryable failure result.
    /// </summary>
    public static MutationApplyResult<TApplyResult> RetryableFailure(string code, string message) =>
        new(false, default, code, message, MutationApplyFailureKind.Retryable);

    /// <summary>
    /// Creates a terminal failure result.
    /// </summary>
    public static MutationApplyResult<TApplyResult> TerminalFailure(string code, string message) =>
        new(false, default, code, message, MutationApplyFailureKind.Terminal);
}

internal interface IProgrammaticStructuredSamplingClient
{
    bool IsSupported { get; }

    bool SupportsToolUse { get; }

    ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
        ProgrammaticStructuredSamplingRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record ProgrammaticStructuredSamplingRequest(
    string? SystemPrompt,
    IReadOnlyList<ProgrammaticStructuredSamplingMessage> Messages,
    int MaxTokens,
    IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition>? Tools = null,
    ProgrammaticStructuredSamplingToolChoice? ToolChoice = null);

internal sealed record ProgrammaticStructuredSamplingMessage(
    string Role,
    IReadOnlyList<ProgrammaticStructuredSamplingContentBlock> Content);

internal abstract record ProgrammaticStructuredSamplingContentBlock;

internal sealed record ProgrammaticStructuredSamplingTextBlock(string Text) : ProgrammaticStructuredSamplingContentBlock;

internal sealed record ProgrammaticStructuredSamplingToolUseBlock(string Id, string Name, JsonNode? Input) : ProgrammaticStructuredSamplingContentBlock;

internal sealed record ProgrammaticStructuredSamplingToolResultBlock(
    string ToolUseId,
    string Text,
    bool IsError,
    JsonNode? StructuredContent = null) : ProgrammaticStructuredSamplingContentBlock;

internal sealed record ProgrammaticStructuredSamplingToolDefinition(
    string Name,
    string Description,
    JsonNode InputSchema);

internal sealed record ProgrammaticStructuredSamplingToolChoice(string Mode);

internal sealed record ProgrammaticStructuredSamplingResult(
    string Role,
    IReadOnlyList<ProgrammaticStructuredSamplingContentBlock> Content,
    string Model,
    string? StopReason);

internal sealed record RegisteredProgrammaticSamplingTool(
    ProgrammaticStructuredSamplingToolDefinition Definition,
    JsonNode ResultSchema,
    Func<JsonObject, ProgrammaticSamplingToolContext, ValueTask<JsonNode?>> Handler);

internal sealed record ProgrammaticSamplingToolExecutionResult(
    string Text,
    JsonNode? StructuredContent);

internal sealed record ProgrammaticSamplingLoopLimits(
    int MaxSamplingRounds,
    int MaxSamplingToolResultBytes);

internal static class ProgrammaticSamplingLoopLimitResolver
{
    public static ProgrammaticSamplingLoopLimits Default { get; } = new(12, 16_384);

    public static ProgrammaticSamplingLoopLimits Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(ProgrammaticSamplingLoopLimits)) as ProgrammaticSamplingLoopLimits ?? Default;
    }
}

internal interface IProgrammaticSamplingToolRegistry
{
    IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> Tools { get; }

    bool TryGetDefinition(string name, out ProgrammaticStructuredSamplingToolDefinition definition);

    ValueTask<ProgrammaticSamplingToolExecutionResult> InvokeAsync(
        string name,
        JsonObject arguments,
        ProgrammaticSamplingToolContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class ProgrammaticSamplingToolRegistry : IProgrammaticSamplingToolRegistry
{
    private readonly IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> _tools;
    private readonly IReadOnlyDictionary<string, RegisteredProgrammaticSamplingTool> _toolsByName;

    public ProgrammaticSamplingToolRegistry(IReadOnlyList<RegisteredProgrammaticSamplingTool> tools)
    {
        _tools = tools.Select(static tool => tool.Definition).ToArray();
        _toolsByName = tools.ToDictionary(static tool => tool.Definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ProgrammaticStructuredSamplingToolDefinition> Tools => _tools;

    public bool TryGetDefinition(string name, out ProgrammaticStructuredSamplingToolDefinition definition)
    {
        if (_toolsByName.TryGetValue(name, out var tool))
        {
            definition = tool.Definition;
            return true;
        }

        definition = null!;
        return false;
    }

    public async ValueTask<ProgrammaticSamplingToolExecutionResult> InvokeAsync(
        string name,
        JsonObject arguments,
        ProgrammaticSamplingToolContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);

        if (!_toolsByName.TryGetValue(name, out var tool))
        {
            throw new ProgrammaticSamplingException(
                "sampling_invalid_tool_call",
                $"Sampling tool '{name}' is not registered.");
        }

        JsonSchemaValidator.Validate(arguments, tool.Definition.InputSchema);
        var result = await tool.Handler(arguments, context);
        JsonSchemaValidator.Validate(result, tool.ResultSchema);
        return new ProgrammaticSamplingToolExecutionResult(CanonicalJson.Serialize(result), result?.DeepClone());
    }
}

internal static class ProgrammaticSamplingServiceResolver
{
    private static readonly IProgrammaticSamplingClient UnsupportedPublicClient = new UnsupportedProgrammaticSamplingClient();
    private static readonly IProgrammaticStructuredSamplingClient UnsupportedStructuredClient = new UnsupportedProgrammaticStructuredSamplingClient();

    public static IProgrammaticSamplingClient ResolvePublic(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(IProgrammaticSamplingClient)) as IProgrammaticSamplingClient ?? UnsupportedPublicClient;
    }

    public static IProgrammaticStructuredSamplingClient ResolveStructured(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(IProgrammaticStructuredSamplingClient)) as IProgrammaticStructuredSamplingClient ?? UnsupportedStructuredClient;
    }

    private sealed class UnsupportedProgrammaticSamplingClient : IProgrammaticSamplingClient
    {
        public bool IsSupported => false;

        public bool SupportsToolUse => false;

        public ValueTask<ProgrammaticSamplingResult> CreateMessageAsync(ProgrammaticSamplingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticSamplingResult>(
                new ProgrammaticSamplingException("sampling_unavailable", "Sampling is unavailable in this execution context."));
    }

    private sealed class UnsupportedProgrammaticStructuredSamplingClient : IProgrammaticStructuredSamplingClient
    {
        public bool IsSupported => false;

        public bool SupportsToolUse => false;

        public ValueTask<ProgrammaticStructuredSamplingResult> CreateMessageAsync(
            ProgrammaticStructuredSamplingRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<ProgrammaticStructuredSamplingResult>(
                new ProgrammaticSamplingException("sampling_unavailable", "Sampling is unavailable in this execution context."));
    }
}

/// <summary>
/// Immutable snapshot of the registered capabilities.
/// </summary>
public interface ICapabilityCatalog
{
    /// <summary>Registered capabilities.</summary>
    IReadOnlyList<CapabilityDefinition> Capabilities { get; }

    /// <summary>Registered resources.</summary>
    IReadOnlyList<ProgrammaticResourceDefinition> Resources { get; }

    /// <summary>Catalog capability version.</summary>
    string CapabilityVersion { get; }

    /// <summary>Generated TypeScript declaration payload.</summary>
    string GeneratedTypeScript { get; }

    /// <summary>Authorization policy attached to the snapshot.</summary>
    IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; }

    /// <summary>
    /// Searches the catalog.
    /// </summary>
    CapabilitySearchResponse Search(CapabilitySearchRequest request);

    /// <summary>
    /// Reads the resource registered for the supplied absolute URI.
    /// </summary>
    ValueTask<ProgrammaticResourceReadResult?> ReadResourceAsync(string uri, ProgrammaticResourceContext context);
}

/// <summary>
/// In-memory catalog snapshot built from registrations.
/// </summary>
public sealed class ProgrammaticCatalogSnapshot : ICapabilityCatalog
{
    private readonly IReadOnlyList<CapabilityDefinition> _capabilities;
    private readonly IReadOnlyList<ProgrammaticResourceDefinition> _resources;
    private readonly IReadOnlyDictionary<string, Func<ProgrammaticResourceContext, ValueTask<string>>> _resourceReaders;

    /// <summary>
    /// Creates a new catalog snapshot.
    /// </summary>
    internal ProgrammaticCatalogSnapshot(
        IReadOnlyList<CapabilityDefinition> capabilities,
        IReadOnlyList<RegisteredProgrammaticResource> resources,
        string capabilityVersion,
        string generatedTypeScript,
        IProgrammaticAuthorizationPolicy authorizationPolicy)
    {
        _capabilities = capabilities;
        _resources = resources.Select(static resource => resource.Definition).ToArray();
        _resourceReaders = resources.ToDictionary(
            static resource => resource.Definition.Uri,
            static resource => resource.Reader,
            StringComparer.Ordinal);
        CapabilityVersion = capabilityVersion;
        GeneratedTypeScript = generatedTypeScript;
        AuthorizationPolicy = authorizationPolicy;
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDefinition> Capabilities => _capabilities;

    /// <inheritdoc />
    public IReadOnlyList<ProgrammaticResourceDefinition> Resources => _resources;

    /// <inheritdoc />
    public string CapabilityVersion { get; }

    /// <inheritdoc />
    public string GeneratedTypeScript { get; }

    /// <inheritdoc />
    public IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; }

    /// <inheritdoc />
    public CapabilitySearchResponse Search(CapabilitySearchRequest request)
    {
        var matches = CapabilitySearchEngine.Search(_capabilities, CapabilityVersion, request);
        return matches;
    }

    /// <inheritdoc />
    public async ValueTask<ProgrammaticResourceReadResult?> ReadResourceAsync(string uri, ProgrammaticResourceContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(context);

        if (!_resourceReaders.TryGetValue(uri, out var reader))
        {
            return null;
        }

        var text = await reader(context);
        return new ProgrammaticResourceReadResult(uri, _resources.Single(resource => resource.Uri == uri).MimeType, text);
    }
}

internal sealed record RegisteredProgrammaticResource(
    ProgrammaticResourceDefinition Definition,
    Func<ProgrammaticResourceContext, ValueTask<string>> Reader);

internal static class CapabilitySearchEngine
{
    public static CapabilitySearchResponse Search(
        IReadOnlyList<CapabilityDefinition> capabilities,
        string capabilityVersion,
        CapabilitySearchRequest request)
    {
        if (request.Limit is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Limit), "Limit must be between 1 and 100.");
        }

        var ordered = capabilities.OrderBy(static capability => capability.ApiPath, StringComparer.Ordinal).ToArray();
        var normalizedTokens = NormalizeQueryTokens(request.Query);
        var filtered = normalizedTokens.Length == 0
            ? ordered
            : ordered
                .Select(capability => new { Capability = capability, Score = Score(capability, normalizedTokens) })
                .Where(static item => item.Score.IsMatch)
                .OrderByDescending(static item => item.Score.TotalScore)
                .ThenByDescending(static item => item.Score.MatchedTokenCount)
                .ThenBy(static item => item.Capability.ApiPath, StringComparer.Ordinal)
                .Select(static item => item.Capability)
                .ToArray();

        var offset = CursorCodec.ParseOffset(request.Cursor, capabilityVersion);
        var page = filtered.Skip(offset).Take(request.Limit).Select(capability => ToSearchItem(capability, request.DetailLevel)).ToArray();
        var nextOffset = offset + page.Length;
        var nextCursor = nextOffset < filtered.Length ? CursorCodec.CreateOffsetCursor(nextOffset, capabilityVersion) : null;

        return new CapabilitySearchResponse(
            ProgrammaticContractConstants.SchemaVersion,
            capabilityVersion,
            request.DetailLevel,
            page,
            nextCursor);
    }

    private static SearchScore Score(CapabilityDefinition capability, IReadOnlyList<string> tokens)
    {
        var apiPath = NormalizeSearchableText(capability.ApiPath);
        var description = NormalizeSearchableText(capability.Description);
        var guidance = capability.UsageGuidance.UseWhen
            .Concat(capability.UsageGuidance.DoNotUseWhen)
            .Concat(capability.UsageGuidance.Notes)
            .Select(NormalizeSearchableText)
            .ToArray();
        var exampleTexts = capability.Examples
            .SelectMany(
                static example =>
                {
                    var values = new List<string> { example.Description };
                    if (example.Input is not null)
                    {
                        values.Add(example.Input.ToJsonString());
                    }

                    if (example.Result is not null)
                    {
                        values.Add(example.Result.ToJsonString());
                    }

                    return values;
                })
            .Select(NormalizeSearchableText)
            .ToArray();

        var totalScore = 0;
        var matchedTokenCount = 0;

        foreach (var token in tokens)
        {
            var tokenScore = ScoreToken(token, apiPath, description, guidance, exampleTexts);
            if (tokenScore == 0)
            {
                return SearchScore.NoMatch;
            }

            totalScore += tokenScore;
            matchedTokenCount++;
        }

        return new SearchScore(true, totalScore, matchedTokenCount);
    }

    private static int ScoreToken(
        string token,
        string apiPath,
        string description,
        IReadOnlyList<string> guidance,
        IReadOnlyList<string> exampleTexts)
    {
        if (string.Equals(apiPath, token, StringComparison.Ordinal))
        {
            return 500;
        }

        if (apiPath.StartsWith(token, StringComparison.Ordinal))
        {
            return 400;
        }

        if (apiPath.Contains(token, StringComparison.Ordinal))
        {
            return 300;
        }

        if (description.Contains(token, StringComparison.Ordinal))
        {
            return 200;
        }

        if (guidance.Any(text => text.Contains(token, StringComparison.Ordinal)))
        {
            return 100;
        }

        if (exampleTexts.Any(text => text.Contains(token, StringComparison.Ordinal)))
        {
            return 50;
        }

        return 0;
    }

    private static string[] NormalizeQueryTokens(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var normalized = string.Join(
            ' ',
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSearchableText)
            .ToArray();
    }

    private static string NormalizeSearchableText(string value)
    {
        return string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private static CapabilitySearchItem ToSearchItem(CapabilityDefinition capability, CapabilityDetailLevel detailLevel)
    {
        return detailLevel switch
        {
            CapabilityDetailLevel.Names => new CapabilitySearchItem(
                capability.ApiPath,
                null,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<CapabilityExample>(),
                null),
            CapabilityDetailLevel.Signatures => new CapabilitySearchItem(
                capability.ApiPath,
                capability.Signature,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<CapabilityExample>(),
                null),
            _ => new CapabilitySearchItem(
                capability.ApiPath,
                capability.Signature,
                capability.Description,
                capability.Input.Schema.DeepClone(),
                capability.Result.Schema.DeepClone(),
                capability.PreviewPayloadSchema?.DeepClone(),
                capability.ApplyResultSchema?.DeepClone(),
                capability.Examples
                    .Select(static example => new CapabilityExample(
                        example.Description,
                        example.Input?.DeepClone(),
                        example.Result?.DeepClone()))
                    .ToArray(),
                capability.UsageGuidance is null
                    ? null
                    : new CapabilityUsageGuidance(
                        capability.UsageGuidance.UseWhen.ToArray(),
                        capability.UsageGuidance.DoNotUseWhen.ToArray(),
                        capability.UsageGuidance.Notes.ToArray()))
        };
    }

    private sealed record SearchScore(bool IsMatch, int TotalScore, int MatchedTokenCount)
    {
        public static SearchScore NoMatch { get; } = new(false, 0, 0);
    }
}

/// <summary>
/// Creates and parses opaque paging cursors tied to a specific capability version.
/// </summary>
public static class CursorCodec
{
    /// <summary>
    /// Creates a cursor for the supplied offset and catalog version.
    /// </summary>
    public static string CreateOffsetCursor(int offset, string capabilityVersion)
    {
        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["capabilityVersion"] = capabilityVersion
        };

        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    /// <summary>
    /// Parses an offset cursor and validates that it matches the current capability version.
    /// </summary>
    public static int ParseOffset(string? cursor, string capabilityVersion)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var payload = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Cursor payload is missing.");
            var version = payload["capabilityVersion"]?.GetValue<string>();
            if (!string.Equals(version, capabilityVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cursor is stale.");
            }

            return payload["offset"]?.GetValue<int>() ?? 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("Cursor is invalid or stale.", exception);
        }
    }
}
