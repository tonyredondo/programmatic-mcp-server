# Overview

This document is the implementation-first overview of the repository.

It explains what the library does, why it exists, how the main pieces fit together, what the repository supports today, and what it explicitly does not try to do.

## What This Library Is

`ProgrammaticMcp` is a reusable .NET library for building programmatic MCP servers.

In this repository, "programmatic MCP" means:

- MCP remains the integration protocol
- clients discover capabilities progressively instead of loading everything up front
- clients generate code against a predictable API surface
- that code runs in a constrained runtime
- large intermediate results spill into artifacts instead of staying inline
- writes go through explicit approval-aware mutation flows

The repository is library-first. It is not a product-specific server.

## Why It Exists

Standard MCP usage is often a loop of:

1. inspect tools
2. call one tool
3. inspect the result
4. call another tool

That works, but it can become noisy when the capability surface is large or when intermediate results are too big to keep in the model context.

This library supports a different model:

1. discover a narrower slice of the capability surface
2. generate JavaScript against that slice
3. run the generated code in a constrained runtime
4. store large intermediate outputs as artifacts
5. make writes explicit through mutation preview and approval records

That gives the client a code-first interaction model while still using MCP as the transport protocol.

## Core Execution Model

The implemented execution flow is:

1. the host registers capabilities and mutations through `ProgrammaticMcpBuilder`
2. the library builds a catalog snapshot, generated TypeScript declarations, and a `capabilityVersion`
3. the transport exposes a small MCP tool surface
4. the client uses discovery and `/types` to generate JavaScript against `globalThis.programmatic`
5. `JintCodeExecutor` runs that JavaScript in a bounded runtime
6. host capability calls are bridged back into the registered handlers
7. large results spill into artifacts when they exceed the configured inline result limit
8. write intents become approval records and are applied later through explicit mutation tools

The built-in runtime uses one Jint engine per request. Host capability calls are serialized inside a single execution.

## Discovery And Code Generation

The current MCP surface is intentionally small:

- `capabilities.search`
- `code.execute`
- `artifact.read`
- `mutation.list`
- `mutation.apply`
- `mutation.cancel`

Clients discover capabilities with `capabilities.search`, then fetch the generated declarations from `/types` when they want stronger code generation support.

Generated TypeScript and `capabilityVersion` come from the same catalog snapshot, so discovery and declarations stay aligned.

## Artifact Flow

Large execution outputs do not have to stay inline.

The built-in implementation can spill oversized results into stored artifacts and return:

- artifact descriptors
- `resultArtifactId` when the main result was spilled

Clients then page through the artifact content with `artifact.read`.

The built-in artifact store is in-memory by default and scoped to the caller binding plus conversation id.

## Mutation And Approval Flow

The runtime does not apply mutations directly from sandbox code.

Instead:

1. sandbox code calls a mutation capability
2. the call produces a preview
3. the preview becomes an approval record
4. the client can rediscover pending approvals with `mutation.list`
5. the client uses `mutation.apply` or `mutation.cancel` with the original `approvalId` and `approvalNonce`

Important current behavior:

- `mutation.list` rediscloses approval ids, but not approval nonces
- clients must keep the `approvalNonce` from the preview-producing execution result
- apply handlers revalidate current state before mutating
- approvals are bound to `conversationId`, `callerBindingId`, mutation name, and canonical args hash

## HTTP Transport And Caller Binding

The first supported transport adapter is ASP.NET Core over the C# MCP SDK.

The ASP.NET Core layer currently supports caller binding through:

- authenticated principal identity
- MCP session identity
- built-in signed cookie fallback
- built-in signed-header fallback

The built-in cookie fallback is:

- route-scoped
- `HttpOnly`
- `SameSite=Lax`
- `Secure` by default

For cookie-derived caller binding, same-origin checks apply to MCP tool requests that rely on that cookie.

The sample server runs MCP over stateless HTTP, enables signed-header fallback, and also supports the built-in cookie fallback for localhost-style development clients.

## Built-In Storage And Runtime Assumptions

The current built-in implementation is intentionally narrow:

- the Jint runtime is a constrained convenience sandbox, not a hostile multi-tenant isolation boundary
- the built-in approval and artifact stores are in-memory by default
- reconnect continuity is only guaranteed within a single process or a sticky-session deployment unless the host supplies its own stores
- authentication, authorization, CORS, TLS, and external exposure are host-owned concerns

## Supported Today

The repository currently supports:

- C# as the implementation language
- .NET 8 and .NET 10 library targets
- `Jint` as the built-in JavaScript runtime
- ASP.NET Core as the current transport adapter
- repository-validated client coverage for:
  - normal C# MCP SDK session identity
  - C# MCP SDK cookie fallback reconnect
  - C# MCP SDK signed-header fallback reconnect
  - raw HTTP cookie fallback reconnect
  - raw HTTP signed-header fallback reconnect

For the exact compatibility matrix, see [docs/client-compatibility.md](client-compatibility.md).

## Not Supported

The repository does not currently try to provide:

- multi-language runtimes
- distributed execution
- strong hostile multi-tenant isolation inside the built-in runtime
- product-specific business logic
- automatic CORS policy configuration
- a distributed built-in approval/artifact store

## Repository Layout

The main implementation is split into:

- `src/ProgrammaticMcp`
  Core contracts, catalog/builders, schema generation, hashing, approvals, artifacts, and shared envelopes.
- `src/ProgrammaticMcp.Jint`
  The built-in JavaScript runtime and bridge.
- `src/ProgrammaticMcp.AspNetCore`
  The ASP.NET Core transport adapter and HTTP-specific behavior.
- `samples/ProgrammaticMcp.SampleServer`
  The end-to-end reference host.
- `tests/*`
  Focused suites for core contracts, the Jint runtime, and the ASP.NET transport/sample integration.

## Where To Go Next

- [README.md](../README.md)
  Repository entry point and quickstart.
- [docs/client-flow.md](client-flow.md)
  Client interaction flow.
- [docs/security.md](security.md)
  Security model and host responsibilities.
- [docs/sample-transcript.md](sample-transcript.md)
  Concrete end-to-end sample flow.
- [docs/packages/README.md](packages/README.md)
  Package-specific notes.
