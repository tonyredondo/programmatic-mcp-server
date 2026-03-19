# Client Flow

This document describes the current v0 code-mode client flow exposed by the repository.

## Sequence

The normal HTTP MCP sequence is:

1. Call `initialize`.
2. Call `tools/list`.
3. Use `capabilities.search` to discover a focused subset of capabilities.
4. Fetch the generated TypeScript declarations from the advertised `/types` endpoint when the client wants stronger code generation support.
5. Generate JavaScript against `globalThis.programmatic`.
6. Execute that JavaScript through `code.execute`.
7. Read large results through `artifact.read` when execution returns artifact descriptors.
8. For writes, use mutation previews first and then `mutation.list`, `mutation.apply`, or `mutation.cancel`.

## Expected Client Behavior

- Clients provide a collision-resistant `conversationId`.
- Clients treat `capabilityVersion` as the contract fingerprint for discovery and generated declarations.
- Clients keep the `approvalId` + `approvalNonce` pair from preview-producing execution results when they want to apply or cancel later.
- Clients should treat mutation previews as advisory and expect apply-time revalidation.
