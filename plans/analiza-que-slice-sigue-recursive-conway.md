# Slice 2 — PULL de TodoLists + `Idempotency-Key` + last-write-wins

## Context

Slice 1 cerró con PUSH local→externo de TodoLists sin items. Idempotencia actual: anti-join local sobre `SyncMappings`. Tres deudas concretas registradas en el Decision Log de `NOTES.md`:

1. **Gap de duplicado externo en crash mid-write** — entre `client.CreateTodoListAsync` (éxito) y `_db.SaveChangesAsync(mapping)`, un crash deja una entrada huérfana en el externo. El próximo tick re-crea.
2. **PULL externo→local** sin definir.
3. **Conflict resolution policy** sin definir.

El usuario pidió: **idempotency key** para el push, **PULL** como siguiente capacidad y **last-write-wins** para conflictos de modificación.

**Constraint duro descubierto:** el OpenAPI upstream (`crunchloop/challenge-senior-engineer/docs/external-api.yaml`) NO documenta el header `Idempotency-Key`. El server no lo procesa hoy. Decisiones tomadas:

- **Idempotency:** mandamos el header `Idempotency-Key` (forward-compatible con un server que lo soporte) **+** persistimos `SyncMapping.IdempotencyKey` (unique) **+** el cierre real del gap del crash viene del **pull adoptando huérfanos** vía `source_id == local.Id.ToString()`. Combinación robusta y compatible con el contrato actual.
- **Scope:** este slice solo cubre TodoLists. Items van a slice 3 (la spec externa no expone POST aislado de items y eso merece brainstorm propio).
- **Conflictos de modificación:** last-write-wins comparando `local.UpdatedAt` vs `external.updated_at`. Tie-break (igualdad de timestamps) lo gana el externo (server authoritative). Documentado como Assumption.
- **Deletes externos** (lista mapeada que desaparece del GET): out of scope este slice. Slice 4.
- **Cadencia background service:** serial en el mismo tick — primero PUSH, después PULL. Un solo `SyncBackgroundService`.

## Hallazgos del contrato externo (lo que la spec confirma)

| Aspecto | Estado | Implicación |
|---|---|---|
| `GET /todolists` | Devuelve `TodoList[]` con items anidados, sin paginación, sin filtros (`?modified_since=`) | Pull = full scan cada tick. OK para volúmenes del challenge. |
| `POST /todolists` | Acepta `source_id` opcional + items anidados. Devuelve list con `id`, `created_at`, `updated_at` server-generated | Lo usamos hoy. Vamos a agregar header `Idempotency-Key`. |
| `PATCH /todolists/{id}` | Solo metadata (Name). NO acepta items. | Necesario cuando "local gana" en last-write-wins. |
| `DELETE /todolists/{id}` | Hard delete, 204, sin tombstone | Out of scope este slice. |
| `Idempotency-Key` header | NO documentado | Mandamos el header igual; deduplicación real viene de la adoption en pull. |
| `created_at` / `updated_at` | Server-generated, ISO 8601, readonly | Base para last-write-wins. |
| Items POST aislado | NO existe | Items quedan fuera del slice. |
| Auth, rate limit | Ninguno | Sin cambios. |

## Approach

### 1. Schema changes

#### 1a. `TodoList.UpdatedAt` (TodoApi)

- `TodoApi/Models/TodoList.cs` — agregar `public DateTime UpdatedAt { get; set; }`.
- `TodoApi/Services/TodoListService.cs:28` (`CreateAsync`) — setear `UpdatedAt = DateTime.UtcNow` al construir.
- `TodoApi/Services/TodoListService.cs:41` (`UpdateAsync`) — después de `todoList.Name = dto.Name`, setear `todoList.UpdatedAt = DateTime.UtcNow`.
- Migration nueva `AddTodoListUpdatedAt`: columna `UpdatedAt datetime2 NOT NULL` con default `GETUTCDATE()` para registros existentes.
- Tests existentes de `TodoListService` siguen verdes (la columna se setea internamente, los DTOs no cambian).

#### 1b. `SyncMapping` extendido (TodoApi.Sync)

