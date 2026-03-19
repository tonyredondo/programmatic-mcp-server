using System.Text;

namespace ProgrammaticMcp.Jint;

public sealed record JintExecutorOptions
{
    public int TimeoutMs { get; init; } = 5_000;

    public int MaxApiCalls { get; init; } = 200;

    public int MaxResultBytes { get; init; } = 262_144;

    public int MaxStatements { get; init; } = 20_000;

    public int MemoryBytes { get; init; } = 8_388_608;

    public int MaxCodeBytes { get; init; } = 262_144;

    public int MaxArgsBytes { get; init; } = 65_536;

    public int MaxConsoleLines { get; init; } = 500;

    public int MaxConsoleBytes { get; init; } = 65_536;

    public int MaxApprovalsPerExecution { get; init; } = 16;

    public int MaxApprovalPayloadBytes { get; init; } = 32_768;

    public int ApprovalTtlSeconds { get; init; } = 600;

    public ArtifactRetentionOptions ArtifactRetention { get; init; } =
        new(
            ArtifactTtlSeconds: 86_400,
            MaxArtifactBytesPerArtifact: 10_485_760,
            MaxArtifactsPerConversation: 200,
            MaxArtifactBytesPerConversation: 104_857_600,
            MaxArtifactBytesGlobal: 536_870_912,
            ArtifactChunkBytes: 65_536);

    internal EffectiveExecutionLimits Resolve(CodeExecutionRequest request)
    {
        return new EffectiveExecutionLimits(
            TimeoutMs: ResolveRequestValue(request.TimeoutMs, TimeoutMs, nameof(request.TimeoutMs)),
            MaxApiCalls: ResolveRequestValue(request.MaxApiCalls, MaxApiCalls, nameof(request.MaxApiCalls)),
            MaxResultBytes: ResolveRequestValue(request.MaxResultBytes, MaxResultBytes, nameof(request.MaxResultBytes)),
            MaxStatements: ResolveRequestValue(request.MaxStatements, MaxStatements, nameof(request.MaxStatements)),
            MemoryBytes: ResolveRequestValue(request.MemoryBytes, MemoryBytes, nameof(request.MemoryBytes)),
            MaxCodeBytes: RequirePositive(MaxCodeBytes, nameof(MaxCodeBytes)),
            MaxArgsBytes: RequirePositive(MaxArgsBytes, nameof(MaxArgsBytes)),
            MaxConsoleLines: RequirePositive(MaxConsoleLines, nameof(MaxConsoleLines)),
            MaxConsoleBytes: RequirePositive(MaxConsoleBytes, nameof(MaxConsoleBytes)),
            MaxApprovalsPerExecution: RequirePositive(MaxApprovalsPerExecution, nameof(MaxApprovalsPerExecution)),
            MaxApprovalPayloadBytes: RequirePositive(MaxApprovalPayloadBytes, nameof(MaxApprovalPayloadBytes)),
            ApprovalTtlSeconds: RequirePositive(ApprovalTtlSeconds, nameof(ApprovalTtlSeconds)),
            ArtifactRetention: ArtifactRetention);
    }

    internal void Validate()
    {
        _ = Resolve(new CodeExecutionRequest("validation", "async function main() { return null; }"));
    }

    private static int ResolveRequestValue(int? requested, int configuredDefault, string name)
    {
        var safeDefault = RequirePositive(configuredDefault, name);
        if (!requested.HasValue)
        {
            return safeDefault;
        }

        if (requested.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Execution limit values must be positive integers.");
        }

        return Math.Min(requested.Value, safeDefault);
    }

    private static int RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Execution limit values must be positive integers.");
        }

        return value;
    }
}

internal sealed record EffectiveExecutionLimits(
    int TimeoutMs,
    int MaxApiCalls,
    int MaxResultBytes,
    int MaxStatements,
    int MemoryBytes,
    int MaxCodeBytes,
    int MaxArgsBytes,
    int MaxConsoleLines,
    int MaxConsoleBytes,
    int MaxApprovalsPerExecution,
    int MaxApprovalPayloadBytes,
    int ApprovalTtlSeconds,
    ArtifactRetentionOptions ArtifactRetention)
{
    public void ValidatePayloadBounds(CodeExecutionRequest request)
    {
        if (Encoding.UTF8.GetByteCount(request.Code) > MaxCodeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Code), $"Code exceeded the maxCodeBytes limit of {MaxCodeBytes}.");
        }

        if (request.Args is not null)
        {
            var argsBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(request.Args));
            if (argsBytes > MaxArgsBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(request.Args), $"Args exceeded the maxArgsBytes limit of {MaxArgsBytes}.");
            }
        }
    }
}
