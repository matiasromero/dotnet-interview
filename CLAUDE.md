# CLAUDE.md — Crunchloop Senior Challenge

Guide for Claude Code (claude.ai/code) when working in this repository. This file combines **how we work** (workspace) with the **.NET tactical guide** (commands, architecture, testing). Live technical decisions are in [`NOTES.md`](./NOTES.md); the spec contract is in [`CHALLENGE.md`](./CHALLENGE.md).

## What this repo is

- Work for the **Crunchloop senior challenge** (bidirectional sync with an external API) — upstream repo of the spec: <https://github.com/crunchloop/challenge-senior-engineer>.
- **`./CHALLENGE.md`** — frozen spec, copy of the upstream README. **Do not edit.** If the upstream changes, pull it down again and discuss.
- **`./NOTES.md`** — decision log and tradeoffs. Append-mostly. It is the documentation deliverable required by the spec.
- The implementation extends the TodoApi built for the previous .NET interview.

## How we work: slice by slice

The spec is built out in small **slices**: one capability or one pattern per iteration, not the complete feature in one shot. The current slice roadmap lives as an editable proposal at the bottom of NOTES.md / Decision Log.

For each slice:

**Before touching code**

1. **Re-read** the relevant section of `CHALLENGE.md` and the `Decision Log` in `NOTES.md`. Do not contradict prior decisions without explicitly discussing it with the user.
2. **Verbalize the intent** before prompting or writing: target file, which pattern mirrors existing code, what we are NOT doing in this slice. (See memory `feedback_directing_ai`: the user prefers "directing AI" over "vibecoding" — directed prompts with explicit constraints, not blind output acceptance.)
3. If the slice has **≥ 3 non-trivial steps**, invoke the `superpowers:writing-plans` skill and leave the plan at `/Users/matiasromero/.claude/plans/<slug>.md`. Trivial slices (one file, one test) go straight in.
4. If there is **open design** (which library, which pattern, which semantics), invoke `superpowers:brainstorming` before proposing implementation.

**During implementation**

5. Apply **TDD** where it makes sense (`superpowers:test-driven-development`). The repo already has a clear xUnit + InMemory pattern documented below; mirror it.
6. Small, directed changes: one file or one layer per turn. No "one-shot complete feature".
7. Show the explicit diff and call out judgment calls: "I used X because Y, alternative Z discarded because…".

**When the slice closes**

