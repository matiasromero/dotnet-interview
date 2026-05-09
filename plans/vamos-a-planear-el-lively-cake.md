# Slice 3 — Sync de TodoListItem (bidireccional, asimetría POST resuelta)

> **Para agentic workers:** REQUIRED SUB-SKILL: usar `superpowers:executing-plans` o `superpowers:subagent-driven-development` para ejecutar tarea por tarea. Cadencia TDD por tarea (write failing test → run/fail → implement → run/pass → commit) según `superpowers:test-driven-development`. Verificación final con `dotnet test` + `dotnet csharpier --check .`.

## Context

El sync engine ya cubre `TodoList` bidireccional (slice 1: PUSH; slice 2: PULL + last-write-wins + adoption + Idempotency-Key). Slice 3 incorpora `TodoListItem`. La pieza dura es la **asimetría del contrato externo**: existen `PATCH /todolists/{listId}/todoitems/{itemId}` y `DELETE /todolists/{listId}/todoitems/{itemId}`, pero **no existe `POST /todolists/{listId}/todoitems`** — los items solo se crean externamente embebidos en el `POST /todolists` inicial.

**Política para esa asimetría (decidida en brainstorming):** items locales nuevos creados en una lista que ya fue pusheada al externo se loguean Warning + no-sync. Documentar como limitación + agregar a sugerencias para el server externo (CHALLENGE.md invita a proponer cambios).

Spec OpenAPI verificada en [`assets/external-api.yaml`](assets/external-api.yaml).

## Decisiones cerradas (no replantear)

1. **Scope**: bidireccional simétrico al slice 2. Embebido en POST inicial + PATCH local→external + DELETE local→external (huérfanos detectados) + PULL con LWW + adoption + create local desde external.
2. **Detección de DELETE local**: hard-delete normal del service + anti-join detecta mappings huérfanos en cada tick. NO tombstone (mantiene `TodoListItem` libre del concern de sync, consistente con slice 1).
3. **Service boundary**: `ITodoListItemSyncService` propio. Push y pull de items en su clase. El `TodoListSyncService.PullTodoListsAsync` cambia signature a tupla `(SyncRunResult, IReadOnlyList<ExternalListWithMapping>)` y el `SyncBackgroundService` orquesta los dos pulls.
4. **Re-parenting**: fuera de scope. Edge case documentado.
5. **Idempotency-Key**: solo en POST inicial (ya existe). PATCH/DELETE no lo mandan.
6. **Política items late**: log Warning + no-sync. Reportar al externo en `NOTES.md`.
7. **Detección de DELETE externo de items mapeados**: fuera de scope (slice 4). Documentar como deuda.

## Architectural overview

```
SyncBackgroundService (cada tick)
  ├─ listSync.PushTodoListsAsync()           ← MODIFICADO: items embebidos en POST inicial
  ├─ itemSync.PushTodoListItemsAsync()       ← NUEVO: PATCH local→external + DELETE huerfanos + WARN late
  ├─ (listResult, mappedExternals) = await listSync.PullTodoListsAsync()  ← MODIFICADO: devuelve tupla
  └─ itemSync.PullTodoListItemsAsync(mappedExternals)                     ← NUEVO: LWW de items
```

**SyncMapping** se reusa (discriminado por `EntityType`); se agrega `ParentExternalId nvarchar(64) NULL` para que el DELETE de items pueda construir el path padre.

**TodoListItem** gana `DateTime UpdatedAt` (espejo del slice 2 sobre `TodoList`).

## File map

### Crear
- `TodoApi.Sync/External/Models/UpdateExternalTodoItemRequest.cs`
- `TodoApi.Sync/Models/LocalTodoListItemRecord.cs`
- `TodoApi.Sync/Models/MappedTodoListItemRecord.cs`
- `TodoApi.Sync/Models/OrphanedItemMapping.cs`
- `TodoApi.Sync/Models/ApplyExternalItemCreatePlan.cs`
- `TodoApi.Sync/Models/ApplyRemoteWinsItemPlan.cs`
- `TodoApi.Sync/Models/PersistEmbeddedItemMappingsPlan.cs` (con `EmbeddedItemMapping`)
- `TodoApi.Sync/Models/EmbeddedExternalItem.cs`
- `TodoApi.Sync/Models/ExternalListWithMapping.cs`
- `TodoApi.Sync/Services/ITodoListItemSyncService.cs`
- `TodoApi.Sync/Services/TodoListItemSyncService.cs`
- `TodoApi.Tests/Sync/Services/TodoListItemSyncServiceTests.cs`
- Migración EF: `AddTodoListItemUpdatedAtAndItemSyncSupport` (consolidada)