- `TodoApi.Sync/Models/SyncMapping.cs` — agregar:
  - `Guid IdempotencyKey { get; set; }` — único por intent de push, generado pre-POST.
  - `DateTime? LocalUpdatedAtAtSync { get; set; }` — snapshot de `local.UpdatedAt` al cierre del último push/pull conciliado.
  - `DateTime? ExternalUpdatedAtAtSync { get; set; }` — snapshot de `external.updated_at` al cierre del último push/pull conciliado.
- `OnModelCreating` (en el `TodoContext` o donde esté la config de `SyncMapping`) — unique index sobre `IdempotencyKey`.
- Migration nueva `ExtendSyncMappingForReconciliation`: tres columnas + unique index. Para mappings preexistentes, `IdempotencyKey` default `NEWID()`; los `*UpdatedAtAtSync` quedan nullable (poblados en el primer ciclo posterior a la migration).

#### 1c. `ISyncDbContext` extendido

Agregar al menos:

```csharp
Task<List<MappedTodoListRecord>> GetMappedTodoListsAsync(CancellationToken ct = default);
// proyección: { LocalId, ExternalId, IdempotencyKey, LastSyncedAt,
//               LocalUpdatedAtAtSync, ExternalUpdatedAtAtSync,
//               currentLocalName, currentLocalUpdatedAt }

Task<TodoListPullCandidate?> FindUnmappedLocalByIdAsync(long localId, CancellationToken ct = default);
// para el caso "external trae source_id apuntando a un local que existe pero sin mapping" (adoption de orphan post-crash)
```

Para ejecutar el pull (create/update locales + crear/actualizar mapping) seguimos el patrón: las mutaciones del `TodoList` local se hacen vía cast interno dentro del `TodoContext` que IMPLEMENTA `ISyncDbContext`, NO desde el sync project. Esto requiere o bien:
- Métodos específicos en `ISyncDbContext` que encapsulen `ApplyExternalCreateAsync`, `ApplyExternalUpdateAsync` (preferido — sigue el patrón de `GetUnmappedTodoListsAsync` de slice 1).
- O exponer un método `ExecuteInTransactionAsync(Func<...>)` (descartado: el sync project no debería componer mutaciones EF crudas).

Decisión: encapsular en `ISyncDbContext` los métodos `ApplyExternalCreateAsync(ApplyExternalCreatePlan)` y `ApplyExternalUpdateAsync(ApplyExternalUpdatePlan)` que internamente tocan la entity `TodoList` via el `TodoContext`. Eso preserva el patrón "el sync project no referencia TodoApi.Models" de slice 1.

### 2. `IExternalTodoListClient` extendido

`TodoApi.Sync/External/IExternalTodoListClient.cs`:

```csharp
Task<ExternalTodoList> CreateTodoListAsync(
    CreateExternalTodoListRequest request,
    Guid idempotencyKey,
    CancellationToken cancellationToken
);

Task<IReadOnlyList<ExternalTodoList>> GetTodoListsAsync(
    CancellationToken cancellationToken
);

Task<ExternalTodoList> UpdateTodoListAsync(
    string externalId,
    UpdateExternalTodoListRequest request,
    CancellationToken cancellationToken
);
```

`TodoApi.Sync/External/ExternalTodoListClient.cs` — implementación:
- `CreateTodoListAsync`: cambiar de `PostAsJsonAsync` (que no acepta headers per-request) a `HttpRequestMessage` manual con `Content = JsonContent.Create(...)` y `request.Headers.Add("Idempotency-Key", key.ToString())`. Conservar manejo de error y deserialización actual.
- `GetTodoListsAsync`: GET a `"todolists"`, deserializar a `List<ExternalTodoList>`. Mismo manejo de error (`ExternalApiException` con method/path/body si !success).
- `UpdateTodoListAsync`: PATCH a `"todolists/{externalId}"`. `HttpRequestMessage(HttpMethod.Patch, ...)` con body JSON.

Resilience: el pipeline registrado en `SyncServiceCollectionExtensions.AddTodoSync` aplica al `HttpClient` typed entero — no hace falta tocarlo. Verificamos en tests que GET y PATCH usan retry/circuit breaker/timeout.

### 3. `TodoListSyncService.PushTodoListsAsync` revisitado

Por cada candidato `local`:
1. `var idempotencyKey = Guid.NewGuid();`
2. `await _client.CreateTodoListAsync(request, idempotencyKey, ct);`
3. Persistir `SyncMapping` con `IdempotencyKey = idempotencyKey`, `LocalUpdatedAtAtSync = local.UpdatedAt`, `ExternalUpdatedAtAtSync = external.updated_at`, `LastSyncedAt = UtcNow`.

