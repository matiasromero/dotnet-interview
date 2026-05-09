# NOTES — Crunchloop Senior Challenge

Cuaderno de decisiones, tradeoffs y supuestos del trabajo sobre [`CHALLENGE.md`](./CHALLENGE.md). Este archivo es el deliverable de documentación que pide el spec.

**Convención:** las secciones formales (Overview → Assumptions) son el documento que se entrega al final; el **Decision Log** al pie es append-only y captura el "por qué" de cada slice mientras se trabaja. Una decisión nueva primero se anota en el Decision Log; cuando se confirma que es la postura final del proyecto, se promueve/sintetiza en la sección formal correspondiente.

---

## High-Level Overview

_(se llena cuando el approach esté concreto — al menos después del slice 1-2)_

## Key Design Decisions

_(síntesis de las decisiones más cargadas. Formato sugerido por entrada:)_

| Decisión | Alternativas descartadas | Por qué | Trade-off aceptado | Slice |
|---|---|---|---|---|

## Resilience and Error Handling

_(retries, backoff, circuit breaker, partial-failure semantics, idempotencia. Vacío hasta el slice de resilience.)_

## Edge Cases

_(checklist de edge cases identificados y cómo se manejan o por qué se ignoran. Se va llenando.)_

- [ ] _ejemplo: ¿qué pasa si la API externa devuelve un TodoList sin items?_

## Areas for Improvement

_(lo que queda fuera de scope pero conviene anotar para el reviewer.)_

## Assumptions

_(supuestos explícitos sobre la API externa, semántica de delete, conflictos, etc. Cada supuesto debería poder responder "¿qué se rompe si esto no es cierto?".)_

---

## Decision Log

_Cronológico, append-only. Una entrada por slice cerrado o por decisión cargada que justifique el registro. Cuando una entrada queda obsoleta, no se borra: se agrega una entrada nueva con `**Supersedes:** YYYY-MM-DD <título>`._

### Plantilla

```
### YYYY-MM-DD — Slice N: <título>
- **Decisión:**
- **Alternativas descartadas:**
- **Por qué:**
- **Supuestos nuevos:**
- **Deuda / follow-ups:**
```

---

### 2026-05-08 — Slice 0: Setup del workspace

- **Decisión:** spec congelado en `CHALLENGE.md` (no editable), NOTES.md y CLAUDE.md en el root del workspace; implementación va a extender `dotnet-interview/` (el TodoApi de la entrevista previa).
- **Alternativas descartadas:**
  - Carpeta sibling nueva (`senior-challenge/`) — descartada para reusar TodoApi/EF/xUnit ya armados; el spec dice explícito "enhancing an existing Todo API".
  - Clonar `crunchloop/challenge-senior-engineer` — descartada porque el upstream solo tiene README + `docs/` (sin starter code), no aporta nada que no podamos bajar a demanda.
  - Crear un skill nuevo para el flujo "spec → implement → document" — descartada por ahora; los skills genéricos (`brainstorming`, `writing-plans`, `test-driven-development`, `verification-before-completion`) cubren la cadencia, y este flujo es project-specific. Se promueve a skill si reaparece en otros challenges.
- **Por qué:** la decisión clave es separar **contrato** (CHALLENGE.md, inmutable) de **estado vivo** (NOTES.md, append-mostly) de **proceso** (CLAUDE.md, instrucciones para Claude). Permite que cualquier sesión futura levante el contexto sin re-explicar.
- **Supuestos nuevos:**
  - El spec upstream no va a cambiar durante el desarrollo. Si cambia, se vuelve a bajar y se discute.
  - El root del workspace **no es un git repo** (`Is a git repository: false`). Pendiente: decidir si se inicializa uno propio o si los commits viven dentro de `dotnet-interview/`.
- **Deuda / follow-ups:**
  - Bajar `docs/` del upstream (contrato OpenAPI) cuando arranque el slice 1.
  - Decidir estrategia de versionado del workspace (root como repo nuevo vs. solo `dotnet-interview/`).

### 2026-05-09 — Slice 1: Sync engine scaffolding + PUSH de TodoLists