### Modificar
- `TodoApi/Models/TodoListItem.cs` — agregar `DateTime UpdatedAt`
- `TodoApi/Services/TodoListItemService.cs:53` (Create) y `:93-94` (Update) — setear `UpdatedAt`
- `TodoApi/Data/TodoContext.cs` — `OnModelCreating` para `TodoListItem.UpdatedAt` + `SyncMapping.ParentExternalId`; implementar nuevos métodos del `ISyncDbContext`; extender `LocalTodoListRecord` con items y `ApplyExternalCreateAsync` con embedded items
- `TodoApi.Sync/External/IExternalTodoListClient.cs` — agregar `UpdateTodoItemAsync`, `DeleteTodoItemAsync`
- `TodoApi.Sync/External/ExternalTodoListClient.cs` — implementar los dos nuevos
- `TodoApi.Sync/Data/ISyncDbContext.cs` — agregar métodos para items
- `TodoApi.Sync/Models/SyncMapping.cs` — agregar `ParentExternalId`
- `TodoApi.Sync/Models/LocalTodoListRecord.cs` — extender con `Items: IReadOnlyList<LocalTodoListItemRecord>`
- `TodoApi.Sync/Models/ApplyExternalCreatePlan.cs` — extender con `Items: IReadOnlyList<EmbeddedExternalItem>`
- `TodoApi.Sync/Services/ITodoListSyncService.cs` y `TodoListSyncService.cs` — `PullTodoListsAsync` devuelve tupla; `PushTodoListsAsync` embebe items y persiste sus mappings; constructor agrega `ITodoListItemSyncService`
- `TodoApi.Sync/Hosting/SyncBackgroundService.cs` — resolver dos services + 4 etapas
- `TodoApi.Sync/DependencyInjection/SyncServiceCollectionExtensions.cs` — registrar `ITodoListItemSyncService`
- `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs` — destructurar tupla en pull tests + nuevo test `PushTodoListsAsync_ListWithLocalItems_PostsEmbeddedAndPersistsItemMappings`
- `TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs` — 5 tests nuevos
- `TodoApi.Tests/Services/TodoListItemServiceTests.cs` — agregar `UpdatedAt = DateTime.UtcNow` en `PopulateDatabaseContext` + 1 assert opcional en Create/Update
- `NOTES.md` — entrada en Decision Log + Edge Cases + Areas for Improvement (slice 3 cerrado, slice 4 promovido)

## Implementation tasks

Cada tarea sigue cadencia TDD: escribir test fallando → correrlo (esperar fail) → implementar → correr (esperar pass) → commit. El detalle por tarea muestra los **artefactos** y los **tests críticos**; los pasos TDD se aplican uniforme.

---

### Task 1 — Schema: UpdatedAt + ParentExternalId + migración

**Files:**
- Modify: `TodoApi/Models/TodoListItem.cs`
- Modify: `TodoApi.Sync/Models/SyncMapping.cs`
- Modify: `TodoApi/Data/TodoContext.cs` (OnModelCreating, líneas ~127-129 índices y configuración)
- Create: `TodoApi/Migrations/<timestamp>_AddTodoListItemUpdatedAtAndItemSyncSupport.cs`

**Changes:**

`TodoListItem` agrega después de `IsCompleted`:
```csharp
public DateTime UpdatedAt { get; set; }
```

`SyncMapping` agrega:
```csharp
public string? ParentExternalId { get; set; }
```

`TodoContext.OnModelCreating`:
```csharp
modelBuilder.Entity<TodoListItem>()
    .Property(i => i.UpdatedAt)
    .HasDefaultValueSql("GETUTCDATE()");

modelBuilder.Entity<SyncMapping>()
    .Property(m => m.ParentExternalId)
    .HasMaxLength(64);
```

Migración EF (`dotnet ef migrations add AddTodoListItemUpdatedAtAndItemSyncSupport --project TodoApi`):
```csharp
migrationBuilder.AddColumn<DateTime>(
    name: "UpdatedAt",
    table: "TodoListItem",
    type: "datetime2",
    nullable: false,
    defaultValueSql: "GETUTCDATE()");

migrationBuilder.AddColumn<string>(
    name: "ParentExternalId",
    table: "SyncMappings",
    type: "nvarchar(64)",
    maxLength: 64,
    nullable: true);
```

**Tests:** ninguno propio (compila + restantes tests verdes). InMemory no aplica `defaultValueSql`; los `UpdatedAt` se setean explícito en code paths.

**Verify:** `dotnet build && dotnet test` — todo verde excepto eventual breakage por `LocalTodoListRecord` (cubierto en Task 5).

**Commit:** `feat(sync): add UpdatedAt to TodoListItem and ParentExternalId to SyncMapping`

---

### Task 2 — TodoListItemService setea UpdatedAt

**Files:**
- Modify: `TodoApi/Services/TodoListItemService.cs:53` (Create) y línea ~93-94 (Update)
- Modify: `TodoApi.Tests/Services/TodoListItemServiceTests.cs` (PopulateDatabaseContext + 2 asserts opcionales)

**Changes:**

`CreateAsync`, en el `new TodoListItem { ... }`:
```csharp
UpdatedAt = DateTime.UtcNow
```

`UpdateAsync`, después de `item.IsCompleted = dto.IsCompleted;`:
```csharp
item.UpdatedAt = DateTime.UtcNow;
```

