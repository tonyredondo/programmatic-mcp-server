# Repository Guidelines

## Purpose

This repository hosts a reusable .NET library for building programmatic MCP servers.

In this repository, "programmatic MCP" means:

- MCP remains the integration protocol.
- agents discover capabilities progressively instead of loading everything up front.
- agents write code against a generated API surface.
- that code runs in a constrained runtime.
- large intermediate results stay out of model context whenever possible.
- writes go through explicit approval-aware mutation flows.

## Source Of Truth

- The implemented behavior of the repository is defined by the shipped code, the automated tests, and the maintained public documentation in `README.md` and `docs/`.
- Treat the code and tests as authoritative for current behavior. Treat the public docs as the maintained explanation of that behavior.
- Do not invent behavior that is not supported by the implemented contracts, tests, and public documentation.
- If implementation work changes public behavior, update the code, tests, sample behavior, and public docs together in the same change.
- Compatibility expectations are documented in `docs/client-compatibility.md`.
- The client interaction model and security model are documented in `docs/client-flow.md` and `docs/security.md`.

## Working Rules

- Use English for code, docs, comments, commit messages, PR text, and branch names unless explicitly told otherwise.
- Keep the core library reusable and product-neutral.
- Keep transport-specific behavior in adapters, not in the core abstractions.
- Keep the exposed runtime surface narrow, explicit, and schema-driven.
- Prefer structured diagnostics and explicit contracts over implicit behavior.
- Keep docs, tests, and samples aligned with the implemented behavior when it changes.
- Keep package docs under `docs/packages/` aligned with the shipped NuGet packages.
- Use subagents by default for parallel review and documentation work when the work can be split cleanly.
- Reuse or close idle agents when the thread starts to fill up.

## V0 Direction

- Primary implementation language: C#
- Initial MCP foundation: `modelcontextprotocol/csharp-sdk`
- Initial execution runtime: `Jint`
- First integration layer: ASP.NET Core over the C# MCP SDK
- The sample server is the end-to-end reference implementation for the repository.

## V0 Non-Goals

- Multi-language runtimes
- Distributed execution
- Strong hostile multi-tenant isolation inside the built-in runtime
- Product-specific business logic
