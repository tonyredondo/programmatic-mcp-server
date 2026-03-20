# Client Compatibility

This document records the compatibility state for the first implementation milestone of the v0 HTTP MCP integration.

## Repository-Validated Clients

The following client paths are validated by automated tests in this repository:

| Client | Transport | Caller Binding | Version / Build | OS | Validation Status |
| --- | --- | --- | --- | --- | --- |
| Repository .NET MCP SDK harness | HTTP MCP via `ModelContextProtocol` | Session identity | `ModelContextProtocol` `1.1.0` on `.NET` runtime `10.0.1` via SDK `10.0.101` | GitHub Actions `ubuntu-24.04` | Validated |
| Repository raw HTTP harness | Stateless HTTP MCP | Cookie fallback | `ProgrammaticMcp.AspNetCore.Tests.RawMcpClient` from this repository on `.NET` runtime `10.0.1` via SDK `10.0.101` | GitHub Actions `ubuntu-24.04` | Validated |
| Repository raw HTTP harness | Stateless HTTP MCP | Signed header fallback | `ProgrammaticMcp.AspNetCore.Tests.RawMcpClient` from this repository on `.NET` runtime `10.0.1` via SDK `10.0.101` | GitHub Actions `ubuntu-24.04` | Validated |

Validation coverage for the repository harness includes:

- `initialize`
- `tools/list`
- `capabilities.search`
- `code.execute`
- `artifact.read`
- `mutation.list`
- `mutation.apply`
- `mutation.cancel`
- `/types`
- cookie fallback
- signed-header fallback
- request cancellation
- graceful shutdown draining
- per-request DI scoping
- compatibility JSON text mirroring

## Current Compatibility Notes

- The built-in cookie fallback is intended for HTTP MCP clients that preserve cookies across reconnects.
- The built-in signed-header fallback is intended for non-cookie HTTP clients that can be provisioned with a stable `X-Programmatic-Mcp-Caller-Binding` token out of band.
- The repository test harness validates both fallback paths through a stateless raw HTTP client, and it also validates the normal sessioned C# MCP SDK client path.
- The signed-header bootstrap path is host-owned: the host generates a stable token through `IProgrammaticCallerBindingTokenService.CreateSignedHeaderToken(...)` and provisions it to the client out of band before the client reconnects with `X-Programmatic-Mcp-Caller-Binding`.
- The built-in cookie fallback issues a route-scoped, `HttpOnly`, `SameSite=Lax` cookie and enforces same-origin checks on any MCP request that resolves caller binding from that cookie.
- Browser-style CORS configuration remains host-owned. The library does not enable CORS automatically.
- Health endpoints also remain host-owned. The library does not map them automatically.

## Reconnect Expectations

- Session-identity clients are expected to preserve the underlying MCP HTTP session for the life of the conversation. The repository-owned C# harness validates that path directly.
- Cookie fallback is intended for clients that reconnect over plain HTTP MCP but preserve the route-scoped caller-binding cookie across those reconnects.
- Signed-header fallback is intended for clients that cannot preserve cookies but can reconnect with the same host-provisioned `X-Programmatic-Mcp-Caller-Binding` token.
- The built-in approval and artifact stores are process-local and in-memory. Reconnect continuity is therefore only guaranteed within a single process or a sticky-session deployment that keeps the same caller on the same process while approvals and artifacts are still alive.
- Restarting the host or reconnecting to a different process without a shared approval/artifact store invalidates built-in pending approvals and stored artifacts, even if the caller binding itself remains stable.

## Manual Compatibility Notes

The following compatibility checks are outside the pinned repository-owned milestone matrix:

- Local macOS runs are useful development checks, but they are not part of the pinned supported matrix unless they are added to automated validation and recorded above with exact version/build details.
- Claude Desktop `1.1.7203` on macOS is installed locally, but it has not been added to the pinned supported matrix yet. When it is evaluated, the result should be recorded here with the tested caller-binding mode.
- Codex desktop `26.318.11754` (build `1100`) on macOS is installed locally, but it has not been added to the pinned supported matrix yet. When it is evaluated, the result should be recorded here with the tested caller-binding mode.
- Any client-specific limitations discovered during those manual checks should be recorded here without changing the repository-owned supported matrix until the matrix is intentionally expanded.