`PopulateDatabaseContext` en tests — agregar `UpdatedAt = DateTime.UtcNow` a las instancias hardcoded de `TodoListItem`.

**Tests críticos a agregar (asserts):**
- `CreateAsync_WhenTodoListExists_CreatesItem` — agregar `Assert.True(result.UpdatedAt > DateTime.MinValue);`
- `UpdateAsync_WhenIdExists_UpdatesItem` — capturar `before = item.UpdatedAt;` antes del Update, después assert `Assert.True(updated.UpdatedAt >= before);`

**Verify:** `dotnet test --filter "FullyQualifiedName~TodoListItemServiceTests"` verde.

**Commit:** `feat(todoitems): set UpdatedAt on Create and Update`

---

### Task 3 — DTO externo nuevo: UpdateExternalTodoItemRequest

**Files:**
- Create: `TodoApi.Sync/External/Models/UpdateExternalTodoItemRequest.cs`

**Changes:**
```csharp
namespace TodoApi.Sync.External.Models;

public record UpdateExternalTodoItemRequest(string Description, bool Completed);
```

**Tests:** ninguno (record sin lógica).

**Commit:** `feat(sync): add UpdateExternalTodoItemRequest DTO`

---

### Task 4 — Cliente externo: UpdateTodoItemAsync + DeleteTodoItemAsync

**Files:**
- Modify: `TodoApi.Sync/External/IExternalTodoListClient.cs`
- Modify: `TodoApi.Sync/External/ExternalTodoListClient.cs`
- Modify: `TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs`

**Changes en interface:**
```csharp
Task<ExternalTodoItem> UpdateTodoItemAsync(
    string externalListId,
    string externalItemId,
    UpdateExternalTodoItemRequest request,
    CancellationToken cancellationToken);

Task DeleteTodoItemAsync(
    string externalListId,
    string externalItemId,
    CancellationToken cancellationToken);
```

**Implementación** — espejar `UpdateTodoListAsync`:
```csharp
public async Task<ExternalTodoItem> UpdateTodoItemAsync(
    string externalListId, string externalItemId,
    UpdateExternalTodoItemRequest request, CancellationToken ct)
{
    var path = $"todolists/{externalListId}/todoitems/{externalItemId}";
    using var msg = new HttpRequestMessage(HttpMethod.Patch, path)
    {
        Content = JsonContent.Create(request, options: ExternalJsonOptions.Default)
    };
    using var response = await _http.SendAsync(msg, ct);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ExternalApiException(
            $"PATCH {path} failed with {(int)response.StatusCode}",
            (int)response.StatusCode, "PATCH", path, body);
    }
    var result = await response.Content.ReadFromJsonAsync<ExternalTodoItem>(
        ExternalJsonOptions.Default, ct);
    return result ?? throw new ExternalApiException(
        $"PATCH {path} returned empty body",
        (int)response.StatusCode, "PATCH", path, null);
}

public async Task DeleteTodoItemAsync(
    string externalListId, string externalItemId, CancellationToken ct)
{
    var path = $"todolists/{externalListId}/todoitems/{externalItemId}";
    using var response = await _http.DeleteAsync(path, ct);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ExternalApiException(
            $"DELETE {path} failed with {(int)response.StatusCode}",
            (int)response.StatusCode, "DELETE", path, body);
    }
}
```

**Tests nuevos** (5):
1. `UpdateTodoItemAsync_HappyPath_PatchesAndDeserializesResponse` — verifica path `/todolists/lst-1/todoitems/itm-9`, body con `description` + `completed` snake_case, deserializa response con `Kind = Utc`.
2. `UpdateTodoItemAsync_404Response_ThrowsExternalApiException` — assert `Method == "PATCH"`, `StatusCode == 404`.
3. `UpdateTodoItemAsync_EmptyBody_ThrowsExternalApiException`.
4. `DeleteTodoItemAsync_HappyPath_Returns204_NoThrow`.
5. `DeleteTodoItemAsync_404Response_ThrowsExternalApiException` — assert `Method == "DELETE"`.

**Verify:** `dotnet test --filter "FullyQualifiedName~ExternalTodoListClientTests"` verde.

**Commit:** `feat(sync): add UpdateTodoItemAsync and DeleteTodoItemAsync to external client`

---

### Task 5 — Records y Plans del sync de items + extender LocalTodoListRecord/ApplyExternalCreatePlan

**Files:**
- Create: `TodoApi.Sync/Models/LocalTodoListItemRecord.cs`
- Create: `TodoApi.Sync/Models/MappedTodoListItemRecord.cs`
- Create: `TodoApi.Sync/Models/OrphanedItemMapping.cs`
- Create: `TodoApi.Sync/Models/ApplyExternalItemCreatePlan.cs`
- Create: `TodoApi.Sync/Models/ApplyRemoteWinsItemPlan.cs`
- Create: `TodoApi.Sync/Models/PersistEmbeddedItemMappingsPlan.cs`
- Create: `TodoApi.Sync/Models/EmbeddedExternalItem.cs`
- Create: `TodoApi.Sync/Models/ExternalListWithMapping.cs`
- Modify: `TodoApi.Sync/Models/LocalTodoListRecord.cs` — agregar `Items`
- Modify: `TodoApi.Sync/Models/ApplyExternalCreatePlan.cs` — agregar `Items`

