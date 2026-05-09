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

**Slice 2 agrega PULL externo→local + reconciliación bidireccional para TodoLists.** Cada tick del background service ahora ejecuta `PushTodoListsAsync` y después `PullTodoListsAsync` en el mismo scope (try/catch independientes, una falla no impide la otra). El push agrega un `Guid IdempotencyKey` por intent: se manda como header `Idempotency-Key` en el POST y se persiste en `SyncMapping.IdempotencyKey` (unique). El pull hace `GET /todolists` (full scan, sin filtros server-side disponibles) y para cada entry decide entre tres casos: (A) ya mapeado → reconcile last-write-wins comparando `local.UpdatedAt` vs `external.updated_at` (tie-break al externo); (B) `source_id` parseable apuntando a un local sin mapping → ADOPTION (cierra el gap del crash mid-write de slice 1); (C) ninguno de los anteriores → crear `TodoList` local con `UpdatedAt = external.updated_at`. Items quedan fuera del slice (slice 3); deletes externos quedan fuera (slice 4).

**Slice 3 agrega sync bidireccional de `TodoListItem` resolviendo la asimetría del contrato externo.** El contrato (verificado en `assets/external-api.yaml`) expone `PATCH /todolists/{listId}/todoitems/{itemId}` y `DELETE /todolists/{listId}/todoitems/{itemId}`, pero **no expone POST aislado de items**: items solo se crean externamente embebidos en el `POST /todolists` inicial. El slice cubre: (1) `TodoListItem.UpdatedAt` (espejo del slice 2 en `TodoList`), seteado por `TodoListItemService` en Create/Update; (2) modificación del push de slice 1 para embebir `local.Items` en el POST inicial y persistir mappings de items vía `PersistEmbeddedItemMappingsAsync`; (3) `TodoListItemSyncService.PushTodoListItemsAsync` → PATCH local→external para items mapeados que cambiaron + DELETE local→external de mapping huérfano (LocalId sin TodoListItem correspondiente, anti-join) + log Warning para items locales nuevos en lista ya pusheada (no-sync, limitación documentada del contrato); (4) `PullTodoListItemsAsync` que recibe los `ExternalListWithMapping` del list pull y aplica LWW (CASO A), adoption por `source_id` (CASO B) o create local (CASO C) por item. `SyncMapping` gana columna `ParentExternalId nvarchar(64) NULL` para que el path padre sobreviva al hard-delete del item local. El `SyncBackgroundService` orquesta 4 fases por tick: list push → item push → list pull (devuelve tupla con mapped externals) → item pull. Re-parenting fuera de scope; deletes externos detectados por desaparición del GET quedan para slice 4 (delete bidireccional unificado).

## Key Design Decisions

