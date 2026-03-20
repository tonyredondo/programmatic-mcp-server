# Jint Runtime Proof

## Status

Passed.

## Exact Jint Version Pinned For The Proof

- `Jint` `4.6.3`

This version is pinned in [Directory.Packages.props](../../Directory.Packages.props).

## What The Proof Covered

The harness in [RuntimeProofHarness.cs](../../src/ProgrammaticMcp.Jint/Spike/RuntimeProofHarness.cs) and the tests in [RuntimeProofHarnessTests.cs](../../tests/ProgrammaticMcp.Jint.Tests/RuntimeProofHarnessTests.cs) verified:

- async entrypoints can return awaited values correctly
- explicit promise unwrapping works through `UnwrapIfPromiseAsync`
- `Promise.all(...)` works while host-call dispatch remains serialized by the bridge
- JavaScript syntax errors can be mapped to `syntax_error` with line and column data
- cancellation stops execution within a bounded timeout
- unknown capability names fail deterministically through the bridge

## Decision

The async runtime profile remains valid and does not need to fall back to a sync-only execution model.

This proof harness remains in the repository as a narrow validation artifact. The production runtime path is implemented separately in `ProgrammaticMcp.Jint`.

## Verification Commands

- `dotnet test tests/ProgrammaticMcp.Jint.Tests/ProgrammaticMcp.Jint.Tests.csproj --configuration Release`
- `dotnet test ProgrammaticMcp.sln --configuration Release --no-build --filter FullyQualifiedName~RuntimeProofHarnessTests`

## Source Confirmation

The Jint async and promise APIs used by this proof were confirmed from official sources:

- [Jint NuGet package 4.6.3](https://www.nuget.org/packages/Jint)
- [Jint README async execution section](https://github.com/sebastienros/jint)
