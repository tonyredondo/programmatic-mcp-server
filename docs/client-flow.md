# Client Flow

This document describes the current v0 code-mode client flow exposed by the repository.

For the broader system model and supported scope, see [overview.md](overview.md).

## Sequence

The normal HTTP MCP sequence is:

1. Call `initialize`.
2. Call `tools/list`.
3. Optionally call `resources/list` and `resources/read` when the server advertises read-only MCP resources.
4. Use `capabilities.search` to discover a focused subset of capabilities.
5. Fetch the generated TypeScript declarations from the advertised `/types` endpoint when the client wants stronger code generation support.
6. Generate JavaScript against `globalThis.programmatic`.
7. Execute that JavaScript through `code.execute`.
8. When the server is stateful and the client advertises MCP sampling, use `programmatic.client.sample(...)` only inside explicitly scoped read-only executions.
8. Read large results through `artifact.read` when execution returns artifact descriptors.
9. For writes, use mutation previews first and then `mutation.list`, `mutation.apply`, or `mutation.cancel`.

## Expected Client Behavior

- Clients provide a collision-resistant `conversationId` that matches `^[A-Za-z0-9._:-]{1,128}$`.
- Clients treat `capabilityVersion` as the contract fingerprint for discovery and generated declarations.
- Clients treat MCP resources as supplemental read-only context outside the generated `programmatic.*` namespace.
- Clients treat `programmatic.client.sample(...)` as an execution-time helper, not as a capability path. It is available only during explicitly scoped read-only `code.execute` runs. On the stateful ASP.NET transport it can use the connected MCP client when that client advertises sampling; in direct non-ASP.NET execution it remains unavailable unless the host injects its own sampling client, and that injected path still uses the same explicit read-only scope rules.
- Clients keep the `approvalId` + `approvalNonce` pair from preview-producing execution results when they want to apply or cancel later. `mutation.list` re-discloses approval ids, but it does not return approval nonces.
- Clients should treat mutation previews as advisory and expect apply-time revalidation.
- Clients must not register or rely on host capability paths that use reserved generated TypeScript identifiers such as `client`, JavaScript/TypeScript keywords, or prototype-pollution-sensitive names.