| Decisión | Alternativas descartadas | Por qué | Trade-off aceptado | Slice |
|---|---|---|---|---|
| Class library `TodoApi.Sync` separada, hosted en TodoApi | Worker SDK (proceso aparte); merge inline en TodoApi | Desacoplamiento del concern de sync sin duplicar config/host. El usuario lo pidió explícito. | Una sola unidad de despliegue (acceptable para challenge); deploys del sync acoplan a deploys de la API. | 1 |
| Tablas `SyncMapping` + `SyncRun` en `TodoContext` existente | DbContext aparte; ExternalId inline en TodoList | Una migration única, transacciones simples. Mantiene el dominio (`TodoList`) libre del concern de sync. | Un join extra en read-paths del sync (no es hot path). | 1 |
| `ISyncDbContext` con método específico `GetUnmappedTodoListsAsync` | `((DbContext)_db).Set<TodoList>()` con cast en el service | Dependencia circular (`TodoApi.Sync` no puede referenciar `TodoApi.Models`). Server-side anti-join evita el límite de 2100 parámetros de SQL Server. | Cada nueva entidad sincronizable agrega un método a la interface (escala razonable para 2-3 entidades). | 1 |
| `Microsoft.Extensions.Http.Resilience` (Polly v8 oficial) | `Polly` + `Microsoft.Extensions.Http.Polly` (legacy v7) | Línea oficial de Microsoft post-Polly v8, integración nativa con `IHttpClientFactory`, telemetría built-in. | Una capa de abstracción más; menos código a cambio. | 1 |
| Save por lista (no batch) en el push loop | Batch al final del run | Idempotencia simple: un crash mid-batch deja mappings ya escritos intactos, el siguiente run no re-pushea esos. | Más round-trips a DB (irrelevante para volúmenes esperados de listas nuevas por tick). | 1 |
| `source_id` como correlation key bidireccional | Tabla de mapping mágica con UUIDs internos | El externo expone `source_id` explícito en el contrato. Push manda `local.Id.ToString()`; futuros pulls pueden detectar entries originados localmente sin lookup adicional. | Acopla el contrato externo al schema local de IDs (long stringificado). | 1 |
| `Idempotency-Key` header + columna en `SyncMapping` + adoption en pull | Adoption-only sin header; pre-flight GET antes de cada POST | El server externo NO documenta el header (no deduplica hoy), pero la combinación es forward-compatible y el cierre real del gap del crash mid-write viene del pull adoptando huérfanos por `source_id`. La columna unique permite tracing/debug. | Una columna `Guid` que durante semanas no aporta deduplicación server-side; un Guid extra generado por intent. | 2 |
| Last-write-wins por `updated_at` con tie-break al externo (`>=`) | Last-writer-wins simétrico (rechazo en empate); local-always-wins; conflict-table que dispara revisión humana | El server externo es authoritative para sus timestamps (los genera él, ISO 8601, monotónicos). En empate exacto, preferir el externo es estable y predecible: el siguiente push local lo va a sobreescribir si el usuario lo edita después. | Pierde el cambio local en empates exactos (raro pero posible si dos clientes pegan al mismo segundo). Sin notificación al usuario del cambio externo aplicado. | 2 |
| Items fuera del slice 2 | Sync de items en mismo slice del pull de lists | El contrato externo no tiene POST aislado de items (solo se crean al crear la lista, o vía PATCH/DELETE individuales). Esa asimetría merece brainstorm propio: ¿re-POST de la lista entera con items nuevos? ¿outbox de items? Decisión arquitectónica que no quiero atar al pull de lists. | Items locales nuevos no se sincronizan al externo hasta slice 3. Pull de lists trae items anidados pero los descartamos. | 2 |
| Pull serial después del push en el mismo tick | Tick alternado push/pull; dos hosted services con intervals separados | Mantiene orden mental: push primero acepta el "winner local"; el pull después concilia y adopta huérfanos creados por crashes del push del mismo o tick previo. Un solo `SyncBackgroundService`. | Un push lento demora el pull. Aceptable para los volúmenes esperados. | 2 |
| `ApplyExternalCreateAsync` con dos `SaveChanges` (TodoList → SyncMapping) | Una sola transacción explícita con `BeginTransaction` | InMemory provider de EF ignora transacciones; documentar simple es más simple que ramas por provider. El gap entre los dos saves es microscópico y, si crashea, el pull siguiente vuelve a crear (caso edge documentado). | Posibilidad teórica de dejar un local sin mapping → al siguiente tick se duplica el local (sin manera de correlacionarlo via source_id porque la entry externa no lo tenía). Documentado como Edge Case. | 2 |
| Items locales nuevos en lista ya pusheada → no-sync + log Warning | Re-create destructivo de la lista entera (DELETE + POST con items); embed-on-first-push only (rechazar items late) | El contrato externo no expone `POST /todolists/{id}/todoitems`. Re-create destructivo cambia `ExternalId` de la lista y de TODOS sus items, invalida mappings, desestabiliza last-write-wins. No-sync + Warning preserva consistencia y deja un fallback claro: el usuario re-edita o el server agrega el endpoint. La spec dice "feel free to suggest changes" → reportado como sugerencia para el server. | Items agregados a una lista ya pusheada NO se sincronizan al externo. Documentado como limitación del contrato. | 3 |
| `SyncMapping.ParentExternalId nvarchar(64) NULL` (denormalizado) | Joinear con SyncMapping del list (col `ParentLocalId long?` para rebuscar el ExternalId del padre); transacción única padre+hijo | El DELETE de items orphans necesita el path `/todolists/{listId}/todoitems/{itemId}` y la única persistencia que sobrevive al hard-delete del item local es el SyncMapping del item. Denormalizar el ExternalId del padre evita un JOIN extra en cada DELETE; el ExternalId del padre es server-generated e inmutable según el contrato, así que la denormalización no envejece. NULL para mappings de TodoList. | Si en el futuro el server externo permitiera mover un item de lista (re-parenting) y ese cambio normalizara el ParentExternalId, quedaría stale. Slice 3 marca re-parenting como out-of-scope. | 3 |
| `TodoListItemSyncService` propio + `PullTodoListsAsync` devuelve tupla `(SyncRunResult, IReadOnlyList<ExternalListWithMapping>)` | Integrar push y pull de items dentro de `TodoListSyncService`; orquestar via callback dentro del list pull | Mantiene el patrón "una entidad → un service" del slice 1. La tupla en pull permite que el `SyncBackgroundService` invoque cada service explícitamente sin acoplar `TodoListSyncService` al item service. La fetch del externo (GET /todolists) sigue ocurriendo una sola vez por tick — el item pull recibe los items embebidos del response. | El item pull tiene una signature menos uniforme con el item push (recibe `mappedExternals` en lugar de no recibir nada). Acceptable por la asimetría real del flujo. | 3 |
| Detección de DELETE local de items por mapping huérfano (anti-join) | Tombstone soft-delete (flag `IsDeleted` en `TodoListItem`) + sync borra externo + hard-delete local | El tombstone invade el modelo de dominio para servir un concern de sync, contradiciendo el principio del slice 1 ("tabla de mapping mantiene el dominio libre del concern"). El anti-join `SyncMappings tipo TodoListItem WHERE NOT EXISTS TodoListItem(Id = LocalId)` corre una vez por tick y no afecta el path de delete del usuario. | En el caso de DELETE local de un item mapeado, hay un tick de retraso entre la eliminación local y la propagación al externo (hasta el próximo `PushTodoListItemsAsync`). Aceptable. Si el item externo ya estaba borrado (404), tratamos como resuelto y limpiamos el mapping local. | 3 |
| Items embebidos en POST inicial: persisten mappings con sus `ExternalId` retornados | POST con items + segundo paso "discover items via GET" para construir mappings; outbox de items | El response del POST ya trae los items con `id` + `source_id` + `updated_at`. Parsear `source_id` (que mandamos como `local.Id.ToString()`) permite mapear bidireccional sin GET extra. Edge case: si el server normaliza el `source_id`, log Warning y skip — el item queda sin mapping y se duplica en el próximo pull (mismo riesgo del list, ya documentado). | Una columna unique extra (`Idempotency-Key` no aplicable a items individuales — el POST cubre la lista entera). | 3 |

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
- **Slice 2:** cada intent de push genera un `Guid IdempotencyKey` que viaja como header `Idempotency-Key` en el POST y se persiste en `SyncMapping.IdempotencyKey` (unique). El server externo no procesa el header hoy (no está en el spec OpenAPI) — es forward-compatible para cuando lo soporte.

