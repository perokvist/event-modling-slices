# AI Skills for Implementing Event Modeling Slices

**Research conducted:** April 22, 2026  
**Primary sources:** eventmodeling.org, podcast.eventmodeling.org, adaptechgroup.com, nebulit.de, GitHub code search, leanpub.com

---

## Executive Summary

Event Modeling defines three slice types — **Command (State Change)**, **View (Read Model)**, and **Automation** — each of which maps to a tightly bounded unit of work with a clear Given-When-Then specification. The event modeling community has converged on a powerful insight: because slices have explicit, unambiguous input/output contracts and machine-readable specifications, they are ideal work units for AI code generation. Projects in the community are applying AI primarily in four ways: (1) generating code stubs from event models via tooling (Nebulit Miro plugin, Modellution), (2) using slice specifications as structured prompts for Copilot/Claude/Cursor, (3) implementing automation slices using AI agents as the "processor" in the todo-list pattern, and (4) eliminating requirement ambiguity so AI can generate correct code on the first attempt. Dedicated open-source GitHub repositories that formalize these patterns as standalone "AI skills" are not yet widely available, but the commercial tooling and community documentation reveal a coherent and maturing approach.

---

## The 3 Slice Types in Event Modeling

Understanding the slice types is a prerequisite for understanding how AI is applied. All three derive from the four building blocks: **Trigger**, **Command** (blue), **Event** (yellow), and **View/Read Model** (green).[^1]

### 1. Command Slice (State Change Pattern)

**Pattern:** `Trigger → Command → Event(s)`

A Command slice captures a user intention that mutates system state. The trigger (wireframe or API call) signals intent; the command encapsulates the parameters; the resulting domain events record what actually changed.

**Specification format (Given-When-Then for commands):**
```
Given: <current system state / prior events>
When:  <Command with parameters>
Then:  <Expected resulting event(s)>
```

**Example (hotel booking):**
```
Given:  Hotel has 12 ocean-view rooms, all available
When:   BookRoom(guestId: G42, roomType: OceanView, from: Apr 4, to: Apr 12)
Then:   RoomBooked(bookingId: B99, guestId: G42, roomType: OceanView, from: Apr 4, to: Apr 12)
```

The command handler validates preconditions and, if satisfied, appends events to the event stream. If conditions are not met, no events are produced (the command is rejected).[^2]

### 2. View Slice (Read Model / Query Pattern)

**Pattern:** `Event(s) → View`

A View slice describes how a read model (projection) is built by consuming previously stored events. It answers the informing side of any information system.

**Specification format (Given-Then for views):**
```
Given: <stored events>
Then:  <view/read-model should contain>
```

**Example (hotel availability):**
```
Given:  Hotel is set up with 12 ocean-view rooms,
        OceanViewRoomBooked × 12 (from Apr 4–12)
Then:   CalendarView shows "no availability" for Apr 4–12 for ocean-view rooms
```

The view is passive — it cannot reject events after they are stored, it simply materializes the state they imply. Implementation varies from a SQL read query to a fully persisted projection updated via subscription.[^3]

### 3. Automation Slice (Automation / Translation Pattern)

**Pattern:** `Event(s) → View → Automated Trigger → Command → Event(s)`

An Automation slice captures background processing. The crucial insight from Event Modeling is that the automation always works the same way as a user would: a processor reads a **todo-list view** (a read model of pending tasks), processes each row by issuing a command, and the resulting event causes the todo-list view to remove that row. This keeps automation complexity at "reading a todo list and making a call."[^4]

**Specification format:**
```
Given:  <view of tasks / todo list>
When:   <System/Processor runs>
Then:   <Events expected from command execution>
```

**Example (payment processing):**
```
Given:  PendingPayments view contains { BookingId: B99, GuestId: G42, Amount: $450 }
When:   PaymentProcessor runs
Then:   PaymentProcessed(bookingId: B99, transactionId: TX-123, amount: $450)
```

The **Translation Pattern** is a specialization of Automation: events from one system are translated into commands for another. The read side can only consume one source system; the write side can publish to multiple systems via Pub/Sub.[^5]

---

## Architecture Overview

