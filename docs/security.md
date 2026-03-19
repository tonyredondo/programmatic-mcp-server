# Security

This document describes the current v0 security model.

## Built-In Runtime Boundary

The built-in Jint runtime is a constrained convenience sandbox for trusted or authenticated same-tenant workflows. It is not a hostile multi-tenant isolation boundary.

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
- same-origin checks are enforced when cookie-based caller binding is used on mutation-related flows
- the built-in signed-header fallback is intended for clients that cannot preserve cookies but can hold a host-provisioned stable token
- CORS policy remains host-owned
