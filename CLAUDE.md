# CLAUDE.md — Crunchloop Senior Challenge

Guía para Claude Code (claude.ai/code) cuando trabaje en este repositorio. Este archivo combina **cómo trabajamos** (workspace) con la **guía táctica de .NET** (commands, arquitectura, testing). Las decisiones técnicas vivas en [`NOTES.md`](./NOTES.md); el contrato del spec en [`CHALLENGE.md`](./CHALLENGE.md).

## Qué es este repo

- Trabajo del **senior challenge de Crunchloop** (sync bidireccional con una API externa) — repo upstream del spec: <https://github.com/crunchloop/challenge-senior-engineer>.
- **`./CHALLENGE.md`** — spec congelado, copia del README upstream. **No editar.** Si el upstream cambia, se baja de nuevo y se discute.
- **`./NOTES.md`** — cuaderno de decisiones y tradeoffs. Append-mostly. Es el deliverable de documentación que pide el spec.
- La implementación extiende el TodoApi que se construyó para la entrevista .NET previa.

## Cómo trabajamos: slice por slice

El spec se desarrolla en **slices** (rebanadas) chicas: una capacidad o un patrón por iteración, no la feature completa de un saque. El roadmap actual de slices vive como propuesta editable al pie de NOTES.md / Decision Log.

Para cada slice:

**Antes de tocar código**

1. **Releer** la sección relevante de `CHALLENGE.md` y el `Decision Log` de `NOTES.md`. No contradecir decisiones previas sin discutirlo explícitamente con el usuario.
2. **Verbalizar la intención** antes de prompt'ear o escribir: archivo target, qué patrón se espeja del código existente, qué NO hacemos en este slice. (Ver memoria `feedback_directing_ai`: el usuario prefiere "directing AI" sobre "vibecoding" — prompts dirigidos con restricciones explícitas, no aceptar output ciego.)
3. Si el slice tiene **≥ 3 pasos no triviales**, invocar el skill `superpowers:writing-plans` y dejar el plan en `/Users/matiasromero/.claude/plans/<slug>.md`. Slices triviales (un archivo, un test) van directo.
4. Si hay **diseño abierto** (qué librería, qué patrón, qué semántica), invocar `superpowers:brainstorming` antes de proponer implementación.

**Durante la implementación**

5. Aplicar **TDD** donde tenga sentido (`superpowers:test-driven-development`). El repo ya tiene patrón claro de xUnit + InMemory documentado abajo; espejarlo.
6. Cambios chicos y dirigidos: un archivo o una capa por turno. Nada de "feature completa one-shot".
7. Mostrar el diff explícito y llamar la atención sobre judgment calls: "usé X porque Y, alternativa Z descartada porque…".

**Cuando el slice cierra**

8. Verificar con evidencia (`superpowers:verification-before-completion`):
   - `dotnet test` — todos verde.
   - `dotnet csharpier --check .` — no formatting drift (el CI del repo upstream falla si formatea mal).
   - Output a la vista antes de declarar éxito.
9. Apendear al **Decision Log de `NOTES.md`** una entrada con: fecha, slice, decisión, alternativas descartadas, por qué, supuestos nuevos, deuda anotada. Si el slice tocó alguna sección formal (Resilience, Edge Cases, Assumptions), actualizar también esa sección.
10. Solo después: commit.

## Reglas duras

- **`CHALLENGE.md` es inmutable.** Es el contrato upstream. No se edita ni siquiera para corregir typos: si está mal, se discute y se documenta el desvío en `NOTES.md`.
- **`NOTES.md` es append-mostly.** Editar entradas pasadas solo para corregir errores factuales. Cambios de criterio se registran como entrada nueva con `**Supersedes:** YYYY-MM-DD <título>`.

## Idioma

- Discusión técnica y conceptual: **castellano argentino**.
- Código, identificadores, comandos, nombres de archivos, mensajes de commit: **inglés natural**.

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

## Skills relevantes (referencia rápida)

- `superpowers:brainstorming` — antes de cualquier diseño abierto.
- `superpowers:writing-plans` — slices con ≥ 3 pasos no triviales.
- `superpowers:test-driven-development` — implementación.
- `superpowers:verification-before-completion` — antes de declarar un slice cerrado.
- `superpowers:executing-plans` — si un slice tiene plan formal y se ejecuta en sesión separada.