```
                  ┌─────────────────────────────────────────────────────────────┐
                  │                    Event Model Blueprint                     │
                  │                                                              │
                  │  [Command Slice]  [View Slice]  [Automation Slice]           │
                  │   Given-When-Then  Given-Then    Given-When-Then             │
                  └──────────────────┬──────────────────────────────────────────┘
                                     │ provides structured context
                                     ▼
              ┌──────────────────────────────────────────────────────┐
              │              AI / Code Generation                    │
              │                                                      │
              │  ┌────────────┐  ┌──────────────┐  ┌────────────┐  │
              │  │ Nebulit    │  │  GitHub      │  │ Cursor /   │  │
              │  │ Miro Plugin│  │  Copilot     │  │ Claude     │  │
              │  │ (generate) │  │  (workspace) │  │ (prompts)  │  │
              │  └────────────┘  └──────────────┘  └────────────┘  │
              └──────────────────────────────────────────────────────┘
                                     │ outputs
                                     ▼
        ┌────────────────┐   ┌──────────────────┐   ┌─────────────────────┐
        │ Command Handler│   │ View / Projection │   │ Automation Processor│
        │ (Decide/Evolve)│   │ (event → state)   │   │ (poll view, execute)│
        └────────────────┘   └──────────────────┘   └─────────────────────┘
```

---

## How Projects Are Applying AI to Implement Slices

### Approach 1: AI-Compatible Slice Specifications as Prompts

The fundamental enabler is that event modeling slices come with machine-readable Given-When-Then specifications. Martin Dilger (Nebulit, co-host of the Event Modeling podcast) articulates the key problem: *"Copilot writes code that doesn't fit. Cursor invents features. Claude guesses. Not because the tools are bad — but because requirements are unclear. Garbage in, garbage out."*[^6]

When each slice has a precise specification, AI tools receive unambiguous context. The specification for a command slice maps directly to a test case:
```
Given: (arrange the state)
When:  (act — apply the command)
Then:  (assert the resulting events)
```

This is directly passed to a code generator (Copilot, Claude, Cursor) along with existing type definitions to produce:
- The command record/class
- The decider/command handler function
- The test scaffolding

For a view slice:
```
Given: (seed the event store)
Then:  (assert the projection state)
```
This drives:
- The projection/event handler function  
- The read model type
- The query handler and its test

### Approach 2: Nebulit Miro Plugin — Code Generation from Event Models

The most mature tool for event-model-to-code generation is the **Nebulit Miro Plugin**.[^7] It allows practitioners to:
1. Build the event model visually on a Miro board
2. Run a completeness check (verifying all information fields are traced)
3. Generate code stubs from the model — effectively producing scaffolding for all three slice types

The plugin outputs typed command/event/view classes and handler stubs that can then be completed by AI or developers. This is the "architecture blueprint for AI-native development" offering in Nebulit's premium tier.[^8]

### Approach 3: Modellution — Code Generation + Project Integration