**Adoption (pull → cierre del gap del slice 1):**

El gap del crash mid-write entre `CreateTodoListAsync` (éxito) y `SaveChangesAsync(mapping)` queda **cerrado por el pull**. Cuando el pull encuentra un external entry con `source_id` parseable a un `long` que coincide con un `TodoList.Id` local que **no tiene mapping**, crea el mapping en lugar de tratar la entry como nueva. Verificado por el test `PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping`.

Limitación: la adoption solo funciona si el server preservó literalmente el `source_id` que el push le mandó. Si lo normaliza/transforma (p.ej. con un prefijo), se rompe la correlación y el huérfano se quedaría. La spec actual no documenta normalización; asumimos preservation literal.

**Pull: partial-failure semantics:**

- `GET /todolists` falla → status `Failed` con `ItemsProcessed = 0` y `ItemsFailed = 0` (no llegamos a iterar). Verificado por `PullTodoListsAsync_GetThrows_StatusFailedAndZeroProcessed`.
- Por cada external item, try/catch independiente. Una falla individual (PATCH externo, ApplyExternalCreate, etc.) incrementa `failed` y continúa con el siguiente.
- Status final del run: misma semántica que push (`failed == 0` → `Succeeded`; `processed == 0 && failed > 0` → `Failed`; mixto → `Partial`).

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
- [x] **API externa devuelve body vacío en 2xx** — el client lanza `ExternalApiException` con mensaje "POST/GET/PATCH todolists returned empty body".
- [x] **5xx / 408 / 429** — Polly retry. Si el circuit breaker abre, los siguientes ticks fallan rápido hasta que cierre.
- [x] **4xx genérico** — el client lanza `ExternalApiException`; el service la captura como falla individual. NO se reintenta (4xx genéricos no son transient).
- [x] **Crash mid-write del push (gap de slice 1)** — el pull adopta el huérfano externo via `source_id == local.Id.ToString()` y crea el mapping local. Test `PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping`.
- [x] **Pull: external más nuevo que el local mapeado** — remote wins, `local.Name` y `local.UpdatedAt` se sobreescriben con `external.updated_at`. Test `PullTodoListsAsync_MappedExternalNewer_UpdatesLocalName`.
- [x] **Pull: local más nuevo que el external mapeado** — local wins, PATCH al externo con el nuevo Name. Test `PullTodoListsAsync_MappedLocalNewer_PatchesExternal`.
- [x] **Pull: ambos lados cambiaron desde la última sync** — last-write-wins por timestamp; tie (`==`) gana el externo. Tests `..._BothChanged_ExternalWinsOnTimestamp`, `..._LocalWinsOnTimestamp`, `..._TieGoesToExternal`.
- [x] **Pull: nada cambió** — solo bumps `LastSyncedAt`, no toca local ni externo. Test `PullTodoListsAsync_MappedNoChanges_BumpsLastSyncedOnly`.
- [x] **Pull: external sin contraparte local** — `source_id` null o no parseable → CASO C, crea `TodoList` local con `UpdatedAt = external.updated_at`. Test `PullTodoListsAsync_ExternalWithUnknownSourceId_CreatesLocalAndMapping`.
- [x] **Pull: GET /todolists falla en bulk** — status `Failed` sin iterar items. Test `PullTodoListsAsync_GetThrows_StatusFailedAndZeroProcessed`.
- [x] **Pull: falla parcial mid-loop** — try/catch por entry; status `Partial`, los exitosos persisten. Test `PullTodoListsAsync_OneOfThreeFails_StatusPartial`.
- [ ] **`source_id` parseable pero apuntando a un local que ya no existe** — el `FindUnmappedLocalByIdAsync` retorna null y caemos a CASO C: creamos un local nuevo. Aceptable para slice 2 (no manejamos deletes). Si el local fue borrado a propósito, se va a recrear desde el externo — comportamiento simétrico al "external creó algo nuevo". Slice 4 (deletes) decidirá la política definitiva.
- [ ] **Crash entre los dos `SaveChanges` de `ApplyExternalCreateAsync`** — quedaría un `TodoList` local sin mapping. Como la entry externa no tiene `source_id` apuntando al local nuevo (vino de afuera), el siguiente pull la trataría como CASO C de nuevo y crearía OTRO local + mapping → duplicado local. Riesgo bajo, ventana microscópica. Mitigación correcta: transacción explícita o outbox. Documentado como deuda.
- [ ] **External devuelve un `id` > 64 chars** — la column `ExternalId` es `nvarchar(64)`. La insert fallaría en SqlServer real. No documentamos length en el contrato externo; asumimos UUIDs (~36 chars). Mitigation futura: subir el límite o fallar explícito al validar el response. Riesgo bajo para el challenge.
- [ ] **Concurrencia entre dos hosts del API corriendo simultáneamente** — el sync correría 2x sobre las mismas listas. Mappings tienen unique index `(EntityType, LocalId)` + `IdempotencyKey`, así que el segundo perdería con DbUpdateException — no estamos manejándola, runs concurrentes se pisan. Single-instance assumption por ahora.
- [ ] **External "deleted" un list que tenemos mapeado** — slice 2 no detecta deletes (un mapping cuya `ExternalId` ya no aparece en el GET es un delete remoto). El mapping queda obsoleto. Slice 4 (DELETE bidireccional) deberá decidir conflict resolution.
- [ ] **Server externo NO preserva `source_id` literalmente** — si lo normaliza/transforma, la adoption del pull falla y los huérfanos del crash mid-write se quedan. La spec actual no documenta normalización. Si pasara, pull crearía duplicados locales (otro `TodoList` con el mismo nombre). Mitigación futura: outbox pattern.
- [x] **Item local nuevo en lista ya pusheada → no se puede sincronizar** — limitación del contrato externo (no expone `POST /todolists/{id}/todoitems` aislado). El push detecta esos items via `GetUnmappedTodoListItemsWithMappedParentAsync` (anti-join: items sin mapping cuya lista padre SÍ tiene mapping) y emite Warning estructurado por cada uno. NO incrementan los contadores `Total`/`Processed`/`Failed` del run. Workaround para el usuario: re-editar la lista para forzar un cambio (no resuelve el problema), o esperar a que el server agregue el endpoint (sugerencia formal en Areas for Improvement). Test `PushTodoListItemsAsync_UnmappedItemsWithMappedParent_LogsWarningAndDoesNotCallClient`.
- [x] **DELETE local de un item mapeado → propaga al externo en el próximo tick** — el hard-delete del item local deja el `SyncMapping` huérfano (LocalId apunta a un row que ya no existe). `GetOrphanedItemMappingsAsync` lo detecta via anti-join y `PushTodoListItemsAsync` invoca `DeleteTodoItemAsync(ParentExternalId, ExternalItemId)` para sincronizarlo. Latencia: ~1 tick (default 60s). Test `PushTodoListItemsAsync_OrphanMapping_DeletesExternalAndRemovesMapping`.
- [x] **Item externo ya estaba borrado cuando hacemos DELETE (404)** — `catch (ExternalApiException ex) when (ex.StatusCode == 404)` trata como resuelto: limpia el mapping local + incrementa `Processed` + log Info. Test `PushTodoListItemsAsync_OrphanMappingExternal404_TreatsAsResolved`.
- [x] **Item externo `source_id` no parseable como `long`** (response del POST inicial o del pull) — `long.TryParse` falla → log Warning + skip mapping. El item queda sin mapping y se va a re-crear como duplicado local en el siguiente pull (CASO C). Mismo riesgo que para lists, ya documentado. Tests `PushTodoListsAsync_ResponseItemHasNonParseableSourceId_*` y `PullTodoListItemsAsync_ExternalWithUnknownSourceId_CreatesLocalItem`.
- [x] **Item externo con `source_id` parseable que NO matchea ningún local item recién pusheado** (response del POST inicial) — log Warning + skip. Test `PushTodoListsAsync_ResponseItemSourceIdDoesNotMatchAnyLocalItem_*`.
- [x] **LWW de items: ambos lados cambiaron, tie en `updated_at`** — externo gana (regla `>=`), simétrico con la política de slice 2 para listas. Tests `PullTodoListItemsAsync_MappedBothChanged_*`.
- [x] **Items dentro de lista huérfana adoptada por pull (CASO B del list pull)** — el list pull adopta el orphan y agrega `mappedExternals.Add(...)`. El item pull subsiguiente procesa los items embebidos como CASO A/B/C según corresponda. No hay caso especial. Cubierto por la combinación de tests del list pull (adoption) y del item pull (con `mappedExternals` no vacío).
- [x] **Items embebidos en pull de CASO C (lista nueva desde el externo)** — `ApplyExternalCreateAsync` extendido en Task 6 crea `TodoListItem` local + `SyncMapping` por cada `EmbeddedExternalItem` del plan, en el mismo método. El item pull NO los procesa de nuevo (CASO C del list pull NO agrega a `mappedExternals`). 3 saves en el flujo: list, items, mappings — la atomicidad débil hereda el riesgo del slice 2 (un crash entre saves duplica en el siguiente pull, mismo razonamiento).
- [ ] **Re-parenting (item movido entre listas, local o externamente)** — out of scope. El `UpdateTodoListItem` local NO permite cambiar `TodoListId` (DTO solo expone `Description` + `IsCompleted`); el `UpdateTodoItemBody` externo tampoco. Si surgiera un item externo cuyo padre cambió post-mapping, el `ParentExternalId` del SyncMapping quedaría stale y el próximo PATCH/DELETE iría a la URL vieja → 404 → log Warning + (en el caso del DELETE) limpieza del mapping. Aceptable. Slice futuro decidirá la política si el contrato lo expone.
- [ ] **DELETE externo de un item mapeado** — un mapping cuya `ExternalItemId` ya no aparece en `GET /todolists` es un delete remoto. Slice 3 NO lo detecta (alineado con la decisión de slice 2 de no manejar deletes externos de listas). Slice 4 deberá unificar la política de delete bidireccional para lists e items. Mientras tanto, el siguiente PATCH local→external al item daría 404; si el item ya tampoco existe local (cascade o delete), el orphan-detection del push lo limpiaría implícitamente con su 404-grace. Caso intermedio (existe local, ya no externo) queda con un mapping zombie hasta el próximo cambio.
- [ ] **Test de `UpdateAsync` del TodoListItemService es débil** — `Assert.True(updated.UpdatedAt >= before)` pasa aún sin el `item.UpdatedAt = DateTime.UtcNow` en el service (porque `before` y `updated.UpdatedAt` se derivan de seeds con `DateTime.UtcNow` cercanos, y `>=` admite empate). El test del Create sí es estricto (`> DateTime.MinValue` falla sin el setter). Mitigación futura: introducir abstracción `IClock` para tests deterministas o agregar `Thread.Sleep(1)` antes del Update. Bajo prioridad — el Create test es la barrera de TDD real.

