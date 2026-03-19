# ProgrammaticMcp.AspNetCore

`ProgrammaticMcp.AspNetCore` exposes the programmatic MCP surface over ASP.NET Core and the C# MCP SDK.

## What It Contains

- ASP.NET Core service registration
- MCP tool handlers for search, execution, artifacts, and approvals
- `/types` endpoint support
- caller binding via signed cookies and optional signed headers
- throttling, startup recovery, and graceful shutdown coordination

## When To Use It

Use this package when you want the first supported HTTP transport for the library.

## Operational Notes

- the built-in approval and artifact stores are in-memory by default
- cookie-based caller binding is intended for same-origin or trusted localhost flows
- the `/types` endpoint inherits the mapped route's authorization behavior
- external client compatibility expectations are documented in [`../client-compatibility.md`](../client-compatibility.md)
