# ProgrammaticMcp.Jint

`ProgrammaticMcp.Jint` provides the default JavaScript runtime for the library.

## What It Contains

- `JintCodeExecutor`
- `JintExecutorOptions`
- the generated `programmatic.*` namespace bootstrap, including `programmatic.client.sample(...)`
- runtime diagnostics, console capture, artifact spilling, and mutation preview creation

## When To Use It

Use this package when you want the built-in constrained JavaScript execution model backed by `Jint`.

## Runtime Notes

- one Jint engine is created per request
- host capability calls are serialized inside a single execution
- `programmatic.client.sample(...)` still requires an explicit read-only `VisibleApiPaths` scope; when that scope is present it uses a contextual sampling client if the host provides one, otherwise it returns `sampling_unavailable`
- request-level limits can only narrow configured defaults; they never widen them
- large results spill into artifacts when caller continuity is available

## Jint Upgrade Checklist

Before changing the pinned Jint version:

1. rerun the runtime proof tests
2. rerun the executor suite
3. verify syntax-error mapping, cancellation, `Promise.all(...)`, and bridge serialization behavior
4. confirm no `capabilityVersion` or declaration drift from the same catalog input
