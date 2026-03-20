# ProgrammaticMcp.AspNetCore

`ProgrammaticMcp.AspNetCore` exposes the programmatic MCP surface over ASP.NET Core and the C# MCP SDK.

## What It Contains

- ASP.NET Core service registration
- MCP tool handlers for search, execution, artifacts, and approvals
- MCP resource handlers for `resources/list` and `resources/read`
- live MCP sampling integration on the stateful HTTP transport
- `/types` endpoint support
- caller binding via signed cookies and optional signed headers
- throttling, startup recovery, and graceful shutdown coordination

## When To Use It

Use this package when you want the first supported HTTP transport for the library.

## Operational Notes

- the built-in approval and artifact stores are in-memory by default
- read-only MCP resources stay separate from the six-tool programmatic surface
- live sampling is available only on the stateful transport, only when the connected client advertises MCP sampling, and only during explicitly scoped read-only `code.execute` runs or capability handlers invoked inside those runs
- mutation handlers, resource readers, and sampling-tool handlers receive blocked sampling clients instead of live sampling
- cookie-based caller binding is intended for same-origin or trusted localhost flows
- the `/types` endpoint inherits the mapped route's authorization behavior
- external client compatibility expectations are documented in [`../client-compatibility.md`](../client-compatibility.md)