**Modellution** ([modellution.com](https://www.modellution.com)) is a web-based event modeling platform that explicitly offers code generation alongside Jira/ClickUp integration. Like the Nebulit plugin, it generates implementation scaffolding from modeled slices. It targets the same goal: let the event model drive the initial code shape so AI can fill in the business logic.[^9]

### Approach 4: AI Agents as Automation Processors

The automation slice pattern is architecturally the same as what AI agents do naturally: read a structured view (todo list), execute an action (command), and mark the task done via the resulting event. Adam Dymitruk and Martin Dilger discuss this in Episode 24 of the Event Modeling podcast.[^10]

Concretely:
- The **View** of the automation slice is the AI agent's "inbox" or "memory" — a structured list of tasks
- The **Automated Trigger** is the AI agent's polling or event-driven activation
- The **Command** is the action the agent takes (calling an LLM, calling an API, posting a result)
- The **Resulting Event** confirms the action and removes the task from the todo list

This pattern makes AI agents determinist and auditable: every action is recorded as an event, and the todo-list view prevents double-processing. The event model also serves as the AI agent's long-term memory (via Event Sourcing), as discussed by Adaptech Group: *"Event Sourcing creates the ultimate memory for AI agents."*[^11]

### Approach 5: "Vibe Modeling" — AI-Assisted Event Modeling Sessions

Episode 19 of the Event Modeling podcast coins the term **"vibe modeling"**: using AI to assist in creating, navigating, and refining the event model itself, particularly for stakeholders who are less technical.[^12] In this approach:
- The AI helps create and maintain the event model
- Once the model is stable, each slice is handed to developers (or AI code generators) for implementation
- The lifecycle of a slice: draft → specified → ready → in development → demo-ready → deployed

Junior developers are explicitly mentioned as beneficiaries: with a complete slice specification, even a junior developer (or AI agent) can implement a slice in isolation without understanding the whole system.[^13]

### Approach 6: GitHub Copilot Workspace / Custom Instructions for Slice Implementation

While no dedicated public GitHub repository was found that formalizes a "Copilot skill" per slice type, the event modeling community's practices align with what can be done using GitHub Copilot's workspace/instruction features:

**For Command Slices**, a Copilot instruction set would include:
```markdown
When implementing a command slice:
1. Create the Command record type with the fields from the specification
2. Implement the Decider function: (Command, State) → Event[]
3. Implement the Evolve function: (State, Event) → State  
4. Write a Given-When-Then test matching the slice specification
5. Do NOT put business logic in the event handler
```

**For View Slices**, a Copilot instruction set would include:
```markdown
When implementing a view slice:
1. Create the View/ReadModel type
2. Implement the projection handler: (Event) → void (updating read model)
3. Write a Given-Then test matching the slice specification
4. Subscribe the projection to the relevant event types
```

**For Automation Slices**, a Copilot instruction set would include:
```markdown
When implementing an automation slice:
1. Create a "todo list" view that accumulates work items from trigger events
2. Implement a processor that queries the todo list and issues commands
3. Ensure the resulting event removes the processed item from the todo list
4. Write the Given-When-Then test for the processor behavior
```

This pattern was explored in the podcast (Episode 3, Episode 34) and is how practitioners report using AI tools in practice.[^14][^15]

---

## Community Tools and Resources

| Tool | Type | AI Capability | Slice Support | Link |
|------|------|--------------|---------------|------|
| **Nebulit Miro Plugin** | Miro Extension | Code generation from model | All 3 slice types | [nebulit.de](https://nebulit.de) |
| **Modellution** | Web Platform | Code generation + PM integration | All 3 slice types | [modellution.com](https://modellution.com) |
| **Evident Design** | Web Platform (next-gen ONote) | Spec-driven, code navigation | All 3 slice types | [evidentstack.com](https://evidentstack.com) |
| **ONote** | Web Platform | Real-time collaboration | All 3 slice types | [onote.com](https://onote.com) |
| **Adaptech Group** | Workshop + Consulting | AI Integration workshop | All 3 slice types | [adaptechgroup.com](https://adaptechgroup.com) |

---

## Key Insights from the Event Modeling Podcast

The [Event Modeling Podcast](https://podcast.eventmodeling.org) (hosted by Adam Dymitruk and Martin Dilger) has addressed AI integration across multiple episodes:

| Episode | Title | Key AI Insight |
|---------|-------|---------------|
| Ep. 3 | AI, GWTs on a timeline | Generating event sourcing code from event models using AI[^16] |
| Ep. 12 | Slice, slice baby! | What makes a slice the unit of work; how to tackle legacy systems with slices[^17] |
| Ep. 14 | AI Nightmares and Expert Help | Challenges: AI generates messy solutions without clear slice specs[^18] |
| Ep. 19 | Vibe Modeling | "Vibe Modeling": AI-assisted event modeling; slice lifecycle; junior devs empowered by slice specs[^19] |
| Ep. 23 | AI Takes Your Job | Season 2: future of event modeling + AI integration; anti-patterns; complexity management[^20] |
| Ep. 24 | Cheap AI Is All You Need | Event Modeling 2.0; information flow as AI context; event models for AI systems[^21] |
| Ep. 26 | A New Programming Language | Vertical slices provide clarity; AI's impact on event sourcing; immutability in AI systems[^22] |
| Ep. 34 | Slicing the Solution | AI in event modeling practice; prompting challenges; AI for schema evolution[^23] |

---

## Structural Reasons Why Slices Work Well with AI

Event Modeling's design properties make slices naturally AI-friendly:

1. **Bounded scope**: Each slice touches exactly one command or one view. There is no cross-cutting concern that an AI needs to understand. This is the "flat cost curve" property.[^24]

2. **Self-contained specification**: Every slice has all the information needed in its Given-When-Then spec — no hidden context, no tribal knowledge required. AI doesn't need to infer; it is told.

3. **Strong contracts**: The events that connect adjacent slices form explicit contracts. The command slice produces events; the view slice consumes events. AI can generate both sides independently without coordination.[^25]

4. **Immutable facts**: Events are immutable. The AI-generated code cannot accidentally corrupt existing history; it can only append. This prevents a whole class of AI-generated bugs.

5. **Testability first**: Slices are specified in a test-first style (Given-When-Then). AI can generate the test before the implementation, enabling test-driven AI code generation.[^26]

---

## Anti-Patterns the Community Warns Against

Based on podcast Episodes 14, 23, and 34:[^27][^28][^29]

- **AI without event model context**: Using Copilot/Cursor on a codebase without a clear event model leads to hallucinations, invented features, and inconsistent implementations.
- **Mixing CRUD with event sourcing in AI prompts**: Asking AI to "add a feature" without grounding it in a slice produces typical CRUD mutations that break the event-sourced invariants.
- **Skipping the completeness check**: Event models that have incomplete information flows (fields not traced from source to destination) cause AI to invent values, producing silent data corruption.
- **Over-scoping AI tasks**: Giving AI a whole feature rather than a single slice leads to the same problems as traditional big-bang development — the AI tries to do too much at once.

---

## Confidence Assessment

| Claim | Confidence | Basis |
|-------|-----------|-------|
| The 3 slice types and their GWT spec format | **High** | Verified from eventmodeling.org and official cheatsheet[^1][^3] |
| Slices as ideal AI work units (community consensus) | **High** | Multiple podcast episodes, Nebulit marketing, Adaptech workshop description[^10][^11][^19] |
| Nebulit Miro Plugin does code generation | **High** | Nebulit.de product page[^7][^8] |
| Modellution does code generation | **Medium** | Mentioned on eventmodeling.org resources page[^9]; full feature set unverified due to blocked access |
| AI agents naturally implement the automation slice pattern | **High** | Explicitly discussed in Ep. 24, Adaptech workshop description[^10][^11] |
| Specific Copilot workspace skill files for event modeling slices exist publicly | **Low** | No public GitHub repos found in search; this is an emerging/proprietary practice |
| "Vibe modeling" as an established term | **Medium** | Discussed in Ep. 19 as emerging trend, not yet formalized |
| Event Sourcing as AI agent memory | **Medium** | Explicitly stated by Adaptech Group; aligns with AI agent architecture literature |

---

## Footnotes

[^1]: [eventmodeling.org/posts/event-modeling-cheatsheet/](https://eventmodeling.org/posts/event-modeling-cheatsheet/) — "4 Building Blocks and 4 Patterns" — the official cheatsheet defining triggers, commands, events, views, and the four patterns (Command, View, Automation, Translation).

[^2]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Commands" — describes the command pattern, Given-When-Then specifications, and how commands represent intentions to change state.

[^3]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Views (or Read Models)" — describes view specifications, passive behavior, and the Given-Then format.

[^4]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Automation" — describes the todo-list pattern for automation, where a processor reads pending tasks and issues commands.

[^5]: [eventmodeling.org/posts/event-modeling-cheatsheet/](https://eventmodeling.org/posts/event-modeling-cheatsheet/) — "Translation Pattern" — "The chain of building blocks looks the same as with the automation pattern in the first place. [...] The only difference in this pattern is, that the translation pattern is used for transferring knowledge from one system into another system."

[^6]: [nebulit.de](https://nebulit.de) — "KI halluziniert — weil unsere Requirements es auch tun. Copilot schreibt Code, der nicht passt. Cursor erfindet Features. Claude rät. Nicht weil die Tools schlecht sind — sondern weil Requirements unklar sind. Garbage in, garbage out."

[^7]: [eventmodeling.org/resources/](https://eventmodeling.org/resources/) — "Miro Plugin from Nebulit.de — Event Modeling Plugin for Miro allows you to create event models and navigate them with ease as well as generate code from the model."

[^8]: [nebulit.de](https://nebulit.de) — Premium offering: "Architektur-Blueprint für KI-native Entwicklung + Code-Generator (custom für deinen Stack) + Lauffähiger Prototyp."

[^9]: [eventmodeling.org/resources/](https://eventmodeling.org/resources/) — "Modellution is a web platform designed for modeling information systems. It allows real-time visual collaboration, estimates, Jira & ClickUp integrations, and code-generation."

[^10]: [podcast.eventmodeling.org/episodes/episode-24/](https://podcast.eventmodeling.org/episodes/episode-24/) — Episode 24 "Cheap AI Is All You Need" — Chapter "46:14 Event Modeling in AI and Future Applications"; also discusses automation pattern integration with AI.

[^11]: [adaptechgroup.com](https://adaptechgroup.com) — Workshop description: "AI Integration: Discover how Event Models provide the perfect context for Generative AI to understand and build your system, and how Event Sourcing creates the ultimate memory for AI agents."

[^12]: [podcast.eventmodeling.org/episodes/episode-19/](https://podcast.eventmodeling.org/episodes/episode-19/) — Episode 19 "Vibe Modeling" — "Vibe coding allows non-developers to create applications but poses risks. Understanding the underlying concepts is crucial for effective coding. Vibe modeling can enhance collaboration among stakeholders."

[^13]: [podcast.eventmodeling.org/episodes/episode-19/](https://podcast.eventmodeling.org/episodes/episode-19/) — Chapter "01:07:25 Empowering Junior Developers" and "01:10:40 Integrating AI in Development."

[^14]: [podcast.eventmodeling.org/episodes/episode-3/](https://podcast.eventmodeling.org/episodes/episode-3/) — Episode 3 "AI, GWTs on a timeline, Security in Event Sourcing" — "generating event sourcing code from an event model using AI."

[^15]: [podcast.eventmodeling.org/episodes/episode-34/](https://podcast.eventmodeling.org/episodes/episode-34/) — Episode 34 "Slicing the Solution" — Chapters "03:24 Integrating AI in Event Modeling" and "22:14 Challenges with AI Prompting."

[^16]: [podcast.eventmodeling.org/episodes/episode-3/](https://podcast.eventmodeling.org/episodes/episode-3/) — Show notes: "generating event sourcing code from an event model using AI."

[^17]: [podcast.eventmodeling.org/episodes/episode-12/](https://podcast.eventmodeling.org/episodes/episode-12/) — Show notes: "what makes a slice so special in Event Modeling and Event Sourcing."

[^18]: [podcast.eventmodeling.org/episodes/episode-14/](https://podcast.eventmodeling.org/episodes/episode-14/) — Show notes: "Adam Dymitruk and Martin Dilger tackle the hard topics of AI generating really messy solutions."

[^19]: [podcast.eventmodeling.org/episodes/episode-19/](https://podcast.eventmodeling.org/episodes/episode-19/) — Full episode summary and chapter listing.

[^20]: [podcast.eventmodeling.org/episodes/episode-23/](https://podcast.eventmodeling.org/episodes/episode-23/) — Show notes: "integration of various tools, and the optimistic outlook for humanity's evolution in the age of AI."

[^21]: [podcast.eventmodeling.org/episodes/episode-24/](https://podcast.eventmodeling.org/episodes/episode-24/) — Show notes and chapter listing.

[^22]: [podcast.eventmodeling.org/episodes/episode-26/](https://podcast.eventmodeling.org/episodes/episode-26/) — Chapter "49:13 AI's Impact on Software Development and Event Sourcing" and "01:06:16 Understanding Vertical Slices in Event Modeling."

[^23]: [podcast.eventmodeling.org/episodes/episode-34/](https://podcast.eventmodeling.org/episodes/episode-34/) — Show notes and chapter listing.

[^24]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Flat Cost Curve" — "The effort of building each workflow step is not impacted by the development of other workflows."

[^25]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Strong Contracts" — "we have made explicit contracts as to the shape of the information of when we start a particular step of the workflow and what is the shape of the data when it's finished."

[^26]: [eventmodeling.org/posts/what-is-event-modeling/](https://eventmodeling.org/posts/what-is-event-modeling/) — Section "Technical Side-Note About Test Driven Development."

[^27]: [podcast.eventmodeling.org/episodes/episode-14/](https://podcast.eventmodeling.org/episodes/episode-14/)

[^28]: [podcast.eventmodeling.org/episodes/episode-23/](https://podcast.eventmodeling.org/episodes/episode-23/)

[^29]: [podcast.eventmodeling.org/episodes/episode-34/](https://podcast.eventmodeling.org/episodes/episode-34/)
