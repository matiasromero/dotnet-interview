# NOTES — Crunchloop Senior Challenge

Cuaderno de decisiones, tradeoffs y supuestos del trabajo sobre [`CHALLENGE.md`](./CHALLENGE.md). Este archivo es el deliverable de documentación que pide el spec.

**Convención:** las secciones formales (Overview → Assumptions) son el documento que se entrega al final; el **Decision Log** al pie es append-only y captura el "por qué" de cada slice mientras se trabaja. Una decisión nueva primero se anota en el Decision Log; cuando se confirma que es la postura final del proyecto, se promueve/sintetiza en la sección formal correspondiente.

---

## High-Level Overview

El sync engine vive como class library separada (`TodoApi.Sync`) referenciada desde `TodoApi`, con tres capas explícitas:

1. **Trigger** — `SyncBackgroundService : BackgroundService` corre en el host de la API. Cada `Sync:Interval` (default 60s, `IOptionsMonitor` para reload) abre un scope de DI fresco, resuelve el sync service, ejecuta una corrida y loguea totales. Top-level catch evita que un tick fallido mate el host.
2. **Lógica** — `TodoListSyncService` es scoped, depende de `ISyncDbContext` (interface mínima del sync project) + `IExternalTodoListClient` + `ILogger<T>`. Una corrida persiste un `SyncRun` Running, busca candidatos vía anti-join server-side, los pushea uno a uno (save por lista) y cierra el `SyncRun` con status agregado.
3. **Cliente** — `IExternalTodoListClient` typed HttpClient registrado vía `IHttpClientFactory` (evita socket exhaustion sin singleton). Decorated con un pipeline de `Microsoft.Extensions.Http.Resilience` (Polly v8): retry exponencial + jitter, circuit breaker, timeout per-attempt. JSON snake_case via `JsonNamingPolicy.SnakeCaseLower`.

Persistencia: dos tablas nuevas (`SyncMappings`, `SyncRuns`) en el `TodoContext` existente, expuestas al sync project mediante `ISyncDbContext` con un método específico `GetUnmappedTodoListsAsync(CancellationToken)` que proyecta a `LocalTodoListRecord`. Ese diseño evita que `TodoApi.Sync` referencie `TodoApi.Models` (que crearía dependencia circular: `TodoApi → TodoApi.Sync` ya existe).

Configuración tipada con `IOptions<ExternalApiOptions>` (DataAnnotations + `ValidateOnStart` para fail-fast en BaseAddress mal formado, retry/timeout fuera de rango) y `IOptions<SyncOptions>` (sin annotations — defaults sanos).

**Slice 1 entrega solo PUSH de TodoLists local→externo.** PULL, items, deletes y updates quedan para slices siguientes (ver Areas for Improvement).

## Key Design Decisions

| Decisión | Alternativas descartadas | Por qué | Trade-off aceptado | Slice |
|---|---|---|---|---|
| Class library `TodoApi.Sync` separada, hosted en TodoApi | Worker SDK (proceso aparte); merge inline en TodoApi | Desacoplamiento del concern de sync sin duplicar config/host. El usuario lo pidió explícito. | Una sola unidad de despliegue (acceptable para challenge); deploys del sync acoplan a deploys de la API. | 1 |
| Tablas `SyncMapping` + `SyncRun` en `TodoContext` existente | DbContext aparte; ExternalId inline en TodoList | Una migration única, transacciones simples. Mantiene el dominio (`TodoList`) libre del concern de sync. | Un join extra en read-paths del sync (no es hot path). | 1 |
| `ISyncDbContext` con método específico `GetUnmappedTodoListsAsync` | `((DbContext)_db).Set<TodoList>()` con cast en el service | Dependencia circular (`TodoApi.Sync` no puede referenciar `TodoApi.Models`). Server-side anti-join evita el límite de 2100 parámetros de SQL Server. | Cada nueva entidad sincronizable agrega un método a la interface (escala razonable para 2-3 entidades). | 1 |
| `Microsoft.Extensions.Http.Resilience` (Polly v8 oficial) | `Polly` + `Microsoft.Extensions.Http.Polly` (legacy v7) | Línea oficial de Microsoft post-Polly v8, integración nativa con `IHttpClientFactory`, telemetría built-in. | Una capa de abstracción más; menos código a cambio. | 1 |
| Save por lista (no batch) en el push loop | Batch al final del run | Idempotencia simple: un crash mid-batch deja mappings ya escritos intactos, el siguiente run no re-pushea esos. | Más round-trips a DB (irrelevante para volúmenes esperados de listas nuevas por tick). | 1 |
| `source_id` como correlation key bidireccional | Tabla de mapping mágica con UUIDs internos | El externo expone `source_id` explícito en el contrato. Push manda `local.Id.ToString()`; futuros pulls pueden detectar entries originados localmente sin lookup adicional. | Acopla el contrato externo al schema local de IDs (long stringificado). | 1 |