**Shapes (resumen):**
```csharp
public record LocalTodoListItemRecord(long Id, string Description, bool IsCompleted, DateTime UpdatedAt);

public record LocalTodoListRecord(
    long Id, string Name, DateTime UpdatedAt,
    IReadOnlyList<LocalTodoListItemRecord> Items);

public record MappedTodoListItemRecord(
    long MappingId, long LocalId, string ExternalItemId, string ParentExternalId,
    Guid IdempotencyKey, DateTime LastSyncedAt,
    DateTime? LocalUpdatedAtAtSync, DateTime? ExternalUpdatedAtAtSync,
    string CurrentDescription, bool CurrentIsCompleted, DateTime CurrentLocalUpdatedAt);

public record OrphanedItemMapping(long MappingId, string ExternalItemId, string ParentExternalId);

public record EmbeddedExternalItem(
    string ExternalItemId, string Description, bool Completed, DateTime ExternalUpdatedAt);

public record ApplyExternalCreatePlan(
    string ExternalId, string Name, DateTime ExternalUpdatedAt, Guid IdempotencyKey,
    IReadOnlyList<EmbeddedExternalItem> Items);

public record ApplyExternalItemCreatePlan(
    long ParentLocalId, string ParentExternalId,
    string ExternalItemId, string Description, bool Completed,
    DateTime ExternalUpdatedAt, Guid IdempotencyKey);

public record ApplyRemoteWinsItemPlan(
    long MappingId, long LocalId,
    string NewDescription, bool NewCompleted, DateTime ExternalUpdatedAt);

public record EmbeddedItemMapping(
    long LocalItemId, string ExternalItemId,
    DateTime LocalUpdatedAt, DateTime ExternalUpdatedAt);

public record PersistEmbeddedItemMappingsPlan(
    string ParentExternalId, IReadOnlyList<EmbeddedItemMapping> Items);

public record ExternalListWithMapping(
    ExternalTodoList External, long ParentLocalId, string ParentExternalId);
```

**Compilación:** romperá `LocalTodoListRecord` y `ApplyExternalCreatePlan` en su uso actual (no en tests directos pero sí en los call sites internos del `TodoContext`). Las próximas tareas (6) los implementan.

**Commit:** `feat(sync): add records and plans for TodoListItem sync`

---

### Task 6 — ISyncDbContext: nuevos métodos para items

**Files:**
- Modify: `TodoApi.Sync/Data/ISyncDbContext.cs`
- Modify: `TodoApi/Data/TodoContext.cs`

**Cambios en `LocalTodoListRecord` consumers** (compatibilidad):
- `GetUnmappedTodoListsAsync` proyecta ahora con `Items` poblado:
```csharp
public async Task<List<LocalTodoListRecord>> GetUnmappedTodoListsAsync(CancellationToken ct)
{
    return await TodoList
        .Where(l => !SyncMappings.Any(m => m.EntityType == SyncEntityType.TodoList && m.LocalId == l.Id))
        .Select(l => new LocalTodoListRecord(
            l.Id, l.Name, l.UpdatedAt,
            l.Items.Select(i => new LocalTodoListItemRecord(
                i.Id, i.Description, i.IsCompleted, i.UpdatedAt)).ToList()))
        .ToListAsync(ct);
}
```
- `FindUnmappedLocalByIdAsync` análogo (con `Items`).
- `ApplyExternalCreateAsync` consume el plan extendido: crea TodoList + sus TodoListItems + mappings de list + mappings de items (todo en sequence con `SaveChanges`).

**Métodos nuevos en `ISyncDbContext`:**
```csharp
Task<List<LocalTodoListItemRecord>> GetUnmappedTodoListItemsWithMappedParentAsync(CancellationToken ct);
// Items SIN mapping cuya LISTA padre SÍ tiene mapping. Para log Warning.

Task<List<MappedTodoListItemRecord>> GetMappedTodoListItemsAsync(CancellationToken ct);
// Triple-join: SyncMapping(item) ⋈ TodoListItem ⋈ SyncMapping(list-of-item).
// El ParentExternalId se resuelve desde el SyncMapping.ParentExternalId del item.

Task<List<OrphanedItemMapping>> GetOrphanedItemMappingsAsync(CancellationToken ct);
// Anti-join: SyncMapping(EntityType=TodoListItem) WHERE NOT EXISTS TodoListItem(Id = LocalId).

Task<LocalTodoListItemRecord?> FindUnmappedLocalItemByIdAsync(
    long localId, long parentListId, CancellationToken ct);

Task ApplyExternalItemCreateAsync(ApplyExternalItemCreatePlan plan, CancellationToken ct);
Task ApplyRemoteWinsItemAsync(ApplyRemoteWinsItemPlan plan, CancellationToken ct);
Task PersistEmbeddedItemMappingsAsync(PersistEmbeddedItemMappingsPlan plan, CancellationToken ct);
```

