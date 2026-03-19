# Security

This document is the Phase 2 draft for the v0 security model.

## Built-In Runtime Boundary

The built-in Jint runtime is a constrained convenience sandbox for trusted or authenticated same-tenant workflows. It is not a hostile multi-tenant isolation boundary.

## Core Identity Model

The library distinguishes between:

- `conversationId`: client-controlled workflow correlation
- `callerBindingId`: server-derived identity used for approval and artifact binding

Mutation flows require both stable caller binding and an explicit authorization decision.

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
- the core contracts require runtime validation of inputs and protocol-visible result payloads, and later runtime/transport phases are responsible for enforcing that rule

## Current Phase Boundary

This is a Phase 2 draft. Transport-specific concerns such as cookies, signed headers, same-origin checks, and reconnect behavior are implemented later in the ASP.NET Core integration phase.