## Resilience and Error Handling

**Pipeline (registered in `SyncServiceCollectionExtensions.AddTodoSync`, orden outer→inner):**

1. **Retry** (outer) — `MaxRetryAttempts = ExternalApiOptions.RetryMaxAttempts` (default 3), `BackoffType.Exponential`, `UseJitter = true`, `Delay = 1s`. Reintenta `HttpRequestException`, `TimeoutRejectedException`, y respuestas `>= 500 || 408 || 429`. NO reintenta 4xx genéricos (la API local sabe pedir bien o no, no tiene sentido retry).
2. **CircuitBreaker** (middle) — `FailureRatio = 0.5`, `SamplingDuration = 30s`, `MinimumThroughput = 5`, `BreakDuration = 30s`. Si la API externa se cae sostenidamente, el sync deja de gastar retries; auto-recuperación a los 30s.
3. **Timeout** (inner, per-attempt) — `ExternalApiOptions.PerAttemptTimeoutSeconds` (default 10s). Aplica por intento individual, no por pipeline — un retry tras timeout obtiene su propia ventana fresca.

El orden importa: si Timeout fuera outer, los retries compartirían una sola ventana global y el primer reintento podría ya estar fuera de tiempo.

**Partial-failure semantics del run:**

- Por cada `TodoList` candidato, el push hace try/catch independiente. Una falla individual incrementa el contador `failed`, loguea Warning, y continúa con el siguiente.
- Status final del `SyncRun`:
  - `failed == 0` → `Succeeded`.
  - `pushed == 0 && failed > 0` → `Failed`.
  - Otherwise → `Partial`.
- El `SyncRunResult` que devuelve el service refleja los mismos contadores.

**Idempotencia:**

- El query de candidatos (`GetUnmappedTodoListsAsync`) usa anti-join contra `SyncMappings`: solo considera TodoLists sin mapping. Lists ya synced se skipean en runs subsiguientes — verificado por el test `PushTodoListsAsync_WithExistingMapping_OnlyPushesUnmapped`.
- Save por lista: cada mapping se persiste inmediatamente después de su `client.Create`. Si el proceso muere a mitad de un batch, las listas ya synceadas tienen su mapping y no se re-pushean.

**Gap conocido:** entre `client.CreateTodoListAsync` (éxito) y `_db.SaveChangesAsync` (mapping save) hay una ventana donde un crash deja un duplicado externo sin mapping local. El siguiente run lo re-pushearía. Mitigación correcta: outbox pattern, fuera de scope para slice 1. Cuando se implemente PULL (slice 2), la reconciliación por `source_id` adoptará estos huérfanos automáticamente.

**Logging:**

- Structured logging con placeholders (`{LocalId}`, `{ExternalId}`, `{Total}`, `{Pushed}`, `{Failed}`, `{Status}`) — consistente con el patrón del repo (ver `TodoListService`).
- Niveles: Info para success de cada push y para resumen del tick; Warning para fallas individuales; Error para excepciones que se escapan del try/catch del loop (último resort, no debería ocurrir); Info para `Sync:Enabled = false`.

## Edge Cases

- [x] **TodoList sin items** — slice 1 mandamos `Items = Array.Empty<...>()` siempre (items son slice futuro). Acceptable para el push initial; el item sync recobra los items existentes cuando se implemente.
- [x] **TodoList ya mapeado** — anti-join lo excluye. No se llama al externo.
- [x] **Falla parcial (1 de N)** — status `Partial`, mappings de las exitosas persisten. Test `PushTodoListsAsync_OneOfThreeFails_StatusPartialAndOthersMapped`.
- [x] **Falla total (N de N)** — status `Failed`, ningún mapping nuevo. Test `PushTodoListsAsync_AllFail_StatusFailed`.
- [x] **No hay candidatos (DB vacía o todo mapeado)** — `SyncRun` se persiste con `Succeeded` + 0 counts. Test `PushTodoListsAsync_NoLocalLists_ReturnsZeroAndSucceeded`.
- [x] **Sync deshabilitado por config** — `SyncBackgroundService.ExecuteAsync` retorna inmediato si `SyncOptions.Enabled = false`. Test smoke verifica start/stop limpio.
- [x] **API externa devuelve body vacío en 2xx** — el client lanza `ExternalApiException` con mensaje "POST todolists returned empty body".
- [x] **5xx / 408 / 429** — Polly retry. Si el circuit breaker abre, los siguientes ticks fallan rápido hasta que cierre.
- [x] **4xx genérico** — el client lanza `ExternalApiException`; el service la captura como falla individual. NO se reintenta (4xx genéricos no son transient).
- [ ] **External devuelve un `id` > 64 chars** — la column `ExternalId` es `nvarchar(64)`. La insert fallaría en SqlServer real. No documentamos length en el contrato externo; asumimos UUIDs (~36 chars). Mitigation futura: subir el límite o fallar explícito al validar el response. Riesgo bajo para el challenge.
- [ ] **Concurrencia entre dos hosts del API corriendo simultáneamente** — el sync correría 2x sobre las mismas listas. Mappings tienen unique index `(EntityType, LocalId)`, así que el segundo perdería con DbUpdateException — no estamos manejándola, runs concurrentes se pisan. Single-instance assumption por ahora.
- [ ] **External "deleted" un list que tenemos mapeado** — slice 1 no consulta el externo, no se entera. Slice 2 (PULL) deberá decidir conflict resolution.