**Implementación clave en `TodoContext` — anti-join huérfanos:**
```csharp
public async Task<List<OrphanedItemMapping>> GetOrphanedItemMappingsAsync(CancellationToken ct)
{
    return await SyncMappings
        .Where(m => m.EntityType == SyncEntityType.TodoListItem
                 && !TodoListItem.Any(i => i.Id == m.LocalId))
        .Select(m => new OrphanedItemMapping(m.Id, m.ExternalId, m.ParentExternalId!))
        .ToListAsync(ct);
}
```

**Implementación de `GetMappedTodoListItemsAsync`** — join contra `TodoListItem` por `LocalId`:
```csharp
return await SyncMappings
    .Where(m => m.EntityType == SyncEntityType.TodoListItem)
    .Join(TodoListItem, m => m.LocalId, i => i.Id,
        (m, i) => new MappedTodoListItemRecord(
            m.Id, m.LocalId, m.ExternalId, m.ParentExternalId!,
            m.IdempotencyKey, m.LastSyncedAt,
            m.LocalUpdatedAtAtSync, m.ExternalUpdatedAtAtSync,
            i.Description, i.IsCompleted, i.UpdatedAt))
    .ToListAsync(ct);
```

**Tests:** ninguno propio del DbContext (cubiertos indirectamente por los tests del service que ejercen estos métodos contra InMemory).

**Verify:** `dotnet build` verde + tests existentes verdes.

**Commit:** `feat(sync): add ISyncDbContext methods for TodoListItem sync`

---

### Task 7 — TodoListItemSyncService: PUSH (PATCH + DELETE huerfanos + WARN late)

**Files:**
- Create: `TodoApi.Sync/Services/ITodoListItemSyncService.cs`
- Create: `TodoApi.Sync/Services/TodoListItemSyncService.cs`
- Create: `TodoApi.Tests/Sync/Services/TodoListItemSyncServiceTests.cs`

**Interface:**
```csharp
public interface ITodoListItemSyncService
{
    Task<SyncRunResult> PushTodoListItemsAsync(CancellationToken ct);
    Task<SyncRunResult> PullTodoListItemsAsync(
        IReadOnlyList<ExternalListWithMapping> mappedExternals, CancellationToken ct);
}
```

**Push flow** (ver Plan agent E.2 para el detalle):
1. Abrir `SyncRun(EntityType=TodoListItem, Direction=Push, Status=Running)`.
2. WARN para items en `GetUnmappedTodoListItemsWithMappedParentAsync` (no incrementan totales).
3. Iterar `GetMappedTodoListItemsAsync`: si `CurrentLocalUpdatedAt > LocalUpdatedAtAtSync` → `UpdateTodoItemAsync(ParentExternalId, ExternalItemId, ...)` → bump snapshots → save. Try/catch por item. `processed++` o `failed++`.
4. Iterar `GetOrphanedItemMappingsAsync`: `DeleteTodoItemAsync(ParentExternalId, ExternalItemId)` → borrar mapping → save. Catch `ExternalApiException` con `StatusCode == 404` → log Info "ya borrado" + borrar mapping + `processed++`. Otros → `failed++`.
5. Cerrar SyncRun: `Total = mapped + orphans` (warnings NO suman); `Status = Succeeded | Partial | Failed` con misma semántica que slice 1+2.

**Tests del PUSH (xUnit + InMemory + Moq strict, mismo patrón que slice 2):**
- `PushTodoListItemsAsync_NoItems_ReturnsZeroAndSucceeded`
- `PushTodoListItemsAsync_UnmappedItemsWithMappedParent_LogsWarningAndDoesNotCallClient`
- `PushTodoListItemsAsync_MappedItemLocalChanged_PatchesExternal` — verifica path/body y bumps de snapshots.
- `PushTodoListItemsAsync_MappedItemNoLocalChanges_DoesNotPatch`
- `PushTodoListItemsAsync_OrphanMapping_DeletesExternalAndRemovesMapping`
- `PushTodoListItemsAsync_OrphanMappingExternal404_TreatsAsResolved`
- `PushTodoListItemsAsync_OneOfThreeFails_StatusPartial`
- `PushTodoListItemsAsync_AllFail_StatusFailed`

**Verify:** `dotnet test --filter "PushTodoListItems"` verde.

**Commit:** `feat(sync): TodoListItemSyncService push (PATCH/DELETE/WARN)`

---

### Task 8 — TodoListItemSyncService: PULL (LWW de items)

**Files:**
- Modify: `TodoApi.Sync/Services/TodoListItemSyncService.cs` — agregar PullTodoListItemsAsync + helpers
- Modify: `TodoApi.Tests/Sync/Services/TodoListItemSyncServiceTests.cs` — tests del pull

