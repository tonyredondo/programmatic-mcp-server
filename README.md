# Programmatic MCP Server

`Programmatic MCP Server` is a .NET library for building MCP servers that are meant to be used through generated code, not only through direct tool calls.

The idea is inspired by:

- [Cloudflare: Code Mode](https://blog.cloudflare.com/code-mode/)
- [Anthropic: Code execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp)

## What This Repo Is Trying To Build

A programmatic MCP server gives an agent a workflow like this:

1. Discover a focused set of capabilities instead of loading a large tool surface blindly.
2. Generate code against a predictable API surface.
3. Run that code in a constrained runtime.
4. Spill large outputs into artifacts instead of stuffing them into model context.
5. Use approval-based mutation flows for writes.

In this model, MCP is still the integration protocol, but the main user experience is code-first rather than tool-call-first.

## Current V0 Shape

The current v0 direction is:

- .NET library-first, not a product-specific server
- C# as the implementation language
- .NET 8 and .NET 10 targets
- `modelcontextprotocol/csharp-sdk` as the MCP foundation
- `Jint` as the initial execution runtime
- ASP.NET Core as the first supported integration layer

The plan is for customers to get the main pieces already wired together:

- capability registration and discovery
- generated JavaScript and TypeScript-facing API surface
- constrained JavaScript execution
- capability search
- artifact storage and paged reads
- preview/list/apply/cancel mutation approval flow
- caller binding for HTTP clients
- ASP.NET Core integration on top of the C# MCP SDK

## Package Layout

The implementation plan is organized around three library packages and one sample:

- `ProgrammaticMcp`
  Core abstractions, capability metadata, schema generation, hashing, approvals, artifacts, and shared contracts.
- `ProgrammaticMcp.Jint`
  The Jint-backed execution runtime, generated namespace bootstrap, bridge logic, and runtime diagnostics.
- `ProgrammaticMcp.AspNetCore`
  ASP.NET Core and C# MCP SDK integration, tool exposure, caller binding, HTTP-specific behavior, and `/types`.
- `samples/ProgrammaticMcp.SampleServer`
  A small reference server used to prove the library design end to end.

## Important V0 Constraints

The current plan is intentionally narrow:

- the built-in Jint runtime is a constrained convenience sandbox, not a hostile multi-tenant isolation boundary
- the first transport is HTTP/ASP.NET Core, not every MCP transport
- the built-in reconnect and storage story is only guaranteed for single-process or sticky-session deployments
- built-in artifact and approval stores are in-memory by default
- authn/authz policy remains host-driven; the library requires explicit authorization decisions for mutation flows

## Running The Sample Server

The repository includes a runnable reference server at `samples/ProgrammaticMcp.SampleServer`.

Start it with:

```bash
dotnet run --project samples/ProgrammaticMcp.SampleServer
```

The sample exposes:

- `GET /`
- `POST /mcp`
- `GET /mcp/types`
- `GET /mcp/health`

The sample domain is intentionally small and in-memory. It exposes these exact capabilities:

- `projects.list`
- `tasks.list`
- `tasks.getById`
- `tasks.exportReport`
- `tasks.complete`

## Sample Flow

The full transcript lives in [docs/sample-transcript.md](docs/sample-transcript.md). The short version is:

1. Discover capabilities with `capabilities.search`.
2. Execute generated code with `code.execute`.
3. Read spilled report output with `artifact.read`.
4. Preview, list, and apply a mutation with `code.execute`, `mutation.list`, and `mutation.apply`.

Example `code.execute` body:

```json
{
  "conversationId": "sample-read",
  "code": "async function main() { return await programmatic.tasks.getById({ taskId: 'task-1' }); }"
}
```

Example report-spill execution:

```json
{
  "conversationId": "sample-report",
  "maxResultBytes": 256,
  "code": "async function main() { return await programmatic.tasks.exportReport({ projectId: 'project-alpha' }); }"
}
```

Follow that with `artifact.read`:

```json
{
  "conversationId": "sample-report",
  "artifactId": "<resultArtifactId>",
  "limit": 1
}
```

Example mutation preview:

```json
{
  "conversationId": "sample-complete",
  "code": "async function main() { return await programmatic.tasks.complete({ taskId: 'task-1' }); }"
}
```

List pending approvals for the conversation:

```json
{
  "conversationId": "sample-complete"
}
```

Apply the approved mutation:

```json
{
  "conversationId": "sample-complete",
  "approvalId": "<approvalId>",
  "approvalNonce": "<approvalNonce>"
}
```

The sample also includes a rejected mutation path: trying to complete `task-3` returns a preview, but `mutation.apply` fails with `validation_failed` because the task is already completed.

## Health And CORS

The sample maps health checks at `/mcp/health`.

The library does not enable CORS automatically. The sample keeps browser-oriented CORS disabled by default and documents a safe localhost-only option through configuration:

```json
{
  "SampleServer": {
    "Cors": {
      "EnableBrowserTooling": true,
      "AllowedOrigins": [
        "http://127.0.0.1:3000",
        "http://localhost:3000"
      ]
    }
  }
}
```

That policy is only meant for local browser tooling. It is not a production default.

## Status

The repository now includes the solution bootstrap, core contracts, Jint runtime, ASP.NET Core transport, and the sample reference server. The implementation spec still lives in [INITIAL_PLAN.md](INITIAL_PLAN.md).

If the README and the plan ever disagree, treat [INITIAL_PLAN.md](INITIAL_PLAN.md) as the source of truth for v0 scope and contract decisions.