## Areas for Improvement

Roadmap explícito de slices futuros y deuda técnica que sale del slice 3:

- ~~**Slice 2 — PULL externo→local + reconciliación.**~~ **Cerrado.** Adopta huérfanos por `source_id`, last-write-wins en TodoLists, sin detección de deletes (slice 4).
- ~~**Slice 3 — Sync de `TodoListItem`.**~~ **Cerrado.** Bidireccional con la limitación documentada de items new en list pushed (no-sync + Warning). Items embebidos en POST inicial; PATCH/DELETE local→external para mapeados/huérfanos; pull con LWW + adoption + create local; orquestación en 4 fases por tick.
- **Sugerencia para el server externo: agregar `POST /todolists/{listId}/todoitems`.** Es el bloqueante real para sincronizar items locales nuevos en listas ya pusheadas. Mientras no exista, slice 3 los logea Warning. CHALLENGE.md invita a sugerir cambios al contrato — esta es la propuesta concreta.
- **Slice 4 — DELETE bidireccional.** Detección de deletes externos (mapping cuya `ExternalId`/`ExternalItemId` desapareció del GET) + delete locales de listas propagados al externo. Para items, slice 3 ya cubre el delete local→external via huérfanos; slice 4 unifica la política con listas y agrega el sentido inverso (server-→local). Necesita política sobre conflict resolution (mirror, soft-delete con tombstones, etc.).
- **Outbox pattern** para garantía exactly-once en push y para resolver el gap microscópico de `ApplyExternalCreateAsync` (dos saves no atómicos). La adoption del slice 2 ya cubre el caso del crash mid-write del push, así que la urgencia bajó pero la deuda persiste.
- **Telemetría / métricas** de runs (Prometheus exporter, tracing, etc.). Hoy solo logs estructurados.
- **Endpoint manual `POST /api/sync/run`** para trigger on-demand (útil en dev y debugging).
- **README** con instrucciones para levantar la API externa (docker compose con el repo upstream `crunchloop/challenge-senior-engineer`) y verificar end-to-end.
- **`SyncRun.Error`** — la column existe pero no se escribe; el catch del loop solo loguea. Populate con un resumen agregado (último error, o concatenación de N) cuando se necesite forensics offline.
- **`ExternalApiException.Body` sin cap** — si el externo devuelve un HTML grande en 5xx, alocamos todo en memoria. Cap razonable (~2KB) en el client.
- **`SyncOptions` sin validación** — defaults sanos, pero un `Interval = 0` o `StartupDelay` negativo serían pathológicos. Custom validator en slice futuro.
- **Single-instance assumption** del background service — multiples hosts corriendo el mismo proceso ejecutarían el sync en paralelo, con condiciones de carrera. Locking distribuido o leader election fuera de scope.
- **`db.Database.Migrate()` solo en Development** — heredado del repo. En Production hay que aplicar migrations out-of-band antes de levantar la app, o el primer save del sync explota.
- **`SyncRunResult.Pushed` mal nombrado para pulls** — el record se usa para los dos sentidos pero el campo se llama `Pushed`. Semánticamente significa "items procesados con éxito" (independiente de la dirección). Renombrar a `Processed` cuando convenga romper compatibilidad de tests.
- **`GET /todolists` sin paginación ni filtros** del lado externo — full scan cada tick. OK para volúmenes del challenge; si crece, requiere o cambio del contrato externo (`?modified_since=…`) o cache local del último `updated_at` visto y filtrado client-side. Fuera de scope.
- **`ApplyExternalCreateAsync` con dos `SaveChanges`** — InMemory provider no soporta transacciones; el outbox formal lo cierra de raíz. Microscópico el riesgo, documentado como Edge Case.
- **Pull no detecta deletes externos** — un mapping cuya ExternalId desapareció del GET queda obsoleto. Slice 4.