El gap del crash mid-write entre paso 2 y paso 3 NO se cierra por esta sola pieza — lo cierra la adoption del pull (paso 4 abajo). El header `Idempotency-Key` queda registrado y mandado al server por si en el futuro deduplica (no daña que hoy lo ignore).

### 4. `TodoListSyncService.PullTodoListsAsync` (nuevo método)

Pseudo-flujo:

```
abrir SyncRun(EntityType=TodoList, Direction=Pull, Status=Running)

externals = await client.GetTodoListsAsync(ct)
mappedByExternalId = await db.GetMappedTodoListsAsync(ct)  // dict por ExternalId

processed=0, failed=0
adopted=0, createdLocal=0
remoteWins=0, localWins=0, noChange=0

foreach external in externals:
  try:
    if external.Id in mappedByExternalId:
      // CASO A: ya mapeado → reconcile
      mapped = mappedByExternalId[external.Id]
      externalChanged = external.updated_at > (mapped.ExternalUpdatedAtAtSync ?? DateTime.MinValue)
      localChanged   = mapped.currentLocalUpdatedAt > (mapped.LocalUpdatedAtAtSync ?? DateTime.MinValue)

      if externalChanged && localChanged:
        // ambos cambiaron → last-write-wins (>= favor externo en empate)
        if external.updated_at >= mapped.currentLocalUpdatedAt:
          ApplyExternalUpdateAsync(local=mapped.LocalId, name=external.Name, externalUpdatedAt=external.updated_at)
          remoteWins++
        else:
          await client.UpdateTodoListAsync(external.Id, new UpdateExternalTodoListRequest(mapped.currentLocalName), ct)
          // refrescar snapshots
          mapping.ExternalUpdatedAtAtSync = response.updated_at
          mapping.LocalUpdatedAtAtSync = mapped.currentLocalUpdatedAt
          localWins++
      elif externalChanged:
        ApplyExternalUpdateAsync(...)
        remoteWins++
      elif localChanged:
        await client.UpdateTodoListAsync(...)
        localWins++
      else:
        noChange++

      // siempre actualizar mapping.LastSyncedAt y los snapshots; persistir.

    elif external.SourceId is parseable to long L AND existe local TodoList con Id=L AND L no tiene mapping:
      // CASO B: orphan creado por crash mid-write → ADOPTION
      // (idempotencyKey no recordable post-crash → generar uno nuevo; el header ya viajó al server pero no lo guardamos. Aceptable: la unicidad solo importa para distinguir intents desde acá hacia adelante.)
      crear SyncMapping(LocalId=L, ExternalId=external.Id, IdempotencyKey=Guid.NewGuid(),
                       LastSyncedAt=UtcNow, LocalUpdatedAtAtSync=local.UpdatedAt,
                       ExternalUpdatedAtAtSync=external.updated_at)
      adopted++

    else:
      // CASO C: entry creada externamente sin contraparte local
      ApplyExternalCreateAsync(name=external.Name, updatedAt=external.updated_at)
        // crea TodoList local con UpdatedAt = external.updated_at
        // crea SyncMapping en la misma transacción
      createdLocal++

    processed++

  catch ex:
    failed++
    log warning con {ExternalId}, {SourceId}

cerrar SyncRun:
  ItemsProcessed = processed
  ItemsFailed = failed
  Status = (failed==0) ? Succeeded : (processed==0 ? Failed : Partial)
```

Notas:
- Save por entry (igual patrón que push de slice 1) — idempotencia frente a crash mid-pull.
- Comparaciones de timestamps todas en UTC (`DateTime.UtcNow`). El JSON serializer existente (`ExternalJsonOptions.Default`, snake_case) ya deserializa `created_at`/`updated_at` a `DateTime`. Verificar en test que llega como UTC (Kind = Utc o `DateTimeOffset`) — si llega como `Local`, convertir explícito.
- Tie-break: `>=` favor externo. Documentado como Assumption.

### 5. `SyncBackgroundService` actualizado

`TodoApi.Sync/Hosting/SyncBackgroundService.cs`:

En el loop, después del `PushTodoListsAsync`, llamar `PullTodoListsAsync` en el mismo scope. Loguear ambos resultados independientemente. Try/catch separados para que un fallo en push no impida el pull (y viceversa).

