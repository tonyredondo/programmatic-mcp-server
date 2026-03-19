# Sample Transcript

This transcript shows the intended end-to-end loop against `samples/ProgrammaticMcp.SampleServer`.

The sample uses the in-memory task domain with these stable ids:

- open task: `task-1`
- already completed task: `task-3`
- project with report data: `project-alpha`

## 1. Initialize

Send `initialize` to `/mcp`.

Expected outcome:

- the server advertises `/mcp/types`
- the instructions mention caller binding requirements for mutation flows
- the sample sets a caller-binding cookie on localhost development runs

## 2. Discover The Capability Surface

Call `capabilities.search` with no query and `detailLevel = Full`.

Expected `apiPath` set:

- `projects.list`
- `tasks.list`
- `tasks.getById`
- `tasks.exportReport`
- `tasks.complete`

## 3. Execute Read-Only Code

Call `code.execute` with:

```json
{
  "conversationId": "sample-read",
  "code": "async function main() { return { projects: await programmatic.projects.list({}), tasks: await programmatic.tasks.list({ projectId: 'project-alpha' }), task: await programmatic.tasks.getById({ taskId: 'task-1' }) }; }"
}
```

Expected outcome:

- `result.projects.projects` contains both sample projects
- `result.tasks.tasks` contains the `project-alpha` tasks
- `result.task.taskId` is `task-1`

## 4. Spill A Large Report To An Artifact

Call `code.execute` with:

```json
{
  "conversationId": "sample-report",
  "maxResultBytes": 256,
  "code": "async function main() { return await programmatic.tasks.exportReport({ projectId: 'project-alpha' }); }"
}
```

Expected outcome:

- `result` is `null`
- `resultArtifactId` is present
- diagnostics include `result_spilled_to_artifact`

Then call `artifact.read`:

```json
{
  "conversationId": "sample-report",
  "artifactId": "<resultArtifactId>",
  "limit": 1
}
```

Expected outcome:

- `found = true`
- `kind = "execution.result"`
- `items[0].text` contains the report payload

## 5. Preview And Apply A Successful Mutation

Preview:

```json
{
  "conversationId": "sample-complete",
  "code": "async function main() { return await programmatic.tasks.complete({ taskId: 'task-1' }); }"
}
```

Expected outcome:

- `approvalsRequested[0].mutationName = "tasks.complete"`
- `approvalsRequested[0].preview.currentStatus = "open"`

List:

```json
{
  "conversationId": "sample-complete"
}
```

Call `mutation.list` and expect the same approval id to be present.

Apply:

```json
{
  "conversationId": "sample-complete",
  "approvalId": "<approvalId>",
  "approvalNonce": "<approvalNonce>"
}
```

Expected `mutation.apply` outcome:

- `status = "completed"`
- `result.newStatus = "completed"`

## 6. Show The Rejected Mutation Path

Preview the already completed task:

```json
{
  "conversationId": "sample-failure",
  "code": "async function main() { return await programmatic.tasks.complete({ taskId: 'task-3' }); }"
}
```

Expected outcome:

- the preview note says the task is already completed

Apply the preview:

```json
{
  "conversationId": "sample-failure",
  "approvalId": "<approvalId>",
  "approvalNonce": "<approvalNonce>"
}
```

Expected `mutation.apply` outcome:

- `status = "failed"`
- `failureCode = "validation_failed"`
- `message` explains that `task-3` is already completed

## Health And Browser Tooling

- health checks are available at `/mcp/health`
- browser-style CORS is off by default
- the sample only enables localhost-oriented CORS when `SampleServer:Cors:EnableBrowserTooling = true`