## Assumptions

Supuestos explícitos del slice 1 — cada uno responde "¿qué se rompe si no es cierto?".

- **El `source_id` del externo es preservado verbatim.** Si el externo lo modifica/normaliza, perdemos correlación bidireccional y los pulls futuros (slice 2) crean duplicados locales en vez de detectar entries originados aquí.
- **Los IDs externos son strings de hasta 64 chars.** Si el externo devuelve algo más largo (URL-based, claves compuestas, etc.), las inserts fallan en SqlServer con error de truncation (visible en logs del sync). Defaults UUIDs (36 chars) — alcanza con margen.
- **El externo no tiene rate limiting agresivo, ni auth.** Spec dice "All endpoints do not require authorization". Si esto cambia, el typed client necesita interceptors para tokens; el pipeline ya maneja 429 con retry.
- **Las TodoLists locales son creadas SOLO por la API local (no por otra fuente).** El push asume que cualquier list sin mapping es candidato a sincronizar. Si en el futuro hay una segunda fuente de creación, necesitamos un flag para distinguir.
- **Single-instance del host.** El background service no tiene leader election ni locking distribuido. Múltiples instancias corriendo en paralelo intentarían pushear las mismas listas; el unique index en `SyncMappings` previene corrupción pero la segunda instancia perdería con `DbUpdateException` no manejada.
- **`Sync:Interval = 60s` es razonable** para los volúmenes esperados (listas creadas por interfaces de usuario, no por batch). Si el throughput sube significativamente, considerar interval más bajo o trigger por evento (slice futuro).
- **El base address del externo termina sin slash final** (default `http://localhost:8080`). El typed client compone con path relativo `"todolists"`. Si alguien configura `BaseAddress = "http://localhost:8080/api"` (con segmento de path), el resolve concatena mal y los requests salen a `http://localhost:8080/todolists` perdiendo `/api`. Mitigación: documentar en el README o validar en `ExternalApiOptions`. Riesgo bajo en el challenge porque el spec usa root.
- **El server externo preserva `source_id` literalmente.** Slice 2 depende de esto para que la adoption del pull funcione: enviamos `local.Id.ToString()` como `source_id` en el push y, post-crash mid-write, esperamos que el GET nos lo devuelva intacto para reconciliarlo. Si el server lo normaliza/transforma, los huérfanos no se adoptan y se duplican locales en el siguiente pull (CASO C). La spec OpenAPI no documenta normalización; asumimos preservation literal.
- **Tie-break en empate exacto de `updated_at` lo gana el externo (regla `>=`).** El server es authoritative para sus timestamps (los genera él). En el caso (raro) en que dos clientes pegan a la misma fracción de segundo, perdemos el cambio local. Si el usuario re-edita después, el siguiente push lo va a sobreescribir. Sin notificación al usuario del cambio externo aplicado.
- **Clock UTC del server externo y del local "razonablemente" alineados.** Sin sync horario formal (NTP, etc.), drift de segundos es tolerable; drift de minutos podría disparar last-write-wins erróneo (ej. local ganaría aunque el externo ya aplicó un cambio más reciente real-time). Asumimos cluster homogéneo o NTP en todos los hosts.
- **El `Idempotency-Key` header que mandamos no es procesado por el server hoy** (no documentado en la spec OpenAPI). Lo enviamos forward-compatible. Si nunca lo soporta, no daña — el cierre del gap viene del adoption en pull.

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

