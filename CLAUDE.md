# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

All commands run from the `dotnet-interview/` directory.

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
