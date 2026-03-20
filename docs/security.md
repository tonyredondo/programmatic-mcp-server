# Security

This document describes the current v0 security model.

## Built-In Runtime Boundary

The built-in Jint runtime is a constrained convenience sandbox for trusted or authenticated same-tenant workflows. It is not a hostile multi-tenant isolation boundary.

## Default Limits

The built-in implementation ships with host-configurable defaults and ceilings that are enforced before work reaches user handlers.

- Execution requests enforce timeout, API-call, result-size, statement, memory, code-size, args-size, and console-output limits.
- Artifact storage enforces per-artifact, per-conversation, and global byte budgets plus TTL-based cleanup.
- Approval flows enforce expiry, per-caller snapshot retention, and stale-`applying` recovery.
- Request-supplied execution limits may only narrow the public execution defaults for the current request. They never widen host defaults.

## Core Identity Model

The library distinguishes between:

- `conversationId`: client-controlled workflow correlation
- `callerBindingId`: server-derived identity used for approval and artifact binding

Mutation flows require both stable caller binding and an explicit authorization decision.

In the ASP.NET Core integration, caller binding can come from:

- the authenticated principal when the host maps one
- MCP session identity when the transport exposes one
- the built-in signed cookie fallback
- the built-in signed-header fallback when the host provisions a stable token out of band

## Mutation Safety Model

- sandbox code does not apply mutations directly
- mutation calls create previews
- apply and cancel operations work through approval records
- approvals are bound to `conversationId`, `callerBindingId`, mutation name, and canonical args hash
- apply handlers must revalidate current state before mutating

## Determinism And Validation

- schemas are generated from one fixed serializer contract
- generated TypeScript and `capabilityVersion` are based on the same registration data
- canonical hashing is used for approval args and capability-version computation
- runtime and transport layers validate inputs, protocol-visible payloads, cursor state, and approval transitions before they are exposed over MCP

## HTTP Transport Notes

- the built-in cookie fallback is route-scoped, `HttpOnly`, and `SameSite=Lax`
- same-origin checks are enforced on any MCP request that resolves caller binding from the built-in cookie
- the built-in signed-header fallback is intended for clients that cannot preserve cookies but can hold a host-provisioned stable token
- CORS policy remains host-owned

## Host Exposure Guidance

- The library does not force a loopback-only bind. Hosts decide whether the MCP endpoint is local-only or exposed more broadly.
- The sample server and recommended local-development setup use loopback-only exposure as the safe default.
- Authentication, authorization policy, reverse-proxy exposure, TLS termination, and CORS policy remain host-owned concerns.
- If the host exposes the MCP endpoint beyond a trusted local environment, it should also supply real authentication, authorization, and storage choices that match that exposure model.
