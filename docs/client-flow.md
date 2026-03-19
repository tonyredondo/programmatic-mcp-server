# Client Flow

This document is the Phase 2 draft for the intended code-mode client flow.

## Intended Sequence

The sequence below describes the contract the repository is targeting. In Phase 2, the shared core types now define the shapes needed by these steps, but the actual MCP transport handlers arrive in later phases.

1. Call `initialize`.
2. Call `tools/list`.
3. Use `capabilities.search` to discover a focused subset of capabilities.
4. Once the MCP HTTP transport is in place, fetch the generated TypeScript declarations from the advertised `/types` endpoint when the client wants stronger code generation support. Phase 2 only defines the shared declaration contract and generator.
5. Generate JavaScript against `globalThis.programmatic`.
6. Execute that JavaScript through `code.execute`.
7. Read large results through `artifact.read` when execution returns artifact descriptors. The transport-level read handler is implemented later, but the core response shape is already defined.
8. For writes, use mutation previews first and then `mutation.list`, `mutation.apply`, or `mutation.cancel`. Those transport handlers are implemented later, but their shared envelope shapes are defined in the core package.

## Expected Client Behavior

- Clients provide a collision-resistant `conversationId`.
- Clients treat `capabilityVersion` as the contract fingerprint for discovery and generated declarations.
- Clients keep the `approvalId` + `approvalNonce` pair from preview-producing execution results when they want to apply or cancel later. Phase 2 defines that shared envelope shape even though the transport handlers arrive later.
- Clients should treat mutation previews as advisory and expect apply-time revalidation.

## Current Phase Boundary

This is a Phase 2 contract draft. The shared core package now defines the catalog, preview, apply, artifact, and execution envelopes those steps rely on, while the transport-level endpoints are implemented later in the ASP.NET Core integration phase.
