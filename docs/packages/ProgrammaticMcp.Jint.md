# ProgrammaticMcp.Jint

`ProgrammaticMcp.Jint` provides the default JavaScript runtime for the library.

## What It Contains

- `JintCodeExecutor`
- `JintExecutorOptions`
- the generated `programmatic.*` namespace bootstrap
- runtime diagnostics, console capture, artifact spilling, and mutation preview creation

## When To Use It

Use this package when you want the built-in constrained JavaScript execution model backed by `Jint`.

## Runtime Notes

- one Jint engine is created per request
- host capability calls are serialized inside a single execution
- request-level limits can only narrow configured defaults; they never widen them
- large results spill into artifacts when caller continuity is available

## Jint Upgrade Checklist

Before changing the pinned Jint version:

1. rerun the Phase 1.5 runtime proof tests
2. rerun the Phase 3 executor suite
3. verify syntax-error mapping, cancellation, `Promise.all(...)`, and bridge serialization behavior
4. confirm no `capabilityVersion` or declaration drift from the same catalog input