**Pull flow:**
1. Abrir `SyncRun(EntityType=TodoListItem, Direction=Pull, Status=Running)`.
2. `mappedItems = await db.GetMappedTodoListItemsAsync(ct)` → indexar por `ExternalItemId`.
3. Aplanar `mappedExternals` a `(item, parentLocalId, parentExternalId)`.
4. Foreach external item, try/catch:
   - **CASO A (mapped)**: `ReconcileMappedItem(item, mapped, ct)` — LWW por `UpdatedAt`. Tie → external (regla `>=`).
   - **CASO B (adoption)**: `long.TryParse(item.SourceId, out localId)` && `db.FindUnmappedLocalItemByIdAsync(localId, parentLocalId)` → mapping nuevo + bumps.
   - **CASO C (create local)**: `db.ApplyExternalItemCreateAsync(plan)`.
   - `processed++` o `failed++`.
5. Cerrar SyncRun.

**Helpers privados:**
- `ReconcileMappedItem(ExternalTodoItem, MappedTodoListItemRecord, ct)` — ramas remoteWins/localWins/bump.
- `PatchExternalItemAsync(MappedTodoListItemRecord, ct)` — `UpdateTodoItemAsync(parentExternalId, externalItemId, new UpdateExternalTodoItemRequest(desc, completed))` + bump snapshots.
- `BumpItemLastSyncedAsync(mappingId, ct)`.
- `AdoptOrphanItemAsync(LocalTodoListItemRecord, ExternalTodoItem, parentExternalId, ct)`.
- `CreateLocalItemFromExternalAsync(ExternalTodoItem, parentLocalId, parentExternalId, ct)`.

**Tests del PULL:**
- `PullTodoListItemsAsync_NoExternalItems_ReturnsZeroAndSucceeded`
- `PullTodoListItemsAsync_ExternalWithUnknownSourceId_CreatesLocalItem`
- `PullTodoListItemsAsync_ExternalWithLocalSourceIdNoMapping_AdoptsAsMapping`
- `PullTodoListItemsAsync_MappedExternalNewer_UpdatesLocal`
- `PullTodoListItemsAsync_MappedLocalNewer_PatchesExternal`
- `PullTodoListItemsAsync_MappedBothChanged_ExternalWinsOnTimestamp`
- `PullTodoListItemsAsync_MappedBothChanged_LocalWinsOnTimestamp`
- `PullTodoListItemsAsync_MappedBothChanged_TieGoesToExternal`
- `PullTodoListItemsAsync_MappedNoChanges_BumpsLastSyncedOnly`
- `PullTodoListItemsAsync_OneOfThreeFails_StatusPartial`

**Verify:** `dotnet test --filter "PullTodoListItems"` verde.

**Commit:** `feat(sync): TodoListItemSyncService pull (LWW + adoption + create)`

---

### Task 9 — TodoListSyncService.PushTodoListsAsync: items embebidos + mappings

**Files:**
- Modify: `TodoApi.Sync/Services/TodoListSyncService.cs` — sección del POST inicial (líneas ~49-70)
- Modify: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs` — un test nuevo + ajuste menor a tests existentes que ya pasan

**Cambio en push** (línea ~49):
```csharp
var external = await _client.CreateTodoListAsync(
    new CreateExternalTodoListRequest(
        SourceId: local.Id.ToString(),
        Name: local.Name,
        Items: local.Items
            .Select(i => new CreateExternalTodoItemRequest(
                i.Id.ToString(), i.Description, i.IsCompleted))
            .ToList()),
    idempotencyKey,
    ct);
```

Después de persistir el mapping del list (línea ~70):
```csharp
if (external.Items.Count > 0)
{
    var embeddedMappings = new List<EmbeddedItemMapping>();
    foreach (var ei in external.Items)
    {
        if (!long.TryParse(ei.SourceId, out var localItemId))
        {
            _log.LogWarning("External item {ExtId} returned with non-parseable source_id; skipping mapping", ei.Id);
            continue;
        }
        var localItem = local.Items.SingleOrDefault(li => li.Id == localItemId);
        if (localItem is null)
        {
            _log.LogWarning("External item {ExtId} source_id {SourceId} does not match any pushed local item", ei.Id, ei.SourceId);
            continue;
        }
        embeddedMappings.Add(new EmbeddedItemMapping(
            localItemId, ei.Id, localItem.UpdatedAt, ei.UpdatedAt));
    }
    if (embeddedMappings.Count > 0)
    {
        await _db.PersistEmbeddedItemMappingsAsync(
            new PersistEmbeddedItemMappingsPlan(external.Id, embeddedMappings), ct);
    }
}
```

**Tests:**
- Nuevo: `PushTodoListsAsync_ListWithLocalItems_PostsEmbeddedAndPersistsItemMappings` — assert que `req.Items` contiene los items locales con sus `source_id` (string del `Id` local), y que después del save existen `SyncMapping`s con `EntityType=TodoListItem` para cada item.
- Nuevo: `PushTodoListsAsync_ListWithoutLocalItems_PostsEmptyItemsArray` — regression.
- Tests existentes del push que pasan `Array.Empty` siguen verdes.

**Verify:** `dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests"` verde.

