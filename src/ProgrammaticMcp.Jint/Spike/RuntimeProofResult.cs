namespace ProgrammaticMcp.Jint.Spike;

public sealed record RuntimeProofResult(
    bool Succeeded,
    object? Value,
    string? FailureCode,
    string? Message,
    int? Line,
    int? Column,
    int MaxObservedHostConcurrency);
