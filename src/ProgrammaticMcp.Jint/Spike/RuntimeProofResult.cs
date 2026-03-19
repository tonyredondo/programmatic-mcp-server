namespace ProgrammaticMcp.Jint.Spike;

/// <summary>
/// Represents the outcome of a runtime proof execution.
/// </summary>
/// <param name="Succeeded">Indicates whether the proof execution completed successfully.</param>
/// <param name="Value">The returned value when execution succeeds.</param>
/// <param name="FailureCode">The structured failure code, when execution fails.</param>
/// <param name="Message">The human-readable failure message, when execution fails.</param>
/// <param name="Line">The line number associated with a syntax failure, when available.</param>
/// <param name="Column">The column number associated with a syntax failure, when available.</param>
/// <param name="MaxObservedHostConcurrency">The highest concurrency observed for host calls.</param>
public sealed record RuntimeProofResult(
    bool Succeeded,
    object? Value,
    string? FailureCode,
    string? Message,
    int? Line,
    int? Column,
    int MaxObservedHostConcurrency);