**Commit:** `feat(sync): include local items in initial POST and persist embedded item mappings`

---

### Task 10 — TodoListSyncService.PullTodoListsAsync: tupla de retorno + invocar item pull

**Files:**
- Modify: `TodoApi.Sync/Services/ITodoListSyncService.cs` — cambiar signature
- Modify: `TodoApi.Sync/Services/TodoListSyncService.cs` — devolver tupla
- Modify: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs` — destructurar en cada test del pull (~12 tests, cambio mecánico)

**Signature nueva:**
```csharp
Task<(SyncRunResult Result, IReadOnlyList<ExternalListWithMapping> MappedExternals)> PullTodoListsAsync(CancellationToken ct);
```

**Cambio interno:** acumular `mappedExternals` durante el loop:
- CASO A (mapped) → `mappedExternals.Add(new(external, mapped.LocalId, external.Id))`.
- CASO B (adoption) → `mappedExternals.Add(new(external, orphan.Id, external.Id))`.
- CASO C (create local) → NO agregar (los items embebidos ya se persistieron en `ApplyExternalCreateAsync` con el plan extendido).

**Pull task ya cubre embedded items en el create**: el `ApplyExternalCreateAsync` extendido en Task 6 crea `TodoListItem`s locales + sus mappings con los items embedded del external. Eso significa que el caso C ya está sincronizado al cierre del list pull. Solo CASOS A y B necesitan que el item pull los procese.

**Adjustments en tests existentes:**
```csharp
var (result, _) = await service.PullTodoListsAsync(CancellationToken.None);
// El segundo elemento se ignora en los tests del list pull.
```

**Verify:** `dotnet test --filter "PullTodoLists"` verde.

**Commit:** `refactor(sync): PullTodoListsAsync returns tuple with mapped externals`

---

### Task 11 — DI + SyncBackgroundService: 4 etapas por tick

**Files:**
- Modify: `TodoApi.Sync/DependencyInjection/SyncServiceCollectionExtensions.cs`
- Modify: `TodoApi.Sync/Hosting/SyncBackgroundService.cs`
- Modify: `TodoApi.Tests/Sync/Hosting/SyncBackgroundServiceTests.cs` (si existe — smoke test)

**DI:**
```csharp
services.AddScoped<ITodoListItemSyncService, TodoListItemSyncService>();
```

**SyncBackgroundService.ExecuteAsync** — orden por tick (cada bloque en try/catch):
```csharp
var listSync = scope.ServiceProvider.GetRequiredService<ITodoListSyncService>();
var itemSync = scope.ServiceProvider.GetRequiredService<ITodoListItemSyncService>();

try { var pushList = await listSync.PushTodoListsAsync(stoppingToken); _log.LogInformation(...); }
catch (Exception ex) { _log.LogError(ex, "List push failed"); }

try { var pushItem = await itemSync.PushTodoListItemsAsync(stoppingToken); _log.LogInformation(...); }
catch (Exception ex) { _log.LogError(ex, "Item push failed"); }

IReadOnlyList<ExternalListWithMapping> mappedExternals = Array.Empty<ExternalListWithMapping>();
try
{
    var (pullList, mes) = await listSync.PullTodoListsAsync(stoppingToken);
    mappedExternals = mes;
    _log.LogInformation(...);
}
catch (Exception ex) { _log.LogError(ex, "List pull failed"); }

if (mappedExternals.Count > 0)
{
    try { var pullItem = await itemSync.PullTodoListItemsAsync(mappedExternals, stoppingToken); _log.LogInformation(...); }
    catch (Exception ex) { _log.LogError(ex, "Item pull failed"); }
}
```

**Verify:** `dotnet test` completo verde + smoke test del background service start/stop.

**Commit:** `feat(sync): orchestrate four-phase tick (push list, push item, pull list, pull item)`

---

### Task 12 — NOTES.md: Decision Log + Edge Cases + Areas

**Files:**
- Modify: `NOTES.md` — append entrada en Decision Log + checkboxes en Edge Cases + actualizar Areas for Improvement

**Decision Log entry (template):**
```
### 2026-05-09 — Slice 3: Sync de TodoListItem (asimetría POST + bidireccional)

- **Decisión:** ...
- **Alternativas descartadas:** ...
- **Por qué:** ...
- **Supuestos nuevos:**
  - El server externo preserva `source_id` literalmente para items embebidos (igual que para lists).
  - El `ParentExternalId` de un item no cambia durante su vida (servidor no re-asigna).
  - Items locales nuevos en lista ya pusheada se loguean Warning + no-sync; el sistema NO recupera esos items hasta que se agregue `POST /todolists/{listId}/todoitems` al contrato externo.
  - Re-parenting de items entre listas no está soportado por ningún lado (ni el local Update, ni el contrato externo).
