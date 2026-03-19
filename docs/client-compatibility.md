# Client Compatibility

This document records the Phase 4 compatibility state for the v0 HTTP MCP integration.

## Repository-Validated Clients

The following client paths are validated by automated tests in this repository:

| Client | Transport | Caller Binding | Version / Build | OS | Validation Status |
| --- | --- | --- | --- | --- | --- |
| Repository .NET MCP SDK harness | HTTP MCP via `ModelContextProtocol` | Session identity | `ModelContextProtocol` `1.1.0` | macOS | Validated |
| Repository raw HTTP harness | Sessionless HTTP MCP | Cookie fallback | in-repo test harness | macOS | Validated |
| Repository raw HTTP harness | Sessionless HTTP MCP | Signed header fallback | in-repo test harness | macOS | Validated |

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

## Locally Detected Desktop Clients

The following desktop clients are installed on this machine, so their exact versions are known. They have not been automated or manually exercised by this repository during Phase 4.

| Client | Installed Version | OS | Caller Binding Notes | Validation Status |
| --- | --- | --- | --- | --- |
| Claude Desktop | `1.1.7203` | macOS | Expected to use cookie fallback when cookies are preserved | Not yet validated in-repo |
| Codex desktop app | `26.317.21539` | macOS | Expected to use either cookie fallback or signed-header fallback depending on client behavior | Not yet validated in-repo |

## Current Compatibility Notes

- The built-in cookie fallback is intended for HTTP MCP clients that preserve cookies across reconnects.
- The built-in signed-header fallback is intended for non-cookie HTTP clients that can be provisioned with a stable `X-Programmatic-Mcp-Caller-Binding` token out of band.
- The repository test harness validates both fallback paths through a sessionless raw HTTP client and also validates the normal sessioned C# MCP SDK client path.
- The signed-header bootstrap path is host-owned: the host generates a stable token through `IProgrammaticCallerBindingTokenService.CreateSignedHeaderToken(...)` and provisions it to the client out of band before the client reconnects with `X-Programmatic-Mcp-Caller-Binding`.
- The built-in cookie fallback issues a route-scoped, `HttpOnly`, `SameSite=Lax` cookie and enforces same-origin checks when that cookie is used for mutation-related flows.
- Browser-style CORS configuration remains host-owned. The library does not enable CORS automatically.
- Health endpoints also remain host-owned. The library does not map them automatically.

## Remaining Manual Validation Work

The remaining compatibility work is outside automated repository coverage:

- verify Claude Desktop against the cookie reconnect flow
- verify Codex desktop against its supported caller-binding mode
- record any client-specific limitations once those manual checks are complete
