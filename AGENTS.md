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

- The shipped code, tests, and public documentation in this repository are the source of truth for current behavior.
- Do not invent behavior that is not supported by those contracts and docs when implementing the library.
- If implementation work changes public behavior, update the code, tests, and docs together in the same change.

## Working Rules

- Use English for code, docs, comments, commit messages, PR text, and branch names unless explicitly told otherwise.
- Keep the core library reusable and product-neutral.
- Keep transport-specific behavior in adapters, not in the core abstractions.
- Keep the exposed runtime surface narrow, explicit, and schema-driven.
- Prefer structured diagnostics and explicit contracts over implicit behavior.
- Keep docs, tests, and samples aligned with the implemented behavior when it changes.

## V0 Direction

- Primary implementation language: C#
- Initial MCP foundation: `modelcontextprotocol/csharp-sdk`
- Initial execution runtime: `Jint`
- First integration layer: ASP.NET Core over the C# MCP SDK

## V0 Non-Goals

- Multi-language runtimes
- Distributed execution
- Strong hostile multi-tenant isolation inside the built-in runtime
- Product-specific business logic
