# Programmatic MCP Server

`Programmatic MCP Server` is a planned .NET library for building MCP servers that are meant to be used through generated code, not only through direct tool calls.

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

## Planned V0 Shape

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

## Planned Package Layout

The implementation plan is organized around three library packages and one sample:

- `ProgrammaticMcp.Core`
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

## Status

This repository is still in the planning stage. The implementation spec lives in [INITIAL_PLAN.md](INITIAL_PLAN.md).

If the README and the plan ever disagree, treat [INITIAL_PLAN.md](INITIAL_PLAN.md) as the source of truth for v0 scope and contract decisions.