### 2026-05-09 — Slice 2: PULL externo→local + Idempotency-Key + last-write-wins

- **Decisión:** el slice agrega tres piezas que se complementan: (1) **Idempotency-Key**: `Guid` por intent de push, mandado como header HTTP y persistido en `SyncMapping.IdempotencyKey` (unique). (2) **PULL** de TodoLists: `GET /todolists` por tick, decisión por entry entre tres casos — A. mapped → reconcile last-write-wins; B. `source_id` parseable apunta a un local sin mapping → adoption (cierra el gap del crash mid-write de slice 1); C. otherwise → crear `TodoList` local. (3) **Last-write-wins** comparando `local.UpdatedAt` vs `external.updated_at`, con tie-break al externo (regla `>=`). Para soportar (3), `TodoList.UpdatedAt` es columna nueva (default `GETUTCDATE()` en SqlServer real); el `TodoListService` la setea en Create/Update. Cadencia: push y pull serial en el mismo tick del `SyncBackgroundService`, try/catch independientes.
- **Alternativas descartadas:**
  - **Adoption-only sin header `Idempotency-Key`** — más simple, pero deja el sistema sin nada preparado para cuando el server agregue soporte. El usuario pidió explícitamente "idempotency key", así que la implementación literal vale aún sabiendo que el server no la procesa hoy.
  - **Pre-flight GET antes de cada POST** para chequear si ya existe `source_id` matching — cierra el gap del crash dentro del mismo tick, pero si el pull corre en el mismo tick (que sí corre), el GET es redundante. Más round-trips.
  - **Items sincronizados en este mismo slice** — el contrato externo no expone POST aislado de items, solo se crean al crear la lista o vía PATCH/DELETE individuales. Esa asimetría merece brainstorm propio (re-POST entera vs outbox de items vs other). Slice 3 propio.
  - **Detección de deletes externos en este slice** — el user dijo "conflictos de modificación, last-write-wins". No mencionó deletes. Slice 4.
  - **Tick alternado push/pull o dos hosted services con intervals separados** — over-engineering para los volúmenes esperados. Serial mismo tick mantiene orden mental.
  - **`SyncRunResult` separado por dirección (`PushRunResult` vs `PullRunResult`)** — innecesario; el record existente sirve si interpretamos `Pushed` como "items procesados exitosamente". Documentado como deuda menor en Areas for Improvement.
  - **Una sola `BeginTransaction` para `ApplyExternalCreateAsync`** — InMemory provider las ignora; ramas por provider complican los tests. Dos `SaveChanges` consecutivos con edge case documentado es aceptable.