- **Decisión:** sync engine modelado como class library `TodoApi.Sync` referenciada desde `TodoApi`, con tres etapas explícitas — `SyncBackgroundService` (trigger), `TodoListSyncService` (lógica), `IExternalTodoListClient` typed HttpClient (cliente). Persistencia en `TodoContext` con dos tablas nuevas (`SyncMapping`, `SyncRun`) expuestas vía interface `ISyncDbContext`. Resilience con `Microsoft.Extensions.Http.Resilience` (Polly v8 oficial): retry exponencial + jitter + circuit breaker + timeout per-attempt. Configuración tipada con `IOptions<ExternalApiOptions>` y `IOptions<SyncOptions>`. Slice 1 cubre solo PUSH de TodoLists local→externo (sin items, sin pull, sin updates/deletes).
- **Alternativas descartadas:**
  - Worker SDK separado — más prod-ready pero duplica config y orquestación; el challenge se ejecuta en un proceso.
  - `ExternalId` inline en `TodoList` — acoplaría el dominio al concern de sync; la tabla `SyncMapping` mantiene el desacoplamiento y soporta multi-target en el futuro.
  - Polly directo (paquete `Polly` + `Microsoft.Extensions.Http.Polly`) — `Microsoft.Extensions.Http.Resilience` es la línea oficial de Microsoft post-Polly v8, con integración nativa a `IHttpClientFactory`.
  - `((DbContext)_db).Set<TodoApi.Models.TodoList>()` desde el sync service — descartada al detectar que requería que `TodoApi.Sync` referenciara `TodoApi`, creando dependencia circular (`TodoApi` ya referencia al sync project). Reemplazada por método específico `ISyncDbContext.GetUnmappedTodoListsAsync(CancellationToken)` que proyecta a un `LocalTodoListRecord` en `TodoApi.Sync.Models` — la query usa anti-join server-side (`!SyncMappings.Any(...)`) en vez de `!mappedIds.Contains(...)`, evitando el límite de 2100 parámetros de SQL Server.
  - Save por batch (commit al final del run) — peor blast radius en crash mid-run (dups externos sin mapping local). Save por lista da idempotencia simple.
- **Por qué:** la decisión cardinal es **desacoplar**. El user explícitamente lo pidió: el sync no debe contaminar `TodoListService` ni `TodoListItemService`. Logramos ese desacoplamiento con (a) proyecto separado, (b) interface `ISyncDbContext` minimalista con un método específico para la query del push, (c) tabla de mapping en lugar de inline. Polly + IHttpClientFactory típico para evitar agotamiento de sockets sin singleton.
- **Supuestos nuevos:**
  - El `source_id` del externo se usa como correlation key bidireccional — se manda nuestro local Id en cada push, y en pulls futuros podemos detectar entries originados localmente sin tabla mágica.
  - `db.Database.Migrate()` solo en Development (comportamiento heredado del repo). En Production hay que aplicar migrations out-of-band antes de levantar la app, o el sync explota al primer save.
  - El typed HttpClient usa URLs relative (`PostAsJsonAsync("todolists", ...)`). El `BaseAddress` viene de config; los defaults (`http://localhost:8080`) cubren el caso de docker compose con la API externa local.
  - InMemory provider no enforza unique indices — los tests no detectan colisiones de mapping. La migration en SqlServer real sí los enforza.
  - `ExternalId nvarchar(64)` cubre UUIDs (36 chars) y la mayoría de IDs de string razonables. Asumimos que el externo no devuelve IDs largos (URL-based, claves compuestas). Si pasa, la insert falla en SqlServer con error de truncation — se vería en logs de sync.
- **Deuda / follow-ups:**
  - PULL externo→local con reconciliación por `updated_at` (slice 2).
  - Sync de `TodoListItem` — la spec externa no expone POST aislado de items (solo PATCH/DELETE individuales); merece slice propio para resolver el workaround.
  - DELETE / UPDATE bidireccional + conflict resolution policy (slice 3+).
  - Outbox pattern para garantía exactly-once en push (riesgo actual: crash entre `client.Create` y save de mapping → duplicado externo).
  - Telemetría / métricas de sync runs (Prometheus exporter o similar). Hoy solo logs estructurados.
  - Endpoint `POST /api/sync/run` para trigger manual (útil en desarrollo).
  - Documentar en README cómo levantar la API externa para verificación end-to-end real (docker compose con el repo upstream `crunchloop/challenge-senior-engineer`).
  - Sellar tipos con `sealed` donde aplique (ya hecho en `ExternalApiException`, `SyncBackgroundService`).
  - El plan original asumía `((DbContext)_db).Set<TodoList>()`; el plan vivo (este Decision Log) registra que la implementación final usa un método dedicado en la interface — para cualquier futuro slice que extienda el sync, el patrón a seguir es agregar métodos específicos al `ISyncDbContext` en lugar de filtrar la query del lado del service con tipos importados.
