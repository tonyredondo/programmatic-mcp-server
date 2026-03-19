using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ProgrammaticMcp;

public sealed record ArtifactWriteRequest(
    string ArtifactId,
    string ConversationId,
    string CallerBindingId,
    string Kind,
    string Name,
    string MimeType,
    string Content,
    DateTimeOffset ExpiresAt);

public sealed record ArtifactReadRequest(string ArtifactId, string ConversationId, string CallerBindingId);

public sealed record ArtifactChunk(int Index, string Content, int Bytes);

public sealed record ArtifactReadResult(
    bool Found,
    string? ArtifactId,
    string? Kind,
    string? Name,
    string? MimeType,
    IReadOnlyList<ArtifactChunk> Items,
    int? TotalChunks,
    int? TotalBytes,
    DateTimeOffset? ExpiresAt);

public sealed record ArtifactRetentionOptions(
    int ArtifactTtlSeconds,
    int MaxArtifactBytesPerArtifact,
    int MaxArtifactsPerConversation,
    int MaxArtifactBytesPerConversation,
    int MaxArtifactBytesGlobal,
    int ArtifactChunkBytes);

public interface IArtifactWriter
{
    ValueTask<ExecutionArtifactDescriptor> WriteJsonArtifactAsync(string name, JsonNode payload, CancellationToken cancellationToken = default);

    ValueTask<ExecutionArtifactDescriptor> WriteTextArtifactAsync(string name, string content, string mimeType, CancellationToken cancellationToken = default);
}

public interface IArtifactStore
{
    ValueTask WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken = default);

    ValueTask<ArtifactReadResult> ReadAsync(ArtifactReadRequest request, CancellationToken cancellationToken = default);

    ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<string, StoredArtifact> _entries = new(StringComparer.Ordinal);
    private readonly ArtifactRetentionOptions _options;
    private readonly object _gate = new();

    public InMemoryArtifactStore(ArtifactRetentionOptions options)
    {
        _options = options;
    }

    public ValueTask WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

    public ValueTask<ArtifactReadResult> ReadAsync(ArtifactReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            CleanupExpired(DateTimeOffset.UtcNow);

            if (!_entries.TryGetValue(request.ArtifactId, out var entry)
                || entry.ConversationId != request.ConversationId
                || entry.CallerBindingId != request.CallerBindingId)
            {
                return ValueTask.FromResult(new ArtifactReadResult(false, null, null, null, null, Array.Empty<ArtifactChunk>(), null, null, null));
            }

            var chunks = Chunk(entry.Content, _options.ArtifactChunkBytes);
            return ValueTask.FromResult(
                new ArtifactReadResult(
                    true,
                    entry.ArtifactId,
                    entry.Kind,
                    entry.Name,
                    entry.MimeType,
                    chunks,
                    chunks.Count,
                    entry.TotalBytes,
                    entry.ExpiresAt));
        }
    }

    public ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

public sealed record ApprovalTransitionResult(
    ApprovalTransitionStatus Status,
    PendingApproval? Approval);

public interface IApprovalStore
{
    ValueTask CreateAsync(PendingApproval approval, CancellationToken cancellationToken = default);

    ValueTask<PendingApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PendingApproval>> ListPendingAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken = default);

    ValueTask<ApprovalTransitionResult> TryTransitionAsync(
        string approvalId,
        ApprovalState expectedState,
        Func<PendingApproval, PendingApproval> transition,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PendingApproval>> ListAllAsync(CancellationToken cancellationToken = default);

    ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalEntry> _entries = new(StringComparer.Ordinal);

    public ValueTask CreateAsync(PendingApproval approval, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryAdd(approval.ApprovalId, new ApprovalEntry(approval)))
        {
            throw new InvalidOperationException($"Approval '{approval.ApprovalId}' already exists.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PendingApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.FromResult(_entries.TryGetValue(approvalId, out var entry) ? entry.Current : null);
    }

    public ValueTask<IReadOnlyList<PendingApproval>> ListPendingAsync(string conversationId, string callerBindingId, CancellationToken cancellationToken = default)
    {
        CleanupExpired(DateTimeOffset.UtcNow);
        var results = _entries.Values
            .Select(static entry => entry.Current)
            .Where(approval =>
                approval.State == ApprovalState.Pending
                && approval.ConversationId == conversationId
                && approval.CallerBindingId == callerBindingId)
            .OrderBy(static approval => approval.CreatedAt)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<PendingApproval>>(results);
    }

    public ValueTask<IReadOnlyList<PendingApproval>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.FromResult<IReadOnlyList<PendingApproval>>(_entries.Values.Select(static entry => entry.Current).OrderBy(static approval => approval.CreatedAt).ToArray());
    }

    public async ValueTask<ApprovalTransitionResult> TryTransitionAsync(
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
                return new ApprovalTransitionResult(ApprovalTransitionStatus.UnexpectedState, entry.Current);
            }

            entry.Current = transition(entry.Current);
            return new ApprovalTransitionResult(ApprovalTransitionStatus.Success, entry.Current);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public ValueTask SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpired(DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

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
                    entry.Current = transition(entry.Current);
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
            if (pair.Value.Current.ExpiresAt <= utcNow)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
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

public static class ApprovalTokenGenerator
{
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

    public static string GenerateApprovalNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record CallerBindingContext(string? PrincipalIdentity, string? SessionIdentity, string? TransportFallbackIdentity);

public interface ICallerBindingAccessor
{
    ValueTask<string?> GetCallerBindingIdAsync(CallerBindingContext context, CancellationToken cancellationToken = default);
}

public interface ICallerBindingStrategy
{
    ValueTask<string?> ResolveAsync(CallerBindingContext context, CancellationToken cancellationToken = default);
}
