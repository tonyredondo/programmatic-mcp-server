using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

public static class ProgrammaticContractConstants
{
    public const string GeneratedRuntimeContractVersion = "programmatic-runtime-v1";
    public const int SchemaVersion = 1;
    public const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";
}

public enum CapabilityDetailLevel
{
    Names,
    Signatures,
    Full
}

public enum ApprovalState
{
    Pending,
    Applying,
    Completed,
    Cancelled,
    FailedTerminal
}

public enum MutationApplyFailureKind
{
    Retryable,
    Terminal
}

public enum ApprovalTransitionStatus
{
    Success,
    NotFound,
    UnexpectedState
}

public sealed record CapabilityUsageGuidance(
    IReadOnlyList<string> UseWhen,
    IReadOnlyList<string> DoNotUseWhen,
    IReadOnlyList<string> Notes)
{
    public static CapabilityUsageGuidance Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

public sealed record CapabilityExample(string Description, JsonNode? Input, JsonNode? Result);

public sealed record CapabilityParameter(string Name, JsonNode Schema, bool Required);

public sealed record CapabilityResult(Type ClrType, JsonNode Schema);

public sealed record CapabilitySearchRequest(
    string? Query = null,
    CapabilityDetailLevel DetailLevel = CapabilityDetailLevel.Full,
    int Limit = 20,
    string? Cursor = null);

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

public sealed record CapabilitySearchResponse(
    int SchemaVersion,
    string CapabilityVersion,
    CapabilityDetailLevel DetailLevel,
    IReadOnlyList<CapabilitySearchItem> Items,
    string? NextCursor);

public sealed record CodeExecutionRequest(
    string ConversationId,
    string Code,
    string Entrypoint = "main",
    JsonNode? Args = null,
    IReadOnlyList<string>? VisibleApiPaths = null,
    int? TimeoutMs = null,
    int? MaxApiCalls = null,
    int? MaxResultBytes = null,
    int? MaxStatements = null,
    int? MemoryBytes = null,
    string? CallerBindingId = null,
    IServiceProvider? Services = null,
    object? Principal = null);

public sealed record ExecutionDiagnostic(string Code, string Message, JsonObject? Data = null);

public sealed record ExecutionConsoleEntry(string Level, string Message);

public sealed record ExecutionArtifactDescriptor(
    string ArtifactId,
    string Kind,
    string Name,
    string MimeType,
    int TotalBytes,
    int TotalChunks,
    string ExpiresAt);

public sealed record ExecutionStats(
    int ApiCalls,
    int ElapsedMs,
    int StatementsExecuted,
    int PeakMemoryBytes,
    int ConsoleLinesEmitted);

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

public interface ICodeExecutor
{
    ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface ICodeExecutionService
{
    ValueTask<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed record ProgrammaticCapabilityContext(
    string ConversationId,
    string? CallerBindingId,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    IArtifactWriter? Artifacts = null);

public sealed record ProgrammaticMutationContext(
    string ConversationId,
    string? CallerBindingId,
    string? ApprovalId,
    IServiceProvider Services,
    CancellationToken CancellationToken,
    IArtifactWriter? Artifacts = null);

public sealed record ProgrammaticAuthorizationContext(
    string Operation,
    string ConversationId,
    string CallerBindingId,
    string? MutationName,
    object? Principal);

public interface IProgrammaticAuthorizationPolicy
{
    ValueTask<bool> AuthorizeAsync(ProgrammaticAuthorizationContext context, CancellationToken cancellationToken = default);
}

public sealed class AllowAllBoundCallersAuthorizationPolicy : IProgrammaticAuthorizationPolicy
{
    public ValueTask<bool> AuthorizeAsync(ProgrammaticAuthorizationContext context, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(true);
    }
}

public sealed class CapabilityDefinition
{
    public required string ApiPath { get; init; }

    public required string Description { get; init; }

    public required string Signature { get; init; }

    public required CapabilityUsageGuidance UsageGuidance { get; init; }

    public required CapabilityResult Input { get; init; }

    public required CapabilityResult Result { get; init; }

    public required IReadOnlyList<CapabilityExample> Examples { get; init; }

    public required bool IsMutation { get; init; }

    public required Type InputType { get; init; }

    public required Type ResultType { get; init; }

    public JsonNode? ApplyResultSchema { get; init; }

    public Type? ApplyResultType { get; init; }

    public JsonNode? PreviewPayloadSchema { get; init; }

    public Func<JsonObject, ProgrammaticCapabilityContext, ValueTask<JsonNode?>>? CapabilityHandler { get; init; }

    public Func<JsonObject, ProgrammaticMutationContext, ValueTask<JsonNode?>>? MutationPreviewHandler { get; init; }

    public Func<JsonObject, JsonNode?, ProgrammaticMutationContext, ValueTask<string>>? MutationSummaryFactory { get; init; }

    public Func<JsonObject, ProgrammaticMutationContext, ValueTask<MutationApplyResult<JsonNode?>>>? MutationApplyHandler { get; init; }
}

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

public sealed record ArtifactReadItem(int Index, string Text, int Bytes);

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

public sealed record MutationListResponse(
    int SchemaVersion,
    string CapabilityVersion,
    IReadOnlyList<MutationPreviewEnvelope> Items,
    string? NextCursor);

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

public sealed record MutationCancelResponse(
    int SchemaVersion,
    string CapabilityVersion,
    string Status,
    string ApprovalId,
    string? ActionArgsHash);

public sealed class MutationApplyResult<TApplyResult>
{
    private MutationApplyResult(bool isSuccess, TApplyResult? value, string? failureCode, string? message, MutationApplyFailureKind? failureKind)
    {
        IsSuccess = isSuccess;
        Value = value;
        FailureCode = failureCode;
        Message = message;
        FailureKind = failureKind;
    }

    public bool IsSuccess { get; }

    public TApplyResult? Value { get; }

    public string? FailureCode { get; }

    public string? Message { get; }

    public MutationApplyFailureKind? FailureKind { get; }

    public static MutationApplyResult<TApplyResult> Success(TApplyResult value) => new(true, value, null, null, null);

    public static MutationApplyResult<TApplyResult> RetryableFailure(string code, string message) =>
        new(false, default, code, message, MutationApplyFailureKind.Retryable);

    public static MutationApplyResult<TApplyResult> TerminalFailure(string code, string message) =>
        new(false, default, code, message, MutationApplyFailureKind.Terminal);
}

public interface ICapabilityCatalog
{
    IReadOnlyList<CapabilityDefinition> Capabilities { get; }

    string CapabilityVersion { get; }

    string GeneratedTypeScript { get; }

    IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; }

    CapabilitySearchResponse Search(CapabilitySearchRequest request);
}

public sealed class ProgrammaticCatalogSnapshot : ICapabilityCatalog
{
    private readonly IReadOnlyList<CapabilityDefinition> _capabilities;

    public ProgrammaticCatalogSnapshot(
        IReadOnlyList<CapabilityDefinition> capabilities,
        string capabilityVersion,
        string generatedTypeScript,
        IProgrammaticAuthorizationPolicy authorizationPolicy)
    {
        _capabilities = capabilities;
        CapabilityVersion = capabilityVersion;
        GeneratedTypeScript = generatedTypeScript;
        AuthorizationPolicy = authorizationPolicy;
    }

    public IReadOnlyList<CapabilityDefinition> Capabilities => _capabilities;

    public string CapabilityVersion { get; }

    public string GeneratedTypeScript { get; }

    public IProgrammaticAuthorizationPolicy AuthorizationPolicy { get; }

    public CapabilitySearchResponse Search(CapabilitySearchRequest request)
    {
        var matches = CapabilitySearchEngine.Search(_capabilities, CapabilityVersion, request);
        return matches;
    }
}

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
                capability.Examples,
                capability.UsageGuidance)
        };
    }

    private sealed record SearchScore(bool IsMatch, int TotalScore, int MatchedTokenCount)
    {
        public static SearchScore NoMatch { get; } = new(false, 0, 0);
    }
}

public static class CursorCodec
{
    public static string CreateOffsetCursor(int offset, string capabilityVersion)
    {
        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["capabilityVersion"] = capabilityVersion
        };

        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

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