- **Por qué:** la decisión cardinal es **cerrar el gap del slice 1 (duplicado externo en crash mid-write) sin esperar al outbox formal**. La adoption en pull lo hace de forma elegante: el pull tiene que matchear por `source_id` igual para evitar duplicados locales — agregar la rama "matchea local sin mapping → crear mapping" cuesta poco y resuelve el problema. El header `Idempotency-Key` cumple literalmente lo pedido por el usuario y queda forward-compatible sin costo. El last-write-wins con tie al externo es la política más simple que tiene sentido (server es authoritative para sus timestamps). Para implementar todo esto sin tocar el patrón de slice 1: nuevos métodos en `ISyncDbContext` (`GetMappedTodoListsAsync`, `FindUnmappedLocalByIdAsync`, `ApplyExternalCreateAsync`, `ApplyRemoteWinsAsync`) — el sync project sigue sin referenciar `TodoApi.Models`. Las operaciones que solo tocan `SyncMappings` se quedan en el service (igual que el push de slice 1).
- **Supuestos nuevos:**
  - El server externo preserva `source_id` literalmente. Si lo normaliza, la adoption falla y los huérfanos del crash mid-write se duplican.
  - Tie-break en empate exacto de `updated_at` lo gana el externo. El usuario re-editando después dispara el push, así que la pérdida es transitoria.
  - Clock UTC del externo "razonablemente" alineado con el local. Drift de segundos OK; minutos no.
  - El `Idempotency-Key` header viaja al server pero no se procesa hoy. La columna local sirve para tracing/debug.
  - El `JsonSerializer` deserializa `created_at`/`updated_at` (ISO 8601 con `Z`) como `DateTime` con `Kind = Utc`. Verificado por el test del cliente externo (`UpdateTodoListAsync_HappyPath_PatchesAndDeserializesResponse` compara contra `new DateTime(..., DateTimeKind.Utc)`).
- **Deuda / follow-ups:**
  - Items (slice 3) — ahora promovido a próximo.
  - Detección de deletes externos (slice 4).
  - Outbox formal — el adoption del pull cubre el caso del crash mid-write del push, pero NO cubre el caso del crash entre los dos `SaveChanges` de `ApplyExternalCreateAsync`. La urgencia bajó pero la deuda persiste.
  - `SyncRunResult.Pushed` mal nombrado para pulls — renombrar a `Processed` cuando convenga romper compat.
  - `GET /todolists` sin paginación/filtros del lado externo — full scan cada tick. Aceptable para el challenge; si crece, requiere cambio de contrato externo o cache de `updated_at` local.
  - Renombrar `LocalTodoListRecord` a algo más neutro (lo usamos tanto para "unmapped local" en push como en CASO B del pull). Bajo prioridad.

### 2026-05-09 — Slice 3: Sync de TodoListItem (asimetría POST + bidireccional)

- **Decisión:** sync bidireccional de `TodoListItem` resolviendo la asimetría confirmada del contrato externo (verificado en `assets/external-api.yaml`): existen `PATCH` y `DELETE` aislados de items pero **no existe `POST /todolists/{id}/todoitems`**; items solo se crean externamente embebidos en el `POST /todolists` inicial. El slice cubre cuatro flujos: (1) `TodoListItem` gana columna `UpdatedAt` (espejo del slice 2 sobre `TodoList`); el service local la setea en Create/Update. (2) Modificación del push del slice 1: `PushTodoListsAsync` ahora embebe `local.Items` en el body del POST inicial y, después del response, persiste mappings de los items embebidos retornados (con `ParentExternalId = external.Id`). (3) `TodoListItemSyncService.PushTodoListItemsAsync` corre cada tick: PATCH local→external para items mapeados que cambiaron (`CurrentLocalUpdatedAt > LocalUpdatedAtAtSync`); DELETE local→external para mapping huérfano (anti-join `SyncMapping(EntityType=TodoListItem) WHERE NOT EXISTS TodoListItem(Id=LocalId)`) con grace para 404; log Warning para items locales nuevos en lista ya pusheada (no-sync, los contadores del run NO los cuentan). (4) `PullTodoListItemsAsync(IReadOnlyList<ExternalListWithMapping>, ct)` recibe los items ya traídos por el list pull (que ahora devuelve tupla) y aplica LWW por item: CASO A `mapped` → reconcile (4 ramas: external wins / local wins / tie-to-external / no-changes-bump); CASO B `source_id` apunta a un local sin mapping → adoption; CASO C → create local desde external. `SyncMapping` gana columna `ParentExternalId nvarchar(64) NULL` para que el path padre del item sobreviva al hard-delete del row local. `SyncBackgroundService` orquesta 4 fases por tick (list push → item push → list pull → item pull), cada una en try/catch independiente.
- **Alternativas descartadas:**
  - **Re-create destructivo de la lista entera** para sincronizar items locales nuevos en lista ya pusheada — DELETE list externo + POST nuevo con TODOS los items. Cambia ExternalIds de la lista y de TODOS sus items, invalida mappings, desestabiliza last-write-wins de timestamps. Blast radius enorme para resolver un caso edge documentable.
  - **Embed-on-first-push only** (rechazar cualquier item creado después del primer push de la lista, no solo loguearlo) — más estricto pero idéntico end-state al chosen approach. La diferencia es semántica: con "no-sync + Warning" el item local persiste y el siguiente cambio lo va a empujar (cuando el server agregue el endpoint).
  - **Tombstone soft-delete** (flag `IsDeleted` en `TodoListItem`) para detectar deletes locales — invade el modelo de dominio para servir un concern de sync. Contradice el principio del slice 1 ("tabla mapping mantiene el dominio libre del concern"). Anti-join sobre mappings huérfanos resuelve igual sin contaminar.
  - **Integrar push y pull de items dentro de `TodoListSyncService`** — rompe el patrón "una entidad → un service" del slice 1 y hace al service mucho más grande. La tupla de retorno del list pull permite mantener el split sin doble GET.
  - **Re-parenting de items entre listas** — el local DTO no expone `TodoListId` mutable y el contrato externo tampoco. Out of scope. Documentado como Edge Case.
  - **Detección de DELETE externo de items mapeados en este slice** — slice 4 (delete bidireccional unificado para lists e items) lo cubrirá. En el meantime, un PATCH local→external a un item ya borrado externamente daría 404 y se loguea Warning.
  - **`Idempotency-Key` header en PATCH/DELETE** — PATCH y DELETE son naturalmente idempotentes (la operación con el mismo ExternalId/contenido produce el mismo resultado). El header se mantiene solo en el POST inicial. PATCH/DELETE no lo mandan.