8. Verify with evidence (`superpowers:verification-before-completion`):
   - `dotnet test` — all green.
   - `dotnet csharpier --check .` — no formatting drift (the upstream repo's CI fails on bad formatting).
   - Output visible before declaring success.
9. Append to the **Decision Log in `NOTES.md`** an entry with: date, slice, decision, alternatives discarded, why, new assumptions, debt noted. If the slice touched any formal section (Resilience, Edge Cases, Assumptions), update that section as well.
10. Only then: commit.

## Hard rules

- **`CHALLENGE.md` is immutable.** It is the upstream contract. Not edited even to fix typos: if it is wrong, discuss and document the deviation in `NOTES.md`.
- **`NOTES.md` is append-mostly.** Edit past entries only to fix factual errors. Changes of judgment are recorded as a new entry with `**Supersedes:** YYYY-MM-DD <title>`.

## Language

- Project documentation (`CLAUDE.md`, `NOTES.md`, `diagrams/*`) and code (identifiers, commands, file names, commit messages): **English**.
- Conversational discussion with the user: **Argentine Spanish** (user preference).

## Commands

All commands run from this directory (the project root).

- Build: `dotnet build`
- Run the API (dev): `dotnet run --project TodoApi` — Swagger UI at `/swagger`, EF migrations are applied automatically on startup in Development.
- Run all tests: `dotnet test`
- Run a single test: `dotnet test --filter "FullyQualifiedName~TodoListServiceTests.GetByIdAsync_WhenCalled_ReturnsTodoListById"` (filter accepts class name, method name, or namespace fragments).
- Restore local tools (csharpier, dotnet-ef) before first use: `dotnet tool restore`.
- Format check (matches CI): `dotnet csharpier --check .` — CI fails if formatting drifts. Apply formatting with `dotnet csharpier .`.
- Add an EF migration: `dotnet ef migrations add <Name> --project TodoApi`.
- Apply migrations manually: `dotnet ef database update --project TodoApi`.

## Architecture

### Layers
Controller → Service (interface-based, DI-registered as scoped) → `TodoContext` (EF Core). Controllers are deliberately thin: they translate `bool`/`null` returns from services into HTTP status codes (`NotFound`, `NoContent`, `CreatedAtAction`). All business logic and DB access lives in services.

### Nested resource convention (TodoList → TodoListItem)
`TodoListItem` is a child of `TodoList` with cascade delete (configured in `TodoContext.OnModelCreating`). The route is `api/todolists/{todoListId}/items[/{id}]`.

Every method in `TodoListItemService` first verifies the parent `TodoList` exists via `AnyAsync`, and returns `null` (for queries / create) or `false` (for update / delete) if the parent is missing — the controller maps that to 404. **When adding new child operations, follow the same pattern: parent-existence check before any item-level work.** This is why service signatures take `todoListId` as the first parameter even when the item `Id` would be globally unique.

### Validation
FluentValidation runs automatically on POST/PUT bodies via `SharpGrip.FluentValidation.AutoValidation.Mvc` (wired in `Program.cs` with `AddFluentValidationAutoValidation()` + `AddValidatorsFromAssemblyContaining<Program>()`). To validate a new DTO, drop a `class XValidator : AbstractValidator<X>` into `TodoApi/Validators/` — no manual registration required. Controllers do not call validators directly; a 400 is returned automatically when rules fail.

### DTOs vs Models
`Dtos/` holds the request shapes (`CreateTodoList`, `UpdateTodoListItem`, etc.) — never accept or return the EF model directly from controllers when creating/updating. `Models/` are the EF entities and may be returned on GET responses.

### Migrations on startup
`Program.cs` calls `db.Database.Migrate()` only in Development. In other environments, migrations must be applied out-of-band. This means dev resets cleanly by recreating the database, but production-style runs need explicit `dotnet ef database update`.

### Database
SQL Server 2022 via the devcontainer (`.devcontainer/docker-compose.yml`). The connection string lives in `TodoApi/appsettings.Development.json`, which is git-ignored — each developer keeps their own credentials locally. The compose file uses `sa/Password123`; the local appsettings may differ. If running outside the devcontainer, provision SQL Server yourself and update the connection string.

## Testing patterns

- xUnit with `Microsoft.EntityFrameworkCore.InMemory` — every test builds its own `DbContext` with `UseInMemoryDatabase(Guid.NewGuid().ToString())`, so tests are isolated and parallel-safe with no shared state.
- Tests instantiate services and controllers directly (no `WebApplicationFactory`, no HTTP); use `NullLogger<T>.Instance` for the logger dependency.
- A `PopulateDatabaseContext` helper at the top of each test class seeds fixtures with hardcoded IDs (1, 2, 3…) — follow the same shape when adding tests so other readers can scan quickly.
- The InMemory provider does **not** enforce relational constraints (cascade delete, FK validation), so exercise parent/child semantics through service code, not by relying on the DB to refuse bad writes.

## Visual artifacts (diagrams, HTML explainers)

When a diagram, visual flow, or self-contained HTML explainer is requested for project concepts (sync engine, lifecycles, architecture), follow the system defined in [`diagrams/STYLE.md`](./diagrams/STYLE.md): aesthetic **"Terminal Schematic"** (dark CRT, IBM Plex Mono/Sans, amber+cyan palette), reusable patterns (boxes, connectors, tick boundary, Y-split, code snippets) and AI conventions (double TL;DR, heading `> §NN Title`, footer with refs). Living reference: [`diagrams/outbox-syncmapping-flow.html`](./diagrams/outbox-syncmapping-flow.html). If tokens are changed, update `STYLE.md` first, then regenerate.

## Relevant skills (quick reference)

- `superpowers:brainstorming` — before any open design.
- `superpowers:writing-plans` — slices with ≥ 3 non-trivial steps.
- `superpowers:test-driven-development` — implementation.
- `superpowers:verification-before-completion` — before declaring a slice closed.
- `superpowers:executing-plans` — if a slice has a formal plan and is executed in a separate session.
