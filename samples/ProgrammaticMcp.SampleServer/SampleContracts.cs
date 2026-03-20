namespace ProgrammaticMcp.SampleServer;

public sealed record ProjectsListInput;

public sealed record ProjectSummary(string ProjectId, string Name, int OpenTaskCount);

public sealed record ProjectsListResult(IReadOnlyList<ProjectSummary> Projects);

public sealed record TasksListInput(string? ProjectId = null, bool? IncludeCompleted = null);

public sealed record TaskSummary(string TaskId, string ProjectId, string Title, string Status);

public sealed record TasksListResult(IReadOnlyList<TaskSummary> Tasks);

public sealed record TaskByIdInput(string TaskId);

public sealed record TaskDetailsResult(
    string TaskId,
    string ProjectId,
    string ProjectName,
    string Title,
    string Description,
    string Status,
    string Priority,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record TaskSamplingSummaryResult(
    string TaskId,
    string Summary);

public sealed record ExportReportInput(string ProjectId);

public sealed record ExportedReportResult(
    string ProjectId,
    string ProjectName,
    int TaskCount,
    DateTimeOffset GeneratedAtUtc,
    string Markdown,
    IReadOnlyList<TaskSummary> Tasks);

public sealed record CompleteTaskArgs(string TaskId);

public sealed record CompleteTaskPreview(
    string TaskId,
    string ProjectId,
    string ProjectName,
    string Title,
    string CurrentStatus,
    bool WillComplete,
    string Note);

public sealed record CompleteTaskApplyResult(
    string TaskId,
    string ProjectId,
    string NewStatus,
    DateTimeOffset CompletedAtUtc);