```
var pushResult = await svc.PushTodoListsAsync(stoppingToken);
log("Sync tick push: {Total} {Processed} {Failed} {Status}", pushResult...);
var pullResult = await svc.PullTodoListsAsync(stoppingToken);
log("Sync tick pull: {Total} {Processed} {Failed} {Status}", pullResult...);
```

### 6. Tests xUnit

Espejando el patrón de `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs` (xUnit + InMemoryDatabase + Moq de `IExternalTodoListClient`).

Push (modificados / nuevos):
- `PushTodoListsAsync_PassesIdempotencyKeyToClient` — capturar el `Guid` argumento y verificar que se persiste en `SyncMapping.IdempotencyKey`.
- (Tests existentes siguen verdes con la nueva firma del cliente.)

Pull (nuevos):
- `PullTodoListsAsync_NoExternalLists_ReturnsZeroAndSucceeded`
- `PullTodoListsAsync_ExternalWithUnknownSourceId_CreatesLocalAndMapping`
- `PullTodoListsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping` ← **cierre del gap de slice 1**
- `PullTodoListsAsync_MappedExternalNewer_UpdatesLocalName`
- `PullTodoListsAsync_MappedLocalNewer_PatchesExternal`
- `PullTodoListsAsync_MappedBothChanged_ExternalWinsOnTimestamp`
- `PullTodoListsAsync_MappedBothChanged_LocalWinsOnTimestamp`
- `PullTodoListsAsync_MappedBothChanged_TieGoesToExternal` ← cubre la regla `>=`
- `PullTodoListsAsync_MappedNoChanges_Skips`
- `PullTodoListsAsync_OneOfThreeFails_StatusPartialAndOthersReconciled`
- `PullTodoListsAsync_AllFail_StatusFailed`

Cliente externo (`TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs`):
- `CreateTodoListAsync_SetsIdempotencyKeyHeader` — `StubHttpMessageHandler` captura el request y verifica el header.
- `GetTodoListsAsync_HappyPath_DeserializesArray`
- `GetTodoListsAsync_5xx_BubblesExternalApiException`
- `UpdateTodoListAsync_HappyPath_PatchesAndDeserializes`
- `UpdateTodoListAsync_404_BubblesExternalApiException`

`StubHttpMessageHandler` puede necesitar extensión para responder distinto según method+path. Si hace falta, modificación trivial.

### 7. `NOTES.md` actualizado

Append-only en Decision Log:

```
### 2026-05-09 — Slice 2: PULL externo→local + Idempotency-Key + last-write-wins

- Decisión: ...
- Alternativas descartadas:
  - Adoption-only sin header
  - Pre-flight GET antes de cada POST
- Por qué: ...
- Supuestos nuevos:
  - Tie-break en igualdad de timestamps gana el externo (server authoritative)
  - El server externo va a respetar `source_id` literalmente — la adoption depende de ese roundtrip preservar el local Id stringificado
  - Clock UTC del server externo "razonablemente" alineado con el local — sin sync horario formal, drift < segundos
- Deuda / follow-ups:
  - Items (slice 3)
  - Detección de deletes externos (slice 4)
  - Outbox formal (sigue abierta como mejora futura, pero la urgencia bajó: adoption + idempotency-key cubren los casos comunes)
```

Update en secciones formales:
- **High-Level Overview**: agregar el flow del pull en el tick.
- **Key Design Decisions**: filas nuevas — idempotency-key approach (header + columna + adoption), last-write-wins con tie-break externo, items fuera del slice.
- **Resilience and Error Handling**: subsección "Adoption" describiendo cómo el pull cierra el gap del crash mid-write.
- **Edge Cases**: marcar el gap como cerrado (`[x]`); agregar los nuevos casos de pull (external newer, local newer, ambos cambiaron, tie, source_id apuntando a local Id inexistente, source_id no parseable).
- **Areas for Improvement**: marcar slice 2 como cerrado, repriorizar items (slice 3) y deletes (slice 4); el outbox baja a deuda menos urgente.
- **Assumptions**: agregar tie-break, clock alignment, `source_id` preservation literal.

## Critical files

