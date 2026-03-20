# Sample Transcript

This transcript shows the intended end-to-end loop against `samples/ProgrammaticMcp.SampleServer`.

For the broader repository overview, see [overview.md](overview.md).

The sample uses the in-memory task domain with these stable ids:

- open task: `task-1`
- already completed task: `task-3`
- sampling task: `task-4`
- project with report data: `project-alpha`

## 1. Initialize

Send `initialize` to `/mcp`.

Expected outcome:

- the server advertises `/mcp/types`
- the server advertises read-only sample resources through `resources/list`
- the server instructions mention live sampling on the stateful transport
- the instructions mention caller binding requirements for mutation flows
- the sample enables signed-header fallback and also sets a caller-binding cookie for localhost-style cookie-capable clients

## 2. Inspect The Sample Resources

Call `resources/list`.

Expected outcome:

- the list includes `sample://workspace/guide`
- the list includes `sample://workspace/projects`

Then call `resources/read` for `sample://workspace/guide`.

Expected outcome:

- `contents[0].mimeType = "text/markdown"`
- `contents[0].text` contains `Sample Workspace Guide`

## 3. Discover The Capability Surface

Call `capabilities.search` with no query and `detailLevel = Full`.

Expected `apiPath` set:

- `projects.list`
- `tasks.list`
- `tasks.getById`
- `tasks.summarizeWithSampling`
- `tasks.exportReport`
- `tasks.complete`

## 4. Execute Read-Only Code

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

## 5. Run Live Sampling From JavaScript

Use a stateful MCP client that advertises sampling support, then call `code.execute` with:

```json
{
  "conversationId": "sample-js-sampling",
  "visibleApiPaths": [],
  "code": "async function main() { return await programmatic.client.sample({ messages: [{ role: 'user', text: 'Summarize task task-1 for the sample flow.' }], enableTools: true, allowedToolNames: ['tasks.readForSampling'] }); }"
}
```

Expected outcome:

- the connected client receives a sampling request
- the sampling loop can call `tasks.readForSampling`
- the final text mentions `task-1`
- the final text mentions that the task is `open`

## 6. Run Capability-Handler Sampling

Use the same stateful MCP client and call `code.execute` with:

```json
{
  "conversationId": "sample-handler-sampling",
  "visibleApiPaths": ["tasks.summarizeWithSampling"],
  "code": "async function main() { return await programmatic.tasks.summarizeWithSampling({ taskId: 'task-4' }); }"
}
```

Expected outcome:

- the capability handler resolves `GetSamplingClient(...)`
- the handler uses the same `tasks.readForSampling` sampling tool
- `result.taskId` is `task-4`
- `result.summary` mentions that the task is `open`

## 7. Spill A Large Report To An Artifact

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

## 8. Preview And Apply A Successful Mutation

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

Keep the `approvalNonce` from the preview response. `mutation.list` does not return it.

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

## 9. Show The Rejected Mutation Path

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

## 10. Cancel A Pending Mutation

Preview the mutation:

```json
{
  "conversationId": "sample-cancel",
  "code": "async function main() { return await programmatic.tasks.complete({ taskId: 'task-1' }); }"
}
```

Then call `mutation.cancel` with the returned `approvalId` and `approvalNonce`.

Expected outcome:

- `status = "cancelled"`
- a follow-up `mutation.list` for `sample-cancel` returns an empty list

## Health And Browser Tooling

- health checks are available at `/mcp/health`
- the sample demonstrates MCP resources, stateful live sampling, artifacts, and mutation flows together
- stateful MCP clients use normal session identity; raw HTTP mutation and reconnect flows still rely on caller binding
- browser-style CORS is off by default
- the sample only enables localhost-oriented CORS when `SampleServer:Cors:EnableBrowserTooling = true`
