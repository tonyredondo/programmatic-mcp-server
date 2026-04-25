using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

/// <summary>
/// Request used when persisting an artifact to the built-in store.
/// </summary>
public sealed record ArtifactWriteRequest(
    string ArtifactId,
    string ConversationId,
    string CallerBindingId,
    string Kind,
    string Name,
    string MimeType,
    string Content,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Request used when reading an artifact from the built-in store.
/// </summary>
public sealed record ArtifactReadRequest(
    string ArtifactId,
    string ConversationId,
    string CallerBindingId,
    string? Cursor = null,
    int? Limit = null);

/// <summary>
/// A single artifact chunk returned from storage.
/// </summary>
public sealed record ArtifactChunk(int Index, string Content, int Bytes);

/// <summary>
/// Result returned when reading an artifact from storage.
/// </summary>
public sealed record ArtifactReadResult(
    bool Found,
    string? ArtifactId,
    string? Kind,
    string? Name,
    string? MimeType,
    IReadOnlyList<ArtifactChunk> Items,
    string? NextCursor,
    int? TotalChunks,
    int? TotalBytes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Retention and chunking settings for the built-in artifact store.
/// </summary>
public sealed record ArtifactRetentionOptions(
    int ArtifactTtlSeconds,
    int MaxArtifactBytesPerArtifact,
    int MaxArtifactsPerConversation,
    int MaxArtifactBytesPerConversation,
    int MaxArtifactBytesGlobal,
    int ArtifactChunkBytes);

/// <summary>
/// Writes execution artifacts through the current host binding.
/// </summary>
public interface IArtifactWriter
{
    /// <summary>
    /// Writes a JSON artifact.
    /// </summary>
    ValueTask<ExecutionArtifactDescriptor> WriteJsonArtifactAsync(string name, JsonNode payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a text artifact.
    /// </summary>
    ValueTask<ExecutionArtifactDescriptor> WriteTextArtifactAsync(string name, string content, string mimeType, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores and retrieves execution artifacts.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Writes an artifact record.
    /// </summary>
    ValueTask WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an artifact record.
    /// </summary>
    ValueTask<ArtifactReadResult> ReadAsync(ArtifactReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes expired artifacts.
    /// </summary>
    ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Base helper for custom artifact stores.
/// </summary>
public abstract class ArtifactStoreBase : IArtifactStore
{
    /// <inheritdoc />
    public virtual ValueTask WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WriteCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<ArtifactReadResult> ReadAsync(ArtifactReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadCoreAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SweepExpiredCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Writes an artifact record.
    /// </summary>
    protected abstract ValueTask WriteCoreAsync(ArtifactWriteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an artifact record.
    /// </summary>
    protected abstract ValueTask<ArtifactReadResult> ReadCoreAsync(ArtifactReadRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes expired artifacts.
    /// </summary>
    protected abstract ValueTask SweepExpiredCoreAsync(CancellationToken cancellationToken);
}

/// <summary>
/// In-memory artifact store used by the default implementation.
/// </summary>
public sealed class InMemoryArtifactStore : ArtifactStoreBase
{
    private readonly ConcurrentDictionary<string, StoredArtifact> _entries = new(StringComparer.Ordinal);
    private readonly ArtifactRetentionOptions _options;
    private readonly object _gate = new();

    /// <summary>
    /// Creates a new in-memory artifact store.
    /// </summary>
    public InMemoryArtifactStore(ArtifactRetentionOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(ArtifactWriteRequest request, CancellationToken cancellationToken)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(request.Content);
        if (totalBytes > _options.MaxArtifactBytesPerArtifact)
        {
            throw new InvalidOperationException(
                $"Artifact '{request.ArtifactId}' exceeds the per-artifact limit of {_options.MaxArtifactBytesPerArtifact} bytes.");
        }

        lock (_gate)
        {
            CleanupExpired(DateTimeOffset.UtcNow);

            var existingConversationEntries = _entries.Values
                .Where(entry =>
                    entry.ConversationId == request.ConversationId
                    && entry.CallerBindingId == request.CallerBindingId)
                .ToArray();

            if (existingConversationEntries.Length >= _options.MaxArtifactsPerConversation)
            {
                throw new InvalidOperationException(
                    $"Conversation '{request.ConversationId}' exceeded the artifact count limit of {_options.MaxArtifactsPerConversation}.");
            }

            var conversationBytes = existingConversationEntries.Sum(static entry => entry.TotalBytes);
            if (conversationBytes + totalBytes > _options.MaxArtifactBytesPerConversation)
            {
                throw new InvalidOperationException(
                    $"Conversation '{request.ConversationId}' exceeded the artifact byte limit of {_options.MaxArtifactBytesPerConversation}.");
            }

            var globalBytes = _entries.Values.Sum(static entry => entry.TotalBytes);
            if (globalBytes + totalBytes > _options.MaxArtifactBytesGlobal)
            {
                throw new InvalidOperationException(
                    $"Artifact storage exceeded the global byte limit of {_options.MaxArtifactBytesGlobal}.");
            }

            if (!_entries.TryAdd(request.ArtifactId, new StoredArtifact(request, totalBytes)))
            {
                throw new InvalidOperationException($"Artifact '{request.ArtifactId}' already exists.");
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask<ArtifactReadResult> ReadCoreAsync(ArtifactReadRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CleanupExpired(DateTimeOffset.UtcNow);

            if (!_entries.TryGetValue(request.ArtifactId, out var entry)
                || entry.ConversationId != request.ConversationId
                || entry.CallerBindingId != request.CallerBindingId)
            {
                return ValueTask.FromResult(new ArtifactReadResult(false, null, null, null, null, Array.Empty<ArtifactChunk>(), null, null, null, null));
            }

            var chunks = Chunk(entry.Content, _options.ArtifactChunkBytes);
            var offset = ArtifactReadCursorCodec.Parse(
                request.Cursor,
                request.ArtifactId,
                request.ConversationId,
                request.CallerBindingId,
                entry.ExpiresAt);
            var limit = request.Limit ?? chunks.Count;
            if (limit <= 0)
            {
                throw new InvalidOperationException("Artifact read limit must be positive.");
            }

            var page = chunks.Skip(offset).Take(limit).ToArray();
            var nextCursor = offset + page.Length < chunks.Count
                ? ArtifactReadCursorCodec.Create(
                    offset + page.Length,
                    request.ArtifactId,
                    request.ConversationId,
                    request.CallerBindingId,
                    entry.ExpiresAt)
                : null;

            return ValueTask.FromResult(
                new ArtifactReadResult(
                    true,
                    entry.ArtifactId,
                    entry.Kind,
                    entry.Name,
                    entry.MimeType,
                    page,
                    nextCursor,
                    chunks.Count,
                    entry.TotalBytes,
                    entry.ExpiresAt));
        }
    }

    /// <inheritdoc />
    protected override ValueTask SweepExpiredCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CleanupExpired(DateTimeOffset.UtcNow);
        }

        return ValueTask.CompletedTask;
    }

    private void CleanupExpired(DateTimeOffset utcNow)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= utcNow)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static IReadOnlyList<ArtifactChunk> Chunk(string content, int chunkBytes)
    {
        if (content.Length == 0)
        {
            return Array.Empty<ArtifactChunk>();
        }

        var chunks = new List<ArtifactChunk>();
        var builder = new StringBuilder();
        var builderBytes = 0;
        var index = 0;

        foreach (var rune in content.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeText);
            if (builderBytes > 0 && builderBytes + runeBytes > chunkBytes)
            {
                var chunkText = builder.ToString();
                chunks.Add(new ArtifactChunk(index++, chunkText, Encoding.UTF8.GetByteCount(chunkText)));
                builder.Clear();
                builderBytes = 0;
            }

            builder.Append(runeText);
            builderBytes += runeBytes;
        }

        if (builderBytes > 0)
        {
            var chunkText = builder.ToString();
            chunks.Add(new ArtifactChunk(index, chunkText, Encoding.UTF8.GetByteCount(chunkText)));
        }

        return chunks;
    }

    private sealed record StoredArtifact(
        string ArtifactId,
        string ConversationId,
        string CallerBindingId,
        string Kind,
        string Name,
        string MimeType,
        string Content,
        int TotalBytes,
        DateTimeOffset ExpiresAt)
    {
        public StoredArtifact(ArtifactWriteRequest request, int totalBytes)
            : this(
                request.ArtifactId,
                request.ConversationId,
                request.CallerBindingId,
                request.Kind,
                request.Name,
                request.MimeType,
                request.Content,
                totalBytes,
                request.ExpiresAt)
        {
        }
    }
}

/// <summary>
/// Stored approval record used by the mutation approval flow.
/// </summary>
public sealed record PendingApproval(
    string ApprovalId,
    string ApprovalNonce,
    string MutationName,
    JsonObject Args,
    string ActionArgsHash,
    MutationPreviewEnvelope PreviewEnvelope,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string ConversationId,
    string CallerBindingId,
    ApprovalState State,
    DateTimeOffset? ApplyingSinceUtc,
    string? FailureCode);

/// <summary>
/// Request used to create a mutation preview and pending approval.
/// </summary>
public sealed record MutationPreviewRequest(
    string MutationName,
    string ConversationId,
    string CallerBindingId,
    JsonObject Args,
    IServiceProvider Services,
    object? Principal);

/// <summary>
/// Result returned when a mutation preview is created.
/// </summary>
public sealed record MutationPreviewResult(
    MutationPreviewEnvelope PreviewEnvelope,
    PendingApproval PendingApproval);

/// <summary>
/// Decision used to apply or cancel a previously issued approval.
/// </summary>
public sealed record ApprovalDecision(
    string ConversationId,
    string CallerBindingId,
    string ApprovalId,
    string ApprovalNonce,
    object? Principal);

/// <summary>
/// Result of an approval state transition attempt.
/// </summary>
public sealed record ApprovalTransitionResult(
    ApprovalTransitionStatus Status,
    PendingApproval? Approval);

/// <summary>
/// Stores and manages pending approvals.
/// </summary>
public interface IApprovalStore
{
    /// <summary>
    /// Creates a new approval record.
    /// </summary>
    ValueTask CreateAsync(PendingApproval approval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an approval record by identifier.
    /// </summary>
    ValueTask<PendingApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists pending approvals for a conversation and caller binding.
    /// </summary>
    ValueTask<IReadOnlyList<PendingApproval>> ListPendingAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to transition an approval from an expected state.
    /// </summary>
    ValueTask<ApprovalTransitionResult> TryTransitionAsync(
        string approvalId,
        ApprovalState expectedState,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every approval currently stored.
    /// </summary>
    ValueTask<IReadOnlyList<PendingApproval>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes expired approvals.
    /// </summary>
    ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Previews a mutation and creates a pending approval record.
/// </summary>
public interface IMutationPreviewer
{
    /// <summary>
    /// Creates a mutation preview.
    /// </summary>
    ValueTask<MutationPreviewResult> PreviewAsync(MutationPreviewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies or cancels a stored approval.
/// </summary>
public interface IMutationExecutor
{
    /// <summary>
    /// Applies a stored approval.
    /// </summary>
    ValueTask<MutationApplyResponse> ApplyAsync(ApprovalDecision decision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a stored approval.
    /// </summary>
    ValueTask<MutationCancelResponse> CancelAsync(ApprovalDecision decision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base helper for custom approval stores.
/// </summary>
public abstract class ApprovalStoreBase : IApprovalStore
{
    /// <inheritdoc />
    public virtual ValueTask CreateAsync(PendingApproval approval, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateCoreAsync(approval, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<PendingApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetCoreAsync(approvalId, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<PendingApproval>> ListPendingAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ListPendingCoreAsync(conversationId, callerBindingId, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<ApprovalTransitionResult> TryTransitionAsync(
        string approvalId,
        ApprovalState expectedState,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryTransitionCoreAsync(approvalId, expectedState, transition, cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<PendingApproval>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ListAllCoreAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SweepExpiredCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a new approval record.
    /// </summary>
    protected abstract ValueTask CreateCoreAsync(PendingApproval approval, CancellationToken cancellationToken);

    /// <summary>
    /// Gets an approval record by identifier.
    /// </summary>
    protected abstract ValueTask<PendingApproval?> GetCoreAsync(string approvalId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists pending approvals for a conversation and caller binding.
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<PendingApproval>> ListPendingCoreAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to transition an approval from an expected state.
    /// </summary>
    protected abstract ValueTask<ApprovalTransitionResult> TryTransitionCoreAsync(
        string approvalId,
        ApprovalState expectedState,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every approval currently stored.
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<PendingApproval>> ListAllCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes expired approvals.
    /// </summary>
    protected abstract ValueTask SweepExpiredCoreAsync(CancellationToken cancellationToken);
}

/// <summary>
/// In-memory approval store used by the default implementation.
/// </summary>
public sealed class InMemoryApprovalStore : ApprovalStoreBase
{
    private readonly ConcurrentDictionary<string, ApprovalEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override ValueTask CreateCoreAsync(PendingApproval approval, CancellationToken cancellationToken)
    {
        if (!_entries.TryAdd(approval.ApprovalId, new ApprovalEntry(CloneApproval(approval))))
        {
            throw new InvalidOperationException($"Approval '{approval.ApprovalId}' already exists.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask<PendingApproval?> GetCoreAsync(string approvalId, CancellationToken cancellationToken)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.FromResult(_entries.TryGetValue(approvalId, out var entry) ? CloneApproval(entry.Current) : null);
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyList<PendingApproval>> ListPendingCoreAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        var results = _entries.Values
            .Select(static entry => entry.Current)
            .Where(approval =>
                approval.State == ApprovalState.Pending
                && approval.ConversationId == conversationId
                && approval.CallerBindingId == callerBindingId)
            .OrderBy(static approval => approval.CreatedAt)
            .Select(CloneApproval)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<PendingApproval>>(results);
    }

    /// <inheritdoc />
    protected override ValueTask<IReadOnlyList<PendingApproval>> ListAllCoreAsync(CancellationToken cancellationToken)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.FromResult<IReadOnlyList<PendingApproval>>(_entries.Values.Select(static entry => entry.Current).OrderBy(static approval => approval.CreatedAt).Select(CloneApproval).ToArray());
    }

    /// <inheritdoc />
    protected override async ValueTask<ApprovalTransitionResult> TryTransitionCoreAsync(
        string approvalId,
        ApprovalState expectedState,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken = default)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        if (!_entries.TryGetValue(approvalId, out var entry))
        {
            return new ApprovalTransitionResult(ApprovalTransitionStatus.NotFound, null);
        }

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            if (entry.Current.State != expectedState)
            {
                return new ApprovalTransitionResult(ApprovalTransitionStatus.UnexpectedState, CloneApproval(entry.Current));
            }

            entry.Current = CloneApproval(transition(CloneApproval(entry.Current)));
            return new ApprovalTransitionResult(ApprovalTransitionStatus.Success, CloneApproval(entry.Current));
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <inheritdoc />
    protected override ValueTask SweepExpiredCoreAsync(CancellationToken cancellationToken)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Recovers approvals that have been stuck in the applying state for too long.
    /// </summary>
    public async ValueTask<int> RecoverStaleApplyingAsync(
        TimeSpan staleThreshold,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpired(DateTimeOffset.UtcNow);

        var cutoff = DateTimeOffset.UtcNow - staleThreshold;
        var recovered = 0;

        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            await entry.Lock.WaitAsync(cancellationToken);
            try
            {
                if (entry.Current.State == ApprovalState.Applying
                    && entry.Current.ApplyingSinceUtc is { } applyingSince
                    && applyingSince <= cutoff)
                {
                    entry.Current = PreserveRecoveredApprovalExpiry(transition(CloneApproval(entry.Current)), DateTimeOffset.UtcNow);
                    recovered++;
                }
            }
            finally
            {
                entry.Lock.Release();
            }
        }

        return recovered;
    }

    private void CleanupExpired(DateTimeOffset utcNow)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.Current.State != ApprovalState.Applying && pair.Value.Current.ExpiresAt <= utcNow)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// Creates an approval copy whose mutable JSON payloads cannot mutate the stored record by reference.
    /// </summary>
    private static PendingApproval CloneApproval(PendingApproval approval)
        => approval with
        {
            Args = approval.Args.DeepClone().AsObject(),
            PreviewEnvelope = ClonePreviewEnvelope(approval.PreviewEnvelope)
        };

    /// <summary>
    /// Creates a mutation preview envelope copy with detached mutable JSON payloads.
    /// </summary>
    private static MutationPreviewEnvelope ClonePreviewEnvelope(MutationPreviewEnvelope envelope)
        => envelope with
        {
            Args = envelope.Args.DeepClone().AsObject(),
            Preview = envelope.Preview?.DeepClone()
        };

    /// <summary>
    /// Keeps recovered approvals observable when their original TTL expired before stale-apply recovery ran.
    /// </summary>
    private static PendingApproval PreserveRecoveredApprovalExpiry(PendingApproval approval, DateTimeOffset utcNow)
    {
        if (approval.ExpiresAt > utcNow)
        {
            return approval;
        }

        var originalTtl = approval.ExpiresAt - approval.CreatedAt;
        var recoveredTtl = originalTtl > TimeSpan.Zero ? originalTtl : TimeSpan.FromMinutes(10);
        return approval with { ExpiresAt = utcNow.Add(recoveredTtl) };
    }

    private sealed class ApprovalEntry
    {
        public ApprovalEntry(PendingApproval current)
        {
            Current = current;
        }

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public PendingApproval Current { get; set; }
    }
}

/// <summary>
/// Generates approval identifiers and nonces.
/// </summary>
public static class ApprovalTokenGenerator
{
    /// <summary>
    /// Generates a new approval identifier.
    /// </summary>
    public static string GenerateApprovalId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    /// <summary>
    /// Generates a new approval nonce.
    /// </summary>
    public static string GenerateApprovalNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// Inputs used when resolving a caller binding.
/// </summary>
public sealed record CallerBindingContext(string? PrincipalIdentity, string? SessionIdentity, string? TransportFallbackIdentity);

/// <summary>
/// Resolves caller binding identifiers from transport and principal inputs.
/// </summary>
public interface ICallerBindingAccessor
{
    /// <summary>
    /// Resolves a stable caller binding identifier.
    /// </summary>
    ValueTask<string?> GetCallerBindingIdAsync(CallerBindingContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Strategy interface for caller binding resolution.
/// </summary>
public interface ICallerBindingStrategy
{
    /// <summary>
    /// Resolves a stable caller binding identifier.
    /// </summary>
    ValueTask<string?> ResolveAsync(CallerBindingContext context, CancellationToken cancellationToken = default);
}

internal static class ArtifactReadCursorCodec
{
    public static string Create(int offset, string artifactId, string conversationId, string callerBindingId, DateTimeOffset expiresAt)
    {
        var payload = new JsonObject
        {
            ["artifactId"] = artifactId,
            ["conversationId"] = conversationId,
            ["callerBindingId"] = callerBindingId,
            ["expiresAt"] = expiresAt.ToString("O"),
            ["offset"] = offset
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    public static int Parse(string? cursor, string artifactId, string conversationId, string callerBindingId, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))!.AsObject();
            if (!string.Equals(payload["artifactId"]?.GetValue<string>(), artifactId, StringComparison.Ordinal)
                || !string.Equals(payload["conversationId"]?.GetValue<string>(), conversationId, StringComparison.Ordinal)
                || !string.Equals(payload["callerBindingId"]?.GetValue<string>(), callerBindingId, StringComparison.Ordinal)
                || !string.Equals(payload["expiresAt"]?.GetValue<string>(), expiresAt.ToString("O"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Artifact cursor does not match the requested artifact.");
            }

            var offset = payload["offset"]?.GetValue<int>() ?? 0;
            if (offset < 0)
            {
                throw new InvalidOperationException("Artifact cursor offset is invalid.");
            }

            return offset;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("Artifact cursor is invalid.", exception);
        }
    }
}