## Areas for Improvement

Roadmap explícito de slices futuros y deuda técnica que sale del slice 1:

- **Slice 2 — PULL externo→local + reconciliación.** Adopt orphans creados externamente, detectar deletes por ausencia, conflict resolution por `updated_at`. Este slice también va a usar `source_id` para detectar entries que ya tenemos mapeados sin doble-creación.
- **Slice 3 — Sync de `TodoListItem`.** El contrato externo no expone POST aislado de items; solo se crean dentro del POST inicial del list, o vía PATCH/DELETE individuales. Workaround a definir (re-POST list + items, o tracking más fino). Merece slice propio.
- **Slice 4 — DELETE / UPDATE bidireccional.** Necesita política de conflict resolution (last-writer-wins por `updated_at`, soft-delete con tombstones, etc.).
- **Outbox pattern** para garantía exactly-once en push (eliminar el gap de duplicado externo en crash mid-write).
- **Telemetría / métricas** de runs (Prometheus exporter, tracing, etc.). Hoy solo logs estructurados.
- **Endpoint manual `POST /api/sync/run`** para trigger on-demand (útil en dev y debugging).
- **README** con instrucciones para levantar la API externa (docker compose con el repo upstream `crunchloop/challenge-senior-engineer`) y verificar end-to-end.
- **`SyncRun.Error`** — la column existe pero no se escribe; el catch del loop solo loguea. Populate con un resumen agregado (último error, o concatenación de N) cuando se necesite forensics offline.
- **`ExternalApiException.Body` sin cap** — si el externo devuelve un HTML grande en 5xx, alocamos todo en memoria. Cap razonable (~2KB) en el client.
- **`SyncOptions` sin validación** — defaults sanos, pero un `Interval = 0` o `StartupDelay` negativo serían pathológicos. Custom validator en slice futuro.
- **Single-instance assumption** del background service — multiples hosts corriendo el mismo proceso ejecutarían el sync en paralelo, con condiciones de carrera. Locking distribuido o leader election fuera de scope.
- **`db.Database.Migrate()` solo en Development** — heredado del repo. En Production hay que aplicar migrations out-of-band antes de levantar la app, o el primer save del sync explota.

## Assumptions

Supuestos explícitos del slice 1 — cada uno responde "¿qué se rompe si no es cierto?".

- **El `source_id` del externo es preservado verbatim.** Si el externo lo modifica/normaliza, perdemos correlación bidireccional y los pulls futuros (slice 2) crean duplicados locales en vez de detectar entries originados aquí.
- **Los IDs externos son strings de hasta 64 chars.** Si el externo devuelve algo más largo (URL-based, claves compuestas, etc.), las inserts fallan en SqlServer con error de truncation (visible en logs del sync). Defaults UUIDs (36 chars) — alcanza con margen.
- **El externo no tiene rate limiting agresivo, ni auth.** Spec dice "All endpoints do not require authorization". Si esto cambia, el typed client necesita interceptors para tokens; el pipeline ya maneja 429 con retry.
- **Las TodoLists locales son creadas SOLO por la API local (no por otra fuente).** El push asume que cualquier list sin mapping es candidato a sincronizar. Si en el futuro hay una segunda fuente de creación, necesitamos un flag para distinguir.
- **Single-instance del host.** El background service no tiene leader election ni locking distribuido. Múltiples instancias corriendo en paralelo intentarían pushear las mismas listas; el unique index en `SyncMappings` previene corrupción pero la segunda instancia perdería con `DbUpdateException` no manejada.
- **`Sync:Interval = 60s` es razonable** para los volúmenes esperados (listas creadas por interfaces de usuario, no por batch). Si el throughput sube significativamente, considerar interval más bajo o trigger por evento (slice futuro).
- **El base address del externo termina sin slash final** (default `http://localhost:8080`). El typed client compone con path relativo `"todolists"`. Si alguien configura `BaseAddress = "http://localhost:8080/api"` (con segmento de path), el resolve concatena mal y los requests salen a `http://localhost:8080/todolists` perdiendo `/api`. Mitigación: documentar en el README o validar en `ExternalApiOptions`. Riesgo bajo en el challenge porque el spec usa root.

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