- `TodoApi/Models/TodoList.cs` — agregar `UpdatedAt`.
- `TodoApi/Services/TodoListService.cs` — setear `UpdatedAt` en Create/Update.
- `TodoApi/Migrations/` — dos migrations nuevas (`AddTodoListUpdatedAt`, `ExtendSyncMappingForReconciliation`).
- `TodoApi.Sync/Models/SyncMapping.cs` — tres columnas nuevas.
- `TodoApi/Data/TodoContext.cs` — config del unique index en `IdempotencyKey` + implementación de los métodos nuevos de `ISyncDbContext`.
- `TodoApi.Sync/Data/ISyncDbContext.cs` — `GetMappedTodoListsAsync`, `FindUnmappedLocalByIdAsync`, `ApplyExternalCreateAsync`, `ApplyExternalUpdateAsync`.
- `TodoApi.Sync/External/IExternalTodoListClient.cs` — `GetTodoListsAsync`, `UpdateTodoListAsync`, firma nueva de `CreateTodoListAsync`.
- `TodoApi.Sync/External/ExternalTodoListClient.cs` — implementación de los tres + header `Idempotency-Key`.
- `TodoApi.Sync/External/Models/` — agregar `UpdateExternalTodoListRequest` y los proyection records (`MappedTodoListRecord`, `TodoListPullCandidate`, `ApplyExternalCreatePlan`, `ApplyExternalUpdatePlan`).
- `TodoApi.Sync/Services/TodoListSyncService.cs` — `PullTodoListsAsync` nuevo + ajuste mínimo a `PushTodoListsAsync` (idempotency key).
- `TodoApi.Sync/Services/ITodoListSyncService.cs` — exponer `PullTodoListsAsync`.
- `TodoApi.Sync/Hosting/SyncBackgroundService.cs` — invocar pull después de push.
- `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs` — nuevos tests del pull.
- `TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs` — tests para GET, PATCH, header.
- `TodoApi.Tests/Sync/TestHelpers/StubHttpMessageHandler.cs` — extender para responder por method+path si hace falta.
- `NOTES.md` — Decision Log + secciones formales.

## Verification

1. `dotnet test` desde el root — toda la suite verde, incluyendo nuevos tests del pull, de adoption y de la firma del cliente con header.
2. `dotnet csharpier --check .` — sin drift (CI del repo upstream falla si hay drift).
3. **Smoke E2E manual** (con la API externa local levantada con docker compose del repo upstream):
   - Crear `TodoList` vía `POST /api/todolists`.
   - Esperar un tick (60s o bajar `Sync:Interval`).
   - Logs: `Pushed TodoList {id} ... Idempotency-Key={guid}`.
   - Modificar el `Name` directamente en el externo (curl PATCH).
   - Esperar el siguiente tick.
   - Logs del pull: `Reconciled TodoList {id}: remote wins`.
   - `GET /api/todolists/{id}` local muestra el nombre actualizado.
4. **Test del adoption (manual)**: insertar break point entre `client.CreateTodoListAsync` y `_db.SaveChangesAsync`. Reiniciar el proceso. El próximo pull adopta el orphan (un único mapping creado, sin re-POST).
5. **NOTES.md** actualizado: nueva entrada en Decision Log + secciones formales sincronizadas. Confirmar que el README/CHALLENGE.md no se tocaron (CHALLENGE.md inmutable).
6. Commit por slice (no antes de la verificación 1+2 verdes).

## Riesgos y plan B

- **El JSON deserializer no infiere `Kind=Utc` en `created_at`/`updated_at`** → comparaciones con `DateTime.UtcNow` salen torcidas. Mitigación: agregar test que verifica el `Kind` y, si es necesario, `DateTimeStyles.AssumeUniversal` en `ExternalJsonOptions` o switchear a `DateTimeOffset`.
- **Server externo SÍ respeta `Idempotency-Key` sin documentarlo** — improbable, downside cero.
- **`updated_at` no estrictamente monotónico** (clock skew) — flapping potencial. Mitigación: si aparece en E2E, agregar tolerancia (±1s) y documentar como edge case. No agregar tolerancia preemptiva.
- **Tests con `StubHttpMessageHandler` necesitan more diferenciar por path/method** — extensión trivial si todavía no soporta.
- **`UpdatedAt` en TodoList rompe respuestas previas del controller GET** (cliente del API local recibe campo nuevo). Es additive, sin breaking change. Confirmar que ningún test del API local verifica shape exacto del response — si lo hace, ajustar.