- **Deuda / follow-ups:**
  - DELETE bidireccional completo (slice 4) — incluye delete externo de lists Y items, mapping huérfano por desaparición del externo.
  - Outbox formal para garantía exactly-once en items embedded del POST inicial (mismo gap microscópico que en list).
  - Sugerencia para el server externo: agregar `POST /todolists/{listId}/todoitems`. Bloquea la sincronización de items new en listas ya pusheadas.
```

**Edge Cases nuevos en NOTES.md** (lista completa en sección J del design technique; copiar y agregar a la sección "Edge Cases"):

1. Item local nuevo en lista ya pusheada → Warning + no-sync (limitación del contrato).
2. Re-parenting (item movido entre listas) → out of scope; documentar.
3. DELETE externo de item mapeado → fuera de scope (slice 4).
4. `source_id` no preservado en items embebidos del POST → Warning + skip mapping; duplicación local en próximo pull.
5. Items dentro de lista huérfana adoptada → manejo natural por CASO B/C de items.
6. Crash entre los dos `SaveChanges` de `ApplyExternalCreateAsync` extendido → pull lo cura como CASO C de items.
7. `UpdatedAt` del item independiente del list → comportamiento correcto (timeline propio).
8. Items con descripción muy larga → asunción heredada (FluentValidation cubre local).
9. Tie-break LWW exacto en items → externo gana (regla `>=`).
10. DELETE local de item mientras está siendo PATCHeado → race aceptable (single-instance).

**Areas for Improvement** — actualizar:
- ~~**Slice 3 — Sync de `TodoListItem`.**~~ **Cerrado.** Bidireccional con la limitación documentada de items new en list pushed.
- **Slice 4 — DELETE bidireccional.** Promovido a próximo. Detección de deletes externos de lists Y items.
- Nuevo: **Sugerencia al contrato externo: `POST /todolists/{listId}/todoitems`**. Bloquea casos legítimos de items late.

**Commit:** `docs(notes): close slice 3 — TodoListItem sync (asymmetric POST documented)`

---

### Task 13 — Verificación E2E

**No-code task. Validar antes de cerrar:**

1. `dotnet build` sin warnings.
2. `dotnet test` — todos los tests verdes (~50+ tests nuevos, ~12 ajustados, todo el resto sin tocar).
3. `dotnet csharpier --check .` — sin formatting drift.
4. (Opcional) Smoke E2E manual: levantar la API externa via docker compose (repo upstream `crunchloop/challenge-senior-engineer`) y la API local; crear lista local con items, verificar que aparecen embebidos en el externo; modificar item local, verificar PATCH; borrar item local, verificar DELETE; crear item externamente y verificar pull; modificar item externamente y verificar LWW.
5. Confirmar que `NOTES.md` Decision Log tiene entrada del slice 3 y los checkboxes nuevos en Edge Cases.

## Edge cases nuevos (resumen visible)

Ver Task 12 — los edge cases se documentan en `NOTES.md` y se cubren con tests específicos en cada Task 7/8/9. Crítico:
- **Items late** → log Warning sin propagar.
- **Orphan delete con 404** → resolver mapping local.
- **`source_id` no parseable en response** → skip mapping + Warning.
- **Crash mid-write con items embedded** → self-healing en pull.

## Out of scope (explícito)

- Detección de DELETE externo de items mapeados (slice 4 con delete bidireccional unificado).
- Detección de DELETE externo de lists mapeadas (slice 4).
- Re-parenting (cambio de `TodoListId`) en cualquier dirección.
- Workaround destructivo (DELETE+POST) para items late.
- Outbox formal para atomicidad de mapping writes.
- Endpoints manuales `POST /api/sync/run` y métricas Prometheus.

## Verification (post-implementación)

```bash
# 1. Build limpio
dotnet build

# 2. Test suite completo
dotnet test

# 3. Formatting
dotnet csharpier --check .

# 4. Verificar tests específicos del slice
dotnet test --filter "FullyQualifiedName~TodoListItemSyncServiceTests"
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests"
dotnet test --filter "FullyQualifiedName~ExternalTodoListClientTests"

# 5. Migración aplica sin errores (en dev)
dotnet ef database update --project TodoApi
```

**Critical files for review:**
- [TodoApi.Sync/Services/TodoListItemSyncService.cs](TodoApi.Sync/Services/TodoListItemSyncService.cs) (nuevo)
- [TodoApi.Sync/Services/TodoListSyncService.cs](TodoApi.Sync/Services/TodoListSyncService.cs) (push embedded + pull tuple)
- [TodoApi.Sync/Data/ISyncDbContext.cs](TodoApi.Sync/Data/ISyncDbContext.cs)
- [TodoApi/Data/TodoContext.cs](TodoApi/Data/TodoContext.cs)
- [TodoApi.Sync/External/ExternalTodoListClient.cs](TodoApi.Sync/External/ExternalTodoListClient.cs)
- [TodoApi.Sync/Hosting/SyncBackgroundService.cs](TodoApi.Sync/Hosting/SyncBackgroundService.cs)
- [NOTES.md](NOTES.md) (Decision Log + Edge Cases + Areas)
