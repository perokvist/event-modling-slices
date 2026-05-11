# Coding Standard for dapr-lab

This document defines coding conventions for the dapr-lab repository. It is intentionally small and focused on C# style choices relevant to the project.

## Naming

- Private fields: camelCase without underscore prefix (e.g., `streamHead`, `eventStoreClient`).
- Private readonly fields: camelCase (no underscore). Optional suffixes like `Readonly` are allowed but not recommended.
- Private properties: PascalCase if property-like behavior; prefer private fields for simple storage.
- Public members: PascalCase.
- Local variables and parameters: camelCase.
- Constants: PascalCase.

## Formatting

- Use 4 spaces for indentation.
- Line length: prefer ≤ 120 characters.

## Files

- One top-level type per file when practical.
- File names should match the primary public type name.

## Enforcements

- Add an `.editorconfig` at the repository root to enforce naming and formatting preferences in supported editors/IDEs.
- Consider adding Roslyn analyzer rules to fail builds on infractions.

## Rationale

Avoiding leading underscores reduces friction when using automatic refactoring tools and aligns the project with common .NET conventions used by many teams.

---

## Pattern Guides

For in-depth guidance on implementing command slices and event-sourced patterns, see:

- **[Record Structures](records.md)** — Command, Event, and State record patterns, naming conventions, and hierarchies
- **[Decider Pattern](decider.md)** — Decision logic, state evolution, guard streams, and event routing
- **[ASP.NET Endpoints](endpoints.md)** — Feature mapping, request/response handling, and integration with the application service layer