- **Por qué:** la decisión cardinal es **respetar la asimetría del contrato sin destruir información**. El re-create destructivo era la única alternativa que cubría items late, pero a un costo (cambio de ExternalIds, mappings invalidados, timestamps desestabilizados) desproporcionado al problema. La política "no-sync + Warning + reportar al server" preserva consistencia local, deja un fallback claro (el usuario re-edita o el server agrega el endpoint), y es honest sobre la limitación. El resto del slice espeja el patrón establecido en slice 1+2: anti-join para detectar candidatos, métodos específicos en `ISyncDbContext` (sin filtrar con tipos importados), service propio por entidad, last-write-wins con tie a external. La pieza nueva conceptualmente es la denormalización del `ParentExternalId` en `SyncMapping` — un trade-off consciente entre "extra JOIN en cada DELETE" y "una columna que no envejece porque el ExternalId del padre es server-generated e inmutable".
- **Supuestos nuevos:**
  - El server externo preserva `source_id` literalmente para items embebidos en el POST inicial (igual que para lists). Si normaliza, los items quedan sin mapping y se duplican en el próximo pull (mismo riesgo del list, ya documentado).
  - El `ExternalId` del list (`ParentExternalId` en mappings de items) no cambia durante la vida del list. El contrato no documenta normalización ni reasignación; asumimos inmutabilidad.
  - Items locales nuevos creados en una lista ya pusheada NO se sincronizan al externo hasta que el server agregue `POST /todolists/{listId}/todoitems`. Documentado como limitación + sugerencia formal en Areas for Improvement.
  - Re-parenting (cambio de `TodoListId` de un item) NO ocurre — ni el `UpdateTodoListItem` local lo permite, ni el `UpdateTodoItemBody` externo. Si el server lo expusiera en el futuro, el `ParentExternalId` del SyncMapping quedaría stale y se rompería.
  - El `JsonSerializer` deserializa `description`/`completed`/`created_at`/`updated_at` de items igual que los de lists (snake_case + `DateTime` con `Kind=Utc` para los timestamps).
  - `Total` del `SyncRunResult` para items = `mapped.Count + orphans.Count` (warnings de items late NO suman). Items mapeados sin cambios cuentan como `Processed` (examinados, decisión: skip), no como Failed.
  - Single-instance del host (heredado de slice 1+2) — si dos instancias corrieran el sync paralelo, el orphan-detection podría double-DELETE o disparar 404-grace innecesarios. Aceptable bajo single-instance.
- **Deuda / follow-ups:**
  - **Slice 4 — DELETE bidireccional** completo (detección de DELETE externo de lists e items + delete locales de listas propagados). El delete local→external de items ya está cubierto por slice 3; slice 4 unifica la política con listas y agrega el sentido inverso.
  - **Sugerencia formal al server externo: agregar `POST /todolists/{listId}/todoitems`**. Bloquea el caso "item local nuevo en lista ya pusheada".
  - **Outbox pattern** — el slice 3 hereda el gap del slice 2 en `ApplyExternalCreateAsync` (ahora con 3 saves en el path con items: list, items, mappings). El crash entre cualquier par puede dejar estado inconsistente que el pull cura como CASO C de items, mismo razonamiento. Outbox formal sigue siendo deuda.
  - **Test de Update del `TodoListItemService` débil** — `Assert.True(updated.UpdatedAt >= before)` pasa aún sin el setter del service. El test del Create sí es strict. Mitigación futura: abstracción `IClock` para tests deterministas.
  - **`SyncRunResult.Pushed` mal nombrado para pulls** — sigue como deuda, agravada ahora que tenemos 4 fases (push list, push item, pull list, pull item) usando el mismo record.
  - **Helper de seeding en tests** — `TodoListItemSyncServiceTests.cs` quedó en ~1650 líneas; el helper `SeedMappedPair(ctx, parentLocalId, parentExternalId, itemLocalId, itemExternalId, snapshot)` reduciría duplicación. Bajo prioridad.
  - **CRLF→LF normalization** — un commit de slice 3 reformateó `TodoContext.cs` (line endings) inadvertidamente. No afecta build pero hace ruido en el diff. Configurar `.gitattributes` con `* text=auto` resolvería para futuros slices.
  - **`TodoContext.cs` creció a ~370 líneas** — sigue cohesivo (single-responsibility = "ISyncDbContext implementation + EF model config") pero candidato a partial class `TodoContext.Sync.cs` cuando slice 4 agregue más métodos.
