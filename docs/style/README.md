# Code Guidelines for dapr-lab

This directory contains coding standards and pattern guides for the dapr-lab repository. Use these documents as a reference when implementing new features or reviewing code.

---

## Standards

- **[Coding Standard](coding-standard.md)** — C# naming, formatting, and file structure conventions

---

## Pattern Guides

Comprehensive guides for implementing event-sourced command slices using the Decider pattern:

### [1. Record Structures](records.md)
Covers the design and naming of Command, Event, and State records. Includes:
- State record anatomy (immutability, identity, minimalism)
- Command record patterns (POCOs, validation, module-scoped bases)
- Event record patterns (past tense naming, rich payloads, upcasting)
- Record hierarchies and relationships
- Common gotchas (mutability, over-population, naming)

**When to use:** Before designing the records for a new command slice.

### [2. Decider Pattern](decider.md)
Deep dive into the Decider — the pure function that encodes business logic. Includes:
- Anatomy of Decide, Evolve, SelectGuardStreams, and RouteEvent
- Decision logic patterns (state-dependent, state machines, compensation)
- Idempotency guarantees for Evolve
- Guard streams and Dynamic Consistency Boundaries (DCB)
- Event routing for multi-stream appends
- Unit testing Deciders
- Common gotchas (side effects, non-idempotency, incomplete handlers)

**When to use:** When implementing decision logic or understanding the flow of a command through the system.

### [3. ASP.NET Endpoints](endpoints.md)
Guidance on mapping commands to HTTP endpoints and integrating with the application service. Includes:
- Feature structure (static classes with MapEndpoints)
- Route mapping conventions
- Request binding and validation
- The Dispatcher pattern
- Response handling (201 Created, 200 OK, 204 No Content, etc.)
- Error handling (validation, rejection, concurrency)
- Common endpoint patterns
- Common gotchas (forgotten [FromBody], mutable responses, leaking internals)

**When to use:** When adding HTTP routes or integrating a Decider with ASP.NET.

---

## Quick Start

To implement a new command slice:

1. **Design records** (→ [records.md](records.md))
   - Define State, Command, and Event records
   - Follow naming conventions

2. **Implement Decider** (→ [decider.md](decider.md))
   - Write Decide function (pure, testable)
   - Write Evolve function (idempotent, handles all events)
   - Add SelectGuardStreams and RouteEvent if needed

3. **Add endpoints** (→ [endpoints.md](endpoints.md))
   - Create Feature class with MapEndpoints
   - Bind command from request
   - Dispatch to Decider
   - Return appropriate response

---

## Example: StartGame Slice

The `src/SampleApp/Modules/Sample/StartGame` directory demonstrates a complete command slice:

```
StartGame/
├── GameState.cs          # State record
├── StartGameCommand.cs   # Command record
├── GameStarted.cs        # Event record
├── GameDecider.cs        # Decider (shared with other commands)
└── StartGameFeature.cs   # HTTP endpoint mapping
```

See each file for code examples that align with these guides.

---

## Philosophy

These guides prioritize:

- **Clarity** — Brief, scannable reference docs with real examples
- **Consistency** — Single source of truth for each pattern
- **Practicality** — Patterns grounded in the codebase, not theory
- **Simplicity** — DRY principle: guidelines can be referenced by tools and skills instead of repeated

Skills like `add-command-slice` can point to these guides rather than embedding examples inline, keeping both skills and documentation lean and maintainable.

---

## For Copilot Skills

If you're building or updating AI code generation skills (like `add-command-slice`), reference these guides in your skill prompts:

```
"See docs/style/records.md for command record naming and structure.
See docs/style/decider.md for decision logic patterns.
See docs/style/endpoints.md for endpoint mapping."
```

This keeps skill prompts focused on scaffolding rather than repeating patterns.
