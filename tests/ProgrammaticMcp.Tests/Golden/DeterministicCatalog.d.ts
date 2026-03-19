declare namespace programmatic {
  type ProjectsListInput = { includeArchived: boolean };
  type ProjectsListResult = { projects: string[] };
  type TasksCompleteInput = { taskId: string };
  type TasksCompleteResult = { actionArgsHash: string; approvalId: string; approvalNonce: string; args: { taskId: string }; expiresAt: string; kind: "mutation_preview"; mutationName: string; preview: { taskId: string; willComplete: boolean }; summary: string };
  type TasksCompletePreviewPayload = { taskId: string; willComplete: boolean };
  type TasksCompleteApplyResult = { status: string; taskId: string };
  namespace projects { }
  namespace projects {
    function list(input: ProjectsListInput): Promise<ProjectsListResult>;
  }
  namespace tasks { }
  namespace tasks {
    function complete(input: TasksCompleteInput): Promise<TasksCompleteResult>;
  }
}
