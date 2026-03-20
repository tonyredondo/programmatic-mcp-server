namespace ProgrammaticMcp.SampleServer;

public sealed class SampleWorkspace
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectState> _projects;
    private readonly Dictionary<string, TaskState> _tasks;

    private SampleWorkspace(Dictionary<string, ProjectState> projects, Dictionary<string, TaskState> tasks)
    {
        _projects = projects;
        _tasks = tasks;
    }

    public static SampleWorkspace CreateDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var projects = new[]
        {
            new ProjectState("project-alpha", "Alpha rollout"),
            new ProjectState("project-beta", "Beta hardening")
        }.ToDictionary(static item => item.ProjectId, StringComparer.Ordinal);

        var tasks = new[]
        {
            new TaskState("task-1", "project-alpha", "Prepare rollout checklist", "Confirm final rollout steps and owners.", "high", false, now.AddDays(-2), null),
            new TaskState("task-2", "project-alpha", "Review launch notes", "Validate launch notes and attach the latest approvals.", "medium", false, now.AddDays(-1), null),
            new TaskState("task-3", "project-beta", "Close security review", "Document the completed security review and attach the report.", "high", true, now.AddDays(-3), now.AddDays(-3)),
            new TaskState("task-4", "project-beta", "Triage regression report", "Review the latest regression report and assign follow-up work.", "medium", false, now.AddHours(-12), null)
        }.ToDictionary(static item => item.TaskId, StringComparer.Ordinal);

        return new SampleWorkspace(projects, tasks);
    }

    public ProjectsListResult ListProjects()
    {
        lock (_gate)
        {
            var projects = _projects.Values
                .OrderBy(static item => item.Name, StringComparer.Ordinal)
                .Select(
                    project => new ProjectSummary(
                        project.ProjectId,
                        project.Name,
                        _tasks.Values.Count(task => task.ProjectId == project.ProjectId && !task.IsCompleted)))
                .ToArray();

            return new ProjectsListResult(projects);
        }
    }

    public TasksListResult ListTasks(TasksListInput input)
    {
        lock (_gate)
        {
            var includeCompleted = input.IncludeCompleted ?? false;
            var tasks = _tasks.Values
                .Where(task => input.ProjectId is null || task.ProjectId == input.ProjectId)
                .Where(task => includeCompleted || !task.IsCompleted)
                .OrderBy(static task => task.ProjectId, StringComparer.Ordinal)
                .ThenBy(static task => task.Title, StringComparer.Ordinal)
                .Select(ToSummary)
                .ToArray();

            return new TasksListResult(tasks);
        }
    }

    public string? GetCurrentOpenTaskId()
    {
        lock (_gate)
        {
            return _tasks.Values
                .Where(task => !task.IsCompleted)
                .OrderBy(static task => task.ProjectId, StringComparer.Ordinal)
                .ThenBy(static task => task.Title, StringComparer.Ordinal)
                .Select(static task => task.TaskId)
                .FirstOrDefault();
        }
    }

    public TaskDetailsResult GetTaskById(TaskByIdInput input)
    {
        lock (_gate)
        {
            var task = GetTaskState(input.TaskId);
            var project = _projects[task.ProjectId];
            return ToDetails(task, project);
        }
    }

    public ExportedReportResult ExportReport(ExportReportInput input)
    {
        lock (_gate)
        {
            var project = GetProjectState(input.ProjectId);
            var tasks = _tasks.Values
                .Where(task => task.ProjectId == input.ProjectId)
                .OrderBy(static task => task.Title, StringComparer.Ordinal)
                .ToArray();

            var lines = new List<string>
            {
                $"# Task report for {project.Name}",
                string.Empty,
                $"Generated: {DateTimeOffset.UtcNow:O}",
                string.Empty
            };

            foreach (var task in tasks)
            {
                lines.Add($"- {task.TaskId}: {task.Title} ({GetStatus(task)})");
                lines.Add($"  Priority: {task.Priority}");
                lines.Add($"  Summary: {task.Description}");
            }

            lines.Add(string.Empty);
            lines.Add("## Notes");
            for (var i = 0; i < 24; i++)
            {
                lines.Add($"- Report section {i + 1}: keep large intermediate results in artifacts when they no longer fit inline.");
            }

            return new ExportedReportResult(
                project.ProjectId,
                project.Name,
                tasks.Length,
                DateTimeOffset.UtcNow,
                string.Join(Environment.NewLine, lines),
                tasks.Select(ToSummary).ToArray());
        }
    }

    public CompleteTaskPreview PreviewCompleteTask(CompleteTaskArgs input)
    {
        lock (_gate)
        {
            var task = GetTaskState(input.TaskId);
            var project = _projects[task.ProjectId];
            return new CompleteTaskPreview(
                task.TaskId,
                task.ProjectId,
                project.Name,
                task.Title,
                GetStatus(task),
                !task.IsCompleted,
                task.IsCompleted
                    ? "This task is already completed. Applying the mutation will return validation_failed."
                    : "Applying the mutation will mark the task as completed.");
        }
    }

    public MutationApplyResult<CompleteTaskApplyResult> ApplyCompleteTask(CompleteTaskArgs input)
    {
        lock (_gate)
        {
            var task = GetTaskState(input.TaskId);
            if (task.IsCompleted)
            {
                return MutationApplyResult<CompleteTaskApplyResult>.TerminalFailure(
                    "validation_failed",
                    $"Task '{input.TaskId}' is already completed.");
            }

            task.IsCompleted = true;
            task.CompletedAtUtc = DateTimeOffset.UtcNow;
            task.UpdatedAtUtc = task.CompletedAtUtc.Value;

            return MutationApplyResult<CompleteTaskApplyResult>.Success(
                new CompleteTaskApplyResult(
                    task.TaskId,
                    task.ProjectId,
                    "completed",
                    task.CompletedAtUtc.Value));
        }
    }

    private ProjectState GetProjectState(string projectId)
    {
        return _projects.TryGetValue(projectId, out var project)
            ? project
            : throw new InvalidOperationException($"Project '{projectId}' was not found.");
    }

    private TaskState GetTaskState(string taskId)
    {
        return _tasks.TryGetValue(taskId, out var task)
            ? task
            : throw new InvalidOperationException($"Task '{taskId}' was not found.");
    }

    private static TaskSummary ToSummary(TaskState task)
        => new(task.TaskId, task.ProjectId, task.Title, GetStatus(task));

    private static TaskDetailsResult ToDetails(TaskState task, ProjectState project)
        => new(
            task.TaskId,
            task.ProjectId,
            project.Name,
            task.Title,
            task.Description,
            GetStatus(task),
            task.Priority,
            task.UpdatedAtUtc,
            task.CompletedAtUtc);

    private static string GetStatus(TaskState task)
        => task.IsCompleted ? "completed" : "open";

    private sealed class ProjectState(string projectId, string name)
    {
        public string ProjectId { get; } = projectId;

        public string Name { get; } = name;
    }

    private sealed class TaskState(
        string taskId,
        string projectId,
        string title,
        string description,
        string priority,
        bool isCompleted,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        public string TaskId { get; } = taskId;

        public string ProjectId { get; } = projectId;

        public string Title { get; } = title;

        public string Description { get; } = description;

        public string Priority { get; } = priority;

        public bool IsCompleted { get; set; } = isCompleted;

        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;

        public DateTimeOffset? CompletedAtUtc { get; set; } = completedAtUtc;
    }
}
