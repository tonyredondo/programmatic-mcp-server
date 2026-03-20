# Programmatic MCP Server

`Programmatic MCP Server` is a .NET library for building MCP servers that are meant to be used through generated code, not only through direct tool calls.

The library is built around one idea: an agent should be able to discover a focused capability surface, generate code against that surface, run the code in a constrained runtime, and keep large intermediate results out of model context whenever possible.

## What The Repository Provides Today

The current implementation provides:

- capability registration and progressive discovery
- generated JavaScript and TypeScript-facing API surface
- constrained JavaScript execution through `Jint`
- artifact storage and paged artifact reads
- preview, list, apply, and cancel mutation approval flows
- caller binding for HTTP MCP clients
- ASP.NET Core integration on top of the C# MCP SDK
- a runnable sample server that exercises the end-to-end loop

## Why It Exists

Traditional MCP usage is tool-call-first: clients load tools, pick one, call it, and repeat. This repository supports a different model:

1. discover a narrow set of capabilities
2. generate code against a predictable API surface
3. execute that code in a bounded runtime
4. spill large outputs into artifacts
5. require explicit approval-aware mutation flows for writes

In this model, MCP is still the protocol, but the client experience is code-first.

## How It Works

At a high level, the implemented flow is:

1. call `initialize`
2. call `tools/list`
3. use `capabilities.search` to discover relevant capabilities
4. fetch the generated declarations from `/types` when the client wants stronger code generation support
5. generate JavaScript against `globalThis.programmatic`
6. execute that JavaScript through `code.execute`
7. read large results through `artifact.read`
8. preview and apply writes through `mutation.list`, `mutation.apply`, and `mutation.cancel`

For a fuller explanation of the execution model, artifact flow, approval flow, transport model, and supported scope, see [docs/overview.md](docs/overview.md).

## Package Layout

The repository currently ships three library packages and one sample server:

- `ProgrammaticMcp`
  Core abstractions, capability metadata, schema generation, hashing, approvals, artifacts, and shared contracts.
- `ProgrammaticMcp.Jint`
  The Jint-backed execution runtime, generated namespace bootstrap, bridge logic, and runtime diagnostics.
- `ProgrammaticMcp.AspNetCore`
  ASP.NET Core and C# MCP SDK integration, tool exposure, caller binding, HTTP-specific behavior, and `/types`.
- `samples/ProgrammaticMcp.SampleServer`
  A reference host used to prove the full loop end to end.

Package-specific notes live under [docs/packages](docs/packages/README.md).

## Minimal Host Example

The smallest useful host wires the builder, registers a read-only capability, and maps the MCP route:

```csharp
using ProgrammaticMcp;
using ProgrammaticMcp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProgrammaticMcpServer(options =>
{
    options.Builder
        .AddCapability<WeatherInput, WeatherResult>(
            "weather.current",
            capability => capability
                .WithDescription("Returns the current weather.")
                .UseWhen("You need a read-only weather lookup.")
                .DoNotUseWhen("You need to mutate data.")
                .WithHandler((input, _) => ValueTask.FromResult(new WeatherResult(input.City, "sunny"))));
});

var app = builder.Build();
app.MapProgrammaticMcpServer("/mcp");
app.Run();

public sealed record WeatherInput(string City);
public sealed record WeatherResult(string City, string Conditions);
```

That host exposes:

- `POST /mcp`
- `GET /mcp/types`

The discovery surface, generated declarations, execution surface, artifact behavior, and mutation contracts all come from that single registration source.

If the host later adds mutations, it must also make an explicit authorization-policy choice for those mutation flows.

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

The sample runs MCP over stateless HTTP, enables signed-header caller binding, and also issues the built-in caller-binding cookie for localhost-style cookie-capable clients.

## Sample Flow

The short version of the sample flow is:

1. discover capabilities with `capabilities.search`
2. execute generated code with `code.execute`
3. read spilled report output with `artifact.read`
4. preview, list, and apply a mutation with `code.execute`, `mutation.list`, and `mutation.apply`

Clients must provide a `conversationId` that matches `^[A-Za-z0-9._:-]{1,128}$`.

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

Example mutation apply request:

```json
{
  "conversationId": "sample-complete",
  "approvalId": "<approvalId>",
  "approvalNonce": "<approvalNonce>"
}
```

The full end-to-end transcript lives in [docs/sample-transcript.md](docs/sample-transcript.md).

## Supported Scope

The current implementation supports:

- .NET 8 and .NET 10 target frameworks for the library packages
- `Jint` as the built-in JavaScript runtime
- ASP.NET Core as the current supported transport adapter
- in-memory built-in approval and artifact stores
- HTTP caller binding through authenticated principal, MCP session identity, signed cookie fallback, or signed-header fallback

Compatibility details and validated client paths are documented in [docs/client-compatibility.md](docs/client-compatibility.md).

## Not Supported

The repository does not currently provide:

- multi-language runtimes
- distributed execution
- strong hostile multi-tenant isolation inside the built-in runtime
- product-specific business logic
- automatic CORS policy configuration
- a shared built-in approval or artifact store for multi-process deployments

## Documentation Map

- [docs/overview.md](docs/overview.md)
  System overview: what the library does, why it exists, how it works, and what is supported.
- [docs/client-flow.md](docs/client-flow.md)
  The client-side interaction flow.
- [docs/security.md](docs/security.md)
  Security model, caller binding, and host responsibilities.
- [docs/client-compatibility.md](docs/client-compatibility.md)
  Validated client paths and reconnect expectations.
- [docs/sample-transcript.md](docs/sample-transcript.md)
  The sample server’s end-to-end transcript.
- [docs/packages/README.md](docs/packages/README.md)
  Package-specific reference notes.
- [docs/spikes/jint-runtime-proof.md](docs/spikes/jint-runtime-proof.md)
  Historical runtime proof notes for the Jint async model.
