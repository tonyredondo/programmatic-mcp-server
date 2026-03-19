# ProgrammaticMcp

`ProgrammaticMcp` is the core package for programmatic MCP servers.

## What It Contains

- capability registration and catalog search
- JSON Schema generation and runtime validation
- deterministic TypeScript declaration generation
- canonical hashing for approval arguments and `capabilityVersion`
- shared execution, artifact, and mutation envelopes
- in-memory approval and artifact stores

## When To Use It

Use this package when you want the shared contracts and registration model without committing to a transport or runtime adapter yet.

## Main Public Surface

- `ProgrammaticMcpBuilder`
- `ICapabilityCatalog`
- `ICodeExecutor` and `ICodeExecutionService`
- `IArtifactStore` and `IApprovalStore`
- `IProgrammaticAuthorizationPolicy`
- schema, hashing, and validation helpers

## Notes

- generated TypeScript and `capabilityVersion` come from the same catalog snapshot
- mutation flows require an explicit authorization policy choice
- the built-in stores are in-memory and intended for the documented v0 scope
