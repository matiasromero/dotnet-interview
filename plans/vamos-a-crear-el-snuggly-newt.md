# Sync Engine — Slice 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Modelar el sync con la API externa como un sistema desacoplado — proyecto nuevo `TodoApi.Sync` que monta el background trigger, la lógica de sync, y un typed HttpClient resiliente — y entregar la primera dirección funcionando end-to-end: PUSH de TodoLists locales hacia la API externa.

**Architecture:** Class library `TodoApi.Sync` referenciado desde `TodoApi`. Tres etapas: (1) `SyncBackgroundService : BackgroundService` corre cada N segundos, (2) `TodoListSyncService` lee TodoLists locales sin mapping y los sincroniza, (3) `IExternalTodoListClient` typed HttpClient hace las llamadas HTTP con resilience pipeline (retry exponencial + jitter + circuit breaker + timeout per-attempt). Persistencia: dos tablas nuevas (`SyncMapping`, `SyncRun`) en el `TodoContext` existente, expuestas al sync project mediante una interface `ISyncDbContext` para mantener el desacoplamiento. Configuración tipada con `IOptions<T>` (`ExternalApiOptions`, `SyncOptions`). Logging structured con `ILogger<T>` siguiendo el patrón del repo.

**Tech Stack:** .NET 8.0, EF Core 7 + SqlServer (TodoContext compartido), `Microsoft.Extensions.Http.Resilience` (Polly v8 oficial), `Microsoft.Extensions.Hosting`, `IHttpClientFactory` typed clients, `System.Text.Json` con `JsonNamingPolicy.SnakeCaseLower`, xUnit + EF InMemory + Moq para tests.

---

## Context

**Por qué este cambio:** el [`CHALLENGE.md`](../../Repositories/crunchloop/dotnet-interview/CHALLENGE.md) pide sincronización bidireccional con una API externa, resiliente y observable. El usuario decidió modelarlo como un proyecto separado para mantener el TodoApi limpio del concern de sync, y arrancar el roadmap con el slice más pequeño que prueba la arquitectura completa: scaffolding + una sola dirección (PUSH de TodoLists). Slices futuros agregarán PULL, items, deletes y updates encima de la misma plomería.

**Spec externo (resumen del [`docs/external-api.yaml`](../../Repositories/challenge-senior-engineer/docs/external-api.yaml) del upstream):**

- `POST /todolists` — body `{ source_id, name, items[] }` → 201 `{ id, source_id, name, created_at, updated_at, items[] }`. IDs externos son `string`.
- `GET /todolists` — full pull con items inline (sin paginación). _Slice futuro._
- `PATCH/DELETE` — _slices futuros._
- Sin auth.

**Insight clave:** el campo `source_id` permite correlación bidireccional sin crear nada inventado: cuando push'eamos un TodoList local con `source_id = local.Id.ToString()`, el sistema externo lo conserva, y en futuros pulls podemos detectar entries originados localmente sin tabla mágica de mapping. Aún así, mantenemos una tabla `SyncMapping` separada (no inline en `TodoList`) para no acoplar el modelo de dominio al concern de sync.

**Decisiones tomadas (validadas con el usuario):**

| Decisión | Por qué |
|---|---|
| Class library `TodoApi.Sync` hosted en TodoApi (no Worker SDK) | Una sola unidad de despliegue para el challenge; comparte logging/config/host. |
| Tablas `SyncMapping` + `SyncRun` en `TodoContext` existente | Una migration nueva, transacciones simples. Decisión del usuario. |
| `Microsoft.Extensions.Http.Resilience` (Polly v8 oficial) | Integración nativa con `IHttpClientFactory`, telemetría built-in. Polly directo es más verbose. |
| Tabla `SyncMapping` separada vs `ExternalId` inline en `TodoList` | Mantiene el dominio desacoplado del sync. Trade-off: un join extra en read-paths del sync (no es hot path). |
| Save por lista (no batch) | Idempotencia simple. Si crash mid-batch al re-correr, sólo se re-pushean las que no llegaron a guardar mapping. Outbox pattern queda como follow-up. |
| `ISyncDbContext` interface en `TodoApi.Sync`, implementada por `TodoContext` | Evita circular dependency y mantiene el sync agnóstico del DbContext concreto. |
| Moq + handcrafted `StubHttpMessageHandler` para tests | Moq para `IExternalTodoListClient` (interfaces simples), handcrafted handler para HTTP (más limpio que mockear `protected SendAsync`). |

**Out of scope (slices futuros):**
- PULL externo→local + reconciliación (compare `source_id` y `updated_at`).
- Sync de `TodoListItem` (la spec no expone `POST` aislado de items — diseño quirky que merece slice propio).
- DELETE / UPDATE bidireccional + conflict resolution policy.
- Outbox pattern para garantía exactly-once.
- Auth.

---

## File Structure

**Nuevo proyecto `TodoApi.Sync/`:**

```
TodoApi.Sync.csproj                                — class library net8.0
Configuration/
  ExternalApiOptions.cs                            — POCO con DataAnnotations (BaseAddress, retry, timeout)
  SyncOptions.cs                                   — POCO (Interval, StartupDelay, Enabled)
Data/
  ISyncDbContext.cs                                — interface mínima: DbSet<SyncMapping>, DbSet<SyncRun>, SaveChangesAsync
DependencyInjection/
  SyncServiceCollectionExtensions.cs               — AddTodoSync(IConfiguration)
External/
  ExternalApiException.cs                          — excepción custom con StatusCode + Method + Path + Body
  ExternalJsonOptions.cs                           — static class con JsonSerializerOptions snake_case
  IExternalTodoListClient.cs                       — interface typed-client
  ExternalTodoListClient.cs                        — impl typed-client
  Models/
    CreateExternalTodoItemRequest.cs               — record wire DTO
    CreateExternalTodoListRequest.cs               — record wire DTO
    ExternalTodoItem.cs                            — record wire DTO
    ExternalTodoList.cs                            — record wire DTO
Hosting/
  SyncBackgroundService.cs                         — BackgroundService loop, scope-per-tick
Models/
  SyncDirection.cs                                 — enum (Push, Pull)
  SyncEntityType.cs                                — enum (TodoList, TodoListItem)
  SyncMapping.cs                                   — entity: LocalId ↔ ExternalId
  SyncRun.cs                                       — entity: audit log de runs
  SyncRunStatus.cs                                 — enum (Running, Succeeded, Failed, Partial)
Services/
  ITodoListSyncService.cs                          — interface
  SyncRunResult.cs                                 — record (Total, Pushed, Failed, Status)
  TodoListSyncService.cs                           — orquesta el push
```

**Modificaciones a `TodoApi/`:**
- `TodoApi.csproj` — agregar `<ProjectReference>` a `TodoApi.Sync`.
- `Data/TodoContext.cs` — implementa `ISyncDbContext`, agrega `DbSet<SyncMapping>`, `DbSet<SyncRun>`, configura indices en `OnModelCreating`.
- `Migrations/<timestamp>_AddSyncTables.cs` — generada por `dotnet ef`.
- `Program.cs` — una línea: `builder.Services.AddTodoSync(builder.Configuration);`.
- `appsettings.json` — secciones `ExternalApi` y `Sync`.
- `appsettings.Development.json` — override del BaseAddress (gitignored — documentar en NOTES).

**Nuevo en `TodoApi.Tests/`:**
- `TodoApi.Tests.csproj` — `<ProjectReference>` a `TodoApi.Sync` + `Moq` 4.20.x.
- `Sync/Services/TodoListSyncServiceTests.cs`.
- `Sync/External/ExternalTodoListClientTests.cs`.
- `Sync/Hosting/SyncBackgroundServiceTests.cs` (smoke).
- `Sync/TestHelpers/StubHttpMessageHandler.cs`.

---

## Task 1: Bootstrap TodoApi.Sync project

**Files:**
- Create: `TodoApi.Sync/TodoApi.Sync.csproj`
- Modify: `dotnet-interview.sln`
- Modify: `TodoApi/TodoApi.csproj`

- [ ] **Step 1: Crear el proyecto class library**

Run desde la raíz del repo:
```bash
dotnet new classlib -n TodoApi.Sync -f net8.0 -o TodoApi.Sync
```

Borrar el `Class1.cs` autogenerado:
```bash
rm TodoApi.Sync/Class1.cs
```

- [ ] **Step 2: Agregar paquetes a TodoApi.Sync**

```bash
dotnet add TodoApi.Sync package Microsoft.EntityFrameworkCore --version 7.0.0
dotnet add TodoApi.Sync package Microsoft.Extensions.Hosting.Abstractions
dotnet add TodoApi.Sync package Microsoft.Extensions.Http
dotnet add TodoApi.Sync package Microsoft.Extensions.Http.Resilience
dotnet add TodoApi.Sync package Microsoft.Extensions.Options.DataAnnotations
```

Versión EF Core 7 para alinearse con `TodoApi.csproj` (mismo `7.0.0-*` que ya usa el repo). El resto de paquetes Microsoft.* no especifican versión — toman la última estable compatible con net8.0.

- [ ] **Step 3: Sumar el proyecto a la solución**

```bash
dotnet sln dotnet-interview.sln add TodoApi.Sync/TodoApi.Sync.csproj
```

- [ ] **Step 4: Agregar ProjectReference desde TodoApi a TodoApi.Sync**

Editar `TodoApi/TodoApi.csproj` y agregar dentro del último `<ItemGroup>` (o crear uno nuevo):

```xml
<ItemGroup>
  <ProjectReference Include="..\TodoApi.Sync\TodoApi.Sync.csproj" />
</ItemGroup>
```

- [ ] **Step 5: Build clean**

```bash
dotnet build
```
Expected: build succeeds, 0 errors, 0 warnings nuevos.

- [ ] **Step 6: Commit**

```bash
git add TodoApi.Sync/ dotnet-interview.sln TodoApi/TodoApi.csproj
git commit -m "$(cat <<'EOF'
feat(sync): bootstrap TodoApi.Sync class library

Adds new project for the external API sync engine. Empty class library wired
into the solution and referenced from TodoApi. Packages staged for EF Core,
hosting, HttpClient factory, and Polly v8 resilience.
EOF
)"
```

---

## Task 2: Sync entities and enums

**Files:**
- Create: `TodoApi.Sync/Models/SyncDirection.cs`
- Create: `TodoApi.Sync/Models/SyncEntityType.cs`
- Create: `TodoApi.Sync/Models/SyncRunStatus.cs`
- Create: `TodoApi.Sync/Models/SyncMapping.cs`
- Create: `TodoApi.Sync/Models/SyncRun.cs`

- [ ] **Step 1: Crear los enums**

`TodoApi.Sync/Models/SyncEntityType.cs`:
```csharp
namespace TodoApi.Sync.Models;

public enum SyncEntityType
{
    TodoList = 1,
    TodoListItem = 2,
}
```

`TodoApi.Sync/Models/SyncDirection.cs`:
```csharp
namespace TodoApi.Sync.Models;

public enum SyncDirection
{
    Push = 1,
    Pull = 2,
}
```

`TodoApi.Sync/Models/SyncRunStatus.cs`:
```csharp
namespace TodoApi.Sync.Models;

public enum SyncRunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Partial = 4,
}
```

Valores explícitos para que la persistencia sea estable si EF mapea como `int`.

- [ ] **Step 2: Crear `SyncMapping`**

`TodoApi.Sync/Models/SyncMapping.cs`:
```csharp
namespace TodoApi.Sync.Models;

public class SyncMapping
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public long LocalId { get; set; }
    public string ExternalId { get; set; } = null!;
    public DateTime LastSyncedAt { get; set; }
}
```

- [ ] **Step 3: Crear `SyncRun`**

`TodoApi.Sync/Models/SyncRun.cs`:
```csharp
namespace TodoApi.Sync.Models;

public class SyncRun
{
    public long Id { get; set; }
    public SyncEntityType EntityType { get; set; }
    public SyncDirection Direction { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build
```
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add TodoApi.Sync/Models/
git commit -m "feat(sync): add SyncMapping and SyncRun entities"
```

---

## Task 3: ISyncDbContext + TodoContext wiring + EF migration

**Files:**
- Create: `TodoApi.Sync/Data/ISyncDbContext.cs`
- Modify: `TodoApi/Data/TodoContext.cs`
- Create: `TodoApi/Migrations/<timestamp>_AddSyncTables.cs` (autogenerada)

- [ ] **Step 1: Definir `ISyncDbContext`**

`TodoApi.Sync/Data/ISyncDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Data;

public interface ISyncDbContext
{
    DbSet<SyncMapping> SyncMappings { get; }
    DbSet<SyncRun> SyncRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Modificar `TodoContext` para implementar la interface y registrar los DbSets**

Editar `TodoApi/Data/TodoContext.cs`. Agregar:
- `using TodoApi.Sync.Data;` y `using TodoApi.Sync.Models;` arriba.
- `: ISyncDbContext` a la cláusula de herencia (junto a `: DbContext`).
- Dos DbSets nuevos.
- Configuración de indices en `OnModelCreating`.

```csharp
using Microsoft.EntityFrameworkCore;
using TodoApi.Models;
using TodoApi.Sync.Data;
using TodoApi.Sync.Models;

namespace TodoApi.Data;

public class TodoContext : DbContext, ISyncDbContext
{
    public TodoContext(DbContextOptions<TodoContext> options) : base(options) { }

    public DbSet<TodoList> TodoList { get; set; } = null!;
    public DbSet<TodoListItem> TodoListItem { get; set; } = null!;
    public DbSet<SyncMapping> SyncMappings { get; set; } = null!;
    public DbSet<SyncRun> SyncRuns { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ... configuración existente de TodoList/TodoListItem cascade delete ...

        modelBuilder.Entity<SyncMapping>(b =>
        {
            b.HasIndex(m => new { m.EntityType, m.LocalId }).IsUnique();
            b.HasIndex(m => new { m.EntityType, m.ExternalId }).IsUnique();
            b.Property(m => m.ExternalId).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<SyncRun>(b =>
        {
            b.HasIndex(r => new { r.EntityType, r.StartedAt });
            b.Property(r => r.Error).HasColumnType("nvarchar(max)");
        });
    }
}
```

> **Importante:** preservar la configuración existente del cascade delete entre `TodoList` y `TodoListItem`. Solo se agrega lo nuevo.

- [ ] **Step 3: Restore EF tooling y generar la migration**

```bash
dotnet tool restore
dotnet ef migrations add AddSyncTables --project TodoApi/TodoApi.csproj --startup-project TodoApi/TodoApi.csproj
```

- [ ] **Step 4: Inspeccionar la migration generada**

Abrir `TodoApi/Migrations/<timestamp>_AddSyncTables.cs` y verificar que crea ambas tablas con sus columnas, los dos unique indices en `SyncMappings`, y el non-unique index en `SyncRuns`. No editar manualmente — la migration se confía como autogenerada.

- [ ] **Step 5: Verificar tests existentes pasan**

```bash
dotnet test
```
Expected: todos los tests existentes verde (los tests de `TodoListService` / `TodoListItemService` no se ven afectados por el agregado de DbSets).

- [ ] **Step 6: Commit**

```bash
git add TodoApi.Sync/Data/ TodoApi/Data/TodoContext.cs TodoApi/Migrations/
git commit -m "$(cat <<'EOF'
feat(sync): persist SyncMapping/SyncRun in TodoContext

Adds ISyncDbContext interface in TodoApi.Sync (decoupling the sync logic
from the concrete DbContext) and makes TodoContext implement it. New EF
migration creates the two tables with unique indices for correlation
lookups.
EOF
)"
```

---

## Task 4: External wire DTOs and JsonOptions

**Files:**
- Create: `TodoApi.Sync/External/Models/CreateExternalTodoItemRequest.cs`
- Create: `TodoApi.Sync/External/Models/CreateExternalTodoListRequest.cs`
- Create: `TodoApi.Sync/External/Models/ExternalTodoItem.cs`
- Create: `TodoApi.Sync/External/Models/ExternalTodoList.cs`
- Create: `TodoApi.Sync/External/ExternalJsonOptions.cs`

- [ ] **Step 1: Records de respuesta**

`TodoApi.Sync/External/Models/ExternalTodoItem.cs`:
```csharp
namespace TodoApi.Sync.External.Models;

public record ExternalTodoItem(
    string Id,
    string SourceId,
    string Description,
    bool Completed,
    DateTime CreatedAt,
    DateTime UpdatedAt);
```

`TodoApi.Sync/External/Models/ExternalTodoList.cs`:
```csharp
namespace TodoApi.Sync.External.Models;

public record ExternalTodoList(
    string Id,
    string SourceId,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<ExternalTodoItem> Items);
```

- [ ] **Step 2: Records de request**

`TodoApi.Sync/External/Models/CreateExternalTodoItemRequest.cs`:
```csharp
namespace TodoApi.Sync.External.Models;

public record CreateExternalTodoItemRequest(
    string SourceId,
    string Description,
    bool Completed);
```

`TodoApi.Sync/External/Models/CreateExternalTodoListRequest.cs`:
```csharp
namespace TodoApi.Sync.External.Models;

public record CreateExternalTodoListRequest(
    string SourceId,
    string Name,
    IReadOnlyList<CreateExternalTodoItemRequest> Items);
```

- [ ] **Step 3: `ExternalJsonOptions`**

`TodoApi.Sync/External/ExternalJsonOptions.cs`:
```csharp
using System.Text.Json;

namespace TodoApi.Sync.External;

internal static class ExternalJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
```

`SnakeCaseLower` está disponible en .NET 8+ — convierte `SourceId` → `source_id` automáticamente, sin `[JsonPropertyName]` en cada campo.

- [ ] **Step 4: Build**

```bash
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add TodoApi.Sync/External/
git commit -m "feat(sync): add wire DTOs and JsonOptions for external API"
```

---

## Task 5: External client — happy path (TDD)

**Files:**
- Create: `TodoApi.Sync/External/ExternalApiException.cs`
- Create: `TodoApi.Sync/External/IExternalTodoListClient.cs`
- Create: `TodoApi.Sync/External/ExternalTodoListClient.cs`
- Create: `TodoApi.Tests/Sync/TestHelpers/StubHttpMessageHandler.cs`
- Test: `TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs`

- [ ] **Step 1: Agregar Moq al proyecto de tests + ProjectReference a TodoApi.Sync**

```bash
dotnet add TodoApi.Tests package Moq --version 4.20.70
dotnet add TodoApi.Tests reference TodoApi.Sync/TodoApi.Sync.csproj
```

- [ ] **Step 2: `ExternalApiException`**

`TodoApi.Sync/External/ExternalApiException.cs`:
```csharp
namespace TodoApi.Sync.External;

public class ExternalApiException : Exception
{
    public int? StatusCode { get; }
    public string? Method { get; }
    public string? Path { get; }
    public string? Body { get; }

    public ExternalApiException(string message, int? statusCode, string? method, string? path, string? body, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Method = method;
        Path = path;
        Body = body;
    }
}
```

- [ ] **Step 3: `IExternalTodoListClient` interface**

`TodoApi.Sync/External/IExternalTodoListClient.cs`:
```csharp
using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public interface IExternalTodoListClient
{
    Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: `StubHttpMessageHandler` test helper**

`TodoApi.Tests/Sync/TestHelpers/StubHttpMessageHandler.cs`:
```csharp
using System.Net;

namespace TodoApi.Tests.Sync.TestHelpers;

public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public StubHttpMessageHandler(HttpStatusCode status, string? body = null)
        : this(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = body is null ? null : new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        })) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }
        return await _responder(request);
    }
}
```

- [ ] **Step 5: Failing test — serializa snake_case y deserializa la respuesta**

`TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Tests.Sync.TestHelpers;
using Xunit;

namespace TodoApi.Tests.Sync.External;

public class ExternalTodoListClientTests
{
    [Fact]
    public async Task CreateTodoListAsync_SerializesSnakeCaseAndDeserializesResponse()
    {
        var responseJson = """
        {
          "id": "ext-1",
          "source_id": "42",
          "name": "Groceries",
          "created_at": "2026-05-09T12:00:00Z",
          "updated_at": "2026-05-09T12:00:00Z",
          "items": []
        }
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.Created, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new ExternalTodoListClient(http);

        var request = new CreateExternalTodoListRequest(
            SourceId: "42",
            Name: "Groceries",
            Items: Array.Empty<CreateExternalTodoItemRequest>());

        var result = await client.CreateTodoListAsync(request, CancellationToken.None);

        Assert.Equal("ext-1", result.Id);
        Assert.Equal("42", result.SourceId);
        Assert.Equal("Groceries", result.Name);

        Assert.Single(handler.RequestBodies);
        var sentJson = JsonDocument.Parse(handler.RequestBodies[0]).RootElement;
        Assert.Equal("42", sentJson.GetProperty("source_id").GetString());
        Assert.Equal("Groceries", sentJson.GetProperty("name").GetString());
        Assert.Equal(0, sentJson.GetProperty("items").GetArrayLength());
    }
}
```

- [ ] **Step 6: Correr el test — debe fallar (la clase no existe)**

```bash
dotnet test --filter "FullyQualifiedName~ExternalTodoListClientTests"
```
Expected: FAIL — `ExternalTodoListClient` no compila.

- [ ] **Step 7: Implementar `ExternalTodoListClient`**

`TodoApi.Sync/External/ExternalTodoListClient.cs`:
```csharp
using System.Net.Http.Json;
using TodoApi.Sync.External.Models;

namespace TodoApi.Sync.External;

public class ExternalTodoListClient : IExternalTodoListClient
{
    private readonly HttpClient _http;

    public ExternalTodoListClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ExternalTodoList> CreateTodoListAsync(
        CreateExternalTodoListRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "todolists",
            request,
            ExternalJsonOptions.Default,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalApiException(
                $"POST todolists failed with {(int)response.StatusCode}",
                (int)response.StatusCode,
                "POST",
                "todolists",
                body);
        }

        var result = await response.Content.ReadFromJsonAsync<ExternalTodoList>(
            ExternalJsonOptions.Default,
            cancellationToken);

        return result ?? throw new ExternalApiException(
            "POST todolists returned empty body",
            (int)response.StatusCode,
            "POST",
            "todolists",
            null);
    }
}
```

- [ ] **Step 8: Correr el test — debe pasar**

```bash
dotnet test --filter "FullyQualifiedName~ExternalTodoListClientTests"
```
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add TodoApi.Sync/External/ExternalApiException.cs TodoApi.Sync/External/IExternalTodoListClient.cs TodoApi.Sync/External/ExternalTodoListClient.cs TodoApi.Tests/
git commit -m "feat(sync): add ExternalTodoListClient with snake_case JSON binding"
```

---

## Task 6: External client — error handling (TDD)

**Files:**
- Modify: `TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs`

- [ ] **Step 1: Failing tests — 4xx y 5xx tiran `ExternalApiException` con StatusCode**

Agregar al test class de la Task 5:
```csharp
[Fact]
public async Task CreateTodoListAsync_4xxResponse_ThrowsExternalApiExceptionWithStatusCode()
{
    var handler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
    var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
    var client = new ExternalTodoListClient(http);

    var ex = await Assert.ThrowsAsync<ExternalApiException>(() =>
        client.CreateTodoListAsync(
            new CreateExternalTodoListRequest("1", "x", Array.Empty<CreateExternalTodoItemRequest>()),
            CancellationToken.None));

    Assert.Equal(400, ex.StatusCode);
    Assert.Equal("POST", ex.Method);
    Assert.Equal("todolists", ex.Path);
    Assert.Contains("bad", ex.Body);
}

[Fact]
public async Task CreateTodoListAsync_5xxResponse_ThrowsExternalApiException()
{
    var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, null);
    var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
    var client = new ExternalTodoListClient(http);

    var ex = await Assert.ThrowsAsync<ExternalApiException>(() =>
        client.CreateTodoListAsync(
            new CreateExternalTodoListRequest("1", "x", Array.Empty<CreateExternalTodoItemRequest>()),
            CancellationToken.None));

    Assert.Equal(500, ex.StatusCode);
}
```

- [ ] **Step 2: Correr los tests — deberían pasar ya (la lógica está implementada en Task 5)**

```bash
dotnet test --filter "FullyQualifiedName~ExternalTodoListClientTests"
```
Expected: PASS — todos los tests verde, incluyendo los nuevos.

> **Nota:** la implementación de Task 5 ya cubre el path 4xx/5xx. Estos tests son red→green degenerados: green directo. Son valiosos para regresión y para documentar el contrato. Si fallan algún día, sabemos qué cambió.

- [ ] **Step 3: Commit**

```bash
git add TodoApi.Tests/Sync/External/ExternalTodoListClientTests.cs
git commit -m "test(sync): cover 4xx/5xx error paths in ExternalTodoListClient"
```

---

## Task 7: Options classes

**Files:**
- Create: `TodoApi.Sync/Configuration/ExternalApiOptions.cs`
- Create: `TodoApi.Sync/Configuration/SyncOptions.cs`

- [ ] **Step 1: `ExternalApiOptions`**

`TodoApi.Sync/Configuration/ExternalApiOptions.cs`:
```csharp
using System.ComponentModel.DataAnnotations;

namespace TodoApi.Sync.Configuration;

public class ExternalApiOptions
{
    [Required, Url]
    public string BaseAddress { get; set; } = "http://localhost:8080";

    [Range(0, 10)]
    public int RetryMaxAttempts { get; set; } = 3;

    [Range(1, 60)]
    public int PerAttemptTimeoutSeconds { get; set; } = 10;
}
```

- [ ] **Step 2: `SyncOptions`**

`TodoApi.Sync/Configuration/SyncOptions.cs`:
```csharp
namespace TodoApi.Sync.Configuration;

public class SyncOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 3: Build**

```bash
dotnet build
```

- [ ] **Step 4: Commit**

```bash
git add TodoApi.Sync/Configuration/
git commit -m "feat(sync): add ExternalApiOptions and SyncOptions"
```

---

## Task 8: TodoListSyncService — happy path with no lists (TDD)

**Files:**
- Create: `TodoApi.Sync/Services/SyncRunResult.cs`
- Create: `TodoApi.Sync/Services/ITodoListSyncService.cs`
- Create: `TodoApi.Sync/Services/TodoListSyncService.cs`
- Test: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs`

- [ ] **Step 1: `SyncRunResult` y la interface**

`TodoApi.Sync/Services/SyncRunResult.cs`:
```csharp
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public record SyncRunResult(int Total, int Pushed, int Failed, SyncRunStatus Status);
```

`TodoApi.Sync/Services/ITodoListSyncService.cs`:
```csharp
namespace TodoApi.Sync.Services;

public interface ITodoListSyncService
{
    Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Failing test — sin lists locales devuelve 0 pushed + Succeeded**

`TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TodoApi.Data;
using TodoApi.Sync.External;
using TodoApi.Sync.Models;
using TodoApi.Sync.Services;
using Xunit;

namespace TodoApi.Tests.Sync.Services;

public class TodoListSyncServiceTests
{
    private static DbContextOptions<TodoContext> NewDbOptions() =>
        new DbContextOptionsBuilder<TodoContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task PushTodoListsAsync_NoLocalLists_ReturnsZeroAndSucceeded()
    {
        await using var ctx = new TodoContext(NewDbOptions());
        var client = new Mock<IExternalTodoListClient>(MockBehavior.Strict);

        var sut = new TodoListSyncService(ctx, client.Object, NullLogger<TodoListSyncService>.Instance);

        var result = await sut.PushTodoListsAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(SyncRunStatus.Succeeded, result.Status);

        var run = Assert.Single(ctx.SyncRuns);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(SyncDirection.Push, run.Direction);
        Assert.Equal(SyncEntityType.TodoList, run.EntityType);
        Assert.NotNull(run.FinishedAt);

        client.VerifyNoOtherCalls();
    }
}
```

> **Nota:** los tests usan `TodoContext` directamente (que implementa `ISyncDbContext`) — el constructor del SUT acepta la interface, EF in-memory soporta los dos sets que agregamos en Task 3.

- [ ] **Step 3: Correr el test — debe fallar (clase no existe)**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests.PushTodoListsAsync_NoLocalLists"
```
Expected: FAIL.

- [ ] **Step 4: Implementar `TodoListSyncService` mínimo**

`TodoApi.Sync/Services/TodoListSyncService.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TodoApi.Sync.Data;
using TodoApi.Sync.External;
using TodoApi.Sync.External.Models;
using TodoApi.Sync.Models;

namespace TodoApi.Sync.Services;

public class TodoListSyncService : ITodoListSyncService
{
    private readonly ISyncDbContext _db;
    private readonly IExternalTodoListClient _client;
    private readonly ILogger<TodoListSyncService> _logger;

    public TodoListSyncService(
        ISyncDbContext db,
        IExternalTodoListClient client,
        ILogger<TodoListSyncService> logger)
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    public async Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            EntityType = SyncEntityType.TodoList,
            Direction = SyncDirection.Push,
            StartedAt = DateTime.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        // Slice 1 happy path: no candidates → Succeeded with zero counts.
        run.FinishedAt = DateTime.UtcNow;
        run.Status = SyncRunStatus.Succeeded;
        run.ItemsProcessed = 0;
        run.ItemsFailed = 0;
        await _db.SaveChangesAsync(cancellationToken);

        return new SyncRunResult(0, 0, 0, SyncRunStatus.Succeeded);
    }
}
```

- [ ] **Step 5: Correr el test — debe pasar**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests.PushTodoListsAsync_NoLocalLists"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add TodoApi.Sync/Services/ TodoApi.Tests/Sync/Services/
git commit -m "feat(sync): scaffold TodoListSyncService with empty-state path"
```

---

## Task 9: TodoListSyncService — push N lists and create mappings (TDD)

**Files:**
- Modify: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs`
- Modify: `TodoApi.Sync/Services/TodoListSyncService.cs`

- [ ] **Step 1: Failing test — 3 lists locales sin mapping → 3 llamadas + 3 SyncMappings**

Agregar al test class:
```csharp
[Fact]
public async Task PushTodoListsAsync_ThreeUnsyncedLists_PushesAllAndCreatesMappings()
{
    await using var ctx = new TodoContext(NewDbOptions());
    ctx.TodoList.AddRange(
        new TodoApi.Models.TodoList { Id = 1, Name = "List 1" },
        new TodoApi.Models.TodoList { Id = 2, Name = "List 2" },
        new TodoApi.Models.TodoList { Id = 3, Name = "List 3" });
    await ctx.SaveChangesAsync();

    var client = new Mock<IExternalTodoListClient>();
    client.Setup(c => c.CreateTodoListAsync(It.IsAny<CreateExternalTodoListRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((CreateExternalTodoListRequest req, CancellationToken _) =>
              new ExternalTodoList(
                  Id: $"ext-{req.SourceId}",
                  SourceId: req.SourceId,
                  Name: req.Name,
                  CreatedAt: DateTime.UtcNow,
                  UpdatedAt: DateTime.UtcNow,
                  Items: Array.Empty<ExternalTodoItem>()));

    var sut = new TodoListSyncService(ctx, client.Object, NullLogger<TodoListSyncService>.Instance);

    var result = await sut.PushTodoListsAsync(CancellationToken.None);

    Assert.Equal(3, result.Total);
    Assert.Equal(3, result.Pushed);
    Assert.Equal(0, result.Failed);
    Assert.Equal(SyncRunStatus.Succeeded, result.Status);

    var mappings = ctx.SyncMappings.OrderBy(m => m.LocalId).ToList();
    Assert.Equal(3, mappings.Count);
    Assert.Equal(new[] { 1L, 2L, 3L }, mappings.Select(m => m.LocalId));
    Assert.Equal(new[] { "ext-1", "ext-2", "ext-3" }, mappings.Select(m => m.ExternalId));
    Assert.All(mappings, m => Assert.Equal(SyncEntityType.TodoList, m.EntityType));

    client.Verify(c => c.CreateTodoListAsync(
        It.Is<CreateExternalTodoListRequest>(r => r.SourceId == "1" && r.Name == "List 1"),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

> Verificamos que `SourceId == LocalId.ToString()` — el camino para correlación bidireccional.

- [ ] **Step 2: Correr — debe fallar**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests.PushTodoListsAsync_ThreeUnsyncedLists"
```
Expected: FAIL — ahora mismo el service no busca candidates.

- [ ] **Step 3: Implementar la query y el push loop**

Reemplazar el método `PushTodoListsAsync` en `TodoListSyncService.cs`:
```csharp
public async Task<SyncRunResult> PushTodoListsAsync(CancellationToken cancellationToken)
{
    var run = new SyncRun
    {
        EntityType = SyncEntityType.TodoList,
        Direction = SyncDirection.Push,
        StartedAt = DateTime.UtcNow,
        Status = SyncRunStatus.Running,
    };
    _db.SyncRuns.Add(run);
    await _db.SaveChangesAsync(cancellationToken);

    var mappedLocalIds = await _db.SyncMappings
        .Where(m => m.EntityType == SyncEntityType.TodoList)
        .Select(m => m.LocalId)
        .ToListAsync(cancellationToken);

    var typedDb = (DbContext)_db;
    var candidates = await typedDb.Set<TodoApi.Models.TodoList>()
        .Where(l => !mappedLocalIds.Contains(l.Id))
        .OrderBy(l => l.Id)
        .ToListAsync(cancellationToken);

    int pushed = 0;
    int failed = 0;

    foreach (var local in candidates)
    {
        try
        {
            var external = await _client.CreateTodoListAsync(
                new CreateExternalTodoListRequest(
                    SourceId: local.Id.ToString(),
                    Name: local.Name,
                    Items: Array.Empty<CreateExternalTodoItemRequest>()),
                cancellationToken);

            _db.SyncMappings.Add(new SyncMapping
            {
                EntityType = SyncEntityType.TodoList,
                LocalId = local.Id,
                ExternalId = external.Id,
                LastSyncedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Pushed TodoList {LocalId} to external as {ExternalId}",
                local.Id, external.Id);
            pushed++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to push TodoList {LocalId}", local.Id);
            failed++;
        }
    }

    run.FinishedAt = DateTime.UtcNow;
    run.ItemsProcessed = pushed;
    run.ItemsFailed = failed;
    run.Status = failed == 0
        ? SyncRunStatus.Succeeded
        : (pushed == 0 ? SyncRunStatus.Failed : SyncRunStatus.Partial);
    await _db.SaveChangesAsync(cancellationToken);

    return new SyncRunResult(candidates.Count, pushed, failed, run.Status);
}
```

> **Decisión:** el query a `TodoList` usa `((DbContext)_db).Set<TodoList>()`. El sync project no debe importar `TodoContext` directamente (acoplaría inversamente). Pero `ISyncDbContext` no expone `TodoList` (intencional — mantiene la interface mínima al concern de sync). Casteamos a `DbContext` y usamos el genérico `Set<>`. Si `_db` no es un `DbContext` (mock), el cast falla con InvalidCastException — aceptable, los tests usan `TodoContext` real.

- [ ] **Step 4: Correr todos los tests del sync service — los 2 deben pasar**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add TodoApi.Sync/Services/TodoListSyncService.cs TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs
git commit -m "feat(sync): push unmapped TodoLists and persist correlation mappings"
```

---

## Task 10: TodoListSyncService — idempotency (TDD)

**Files:**
- Modify: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs`

- [ ] **Step 1: Failing test — 2 lists nuevas + 1 ya mapeada → solo 2 pushes**

Agregar:
```csharp
[Fact]
public async Task PushTodoListsAsync_WithExistingMapping_OnlyPushesUnmapped()
{
    await using var ctx = new TodoContext(NewDbOptions());
    ctx.TodoList.AddRange(
        new TodoApi.Models.TodoList { Id = 1, Name = "Already synced" },
        new TodoApi.Models.TodoList { Id = 2, Name = "New 2" },
        new TodoApi.Models.TodoList { Id = 3, Name = "New 3" });
    ctx.SyncMappings.Add(new SyncMapping
    {
        EntityType = SyncEntityType.TodoList,
        LocalId = 1,
        ExternalId = "ext-prev",
        LastSyncedAt = DateTime.UtcNow.AddHours(-1),
    });
    await ctx.SaveChangesAsync();

    var client = new Mock<IExternalTodoListClient>();
    client.Setup(c => c.CreateTodoListAsync(It.IsAny<CreateExternalTodoListRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((CreateExternalTodoListRequest req, CancellationToken _) =>
              new ExternalTodoList(
                  Id: $"ext-{req.SourceId}",
                  SourceId: req.SourceId,
                  Name: req.Name,
                  CreatedAt: DateTime.UtcNow,
                  UpdatedAt: DateTime.UtcNow,
                  Items: Array.Empty<ExternalTodoItem>()));

    var sut = new TodoListSyncService(ctx, client.Object, NullLogger<TodoListSyncService>.Instance);

    var result = await sut.PushTodoListsAsync(CancellationToken.None);

    Assert.Equal(2, result.Total);
    Assert.Equal(2, result.Pushed);
    Assert.Equal(SyncRunStatus.Succeeded, result.Status);

    client.Verify(c => c.CreateTodoListAsync(
        It.Is<CreateExternalTodoListRequest>(r => r.SourceId == "1"),
        It.IsAny<CancellationToken>()), Times.Never);
    client.Verify(c => c.CreateTodoListAsync(It.IsAny<CreateExternalTodoListRequest>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2));

    // Mapping previo intacto + 2 nuevos.
    Assert.Equal(3, ctx.SyncMappings.Count());
}
```

- [ ] **Step 2: Correr — pasa directo (la query de Task 9 ya filtra por mapping)**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests.PushTodoListsAsync_WithExistingMapping"
```
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs
git commit -m "test(sync): cover idempotency of TodoList push (skip already mapped)"
```

---

## Task 11: TodoListSyncService — partial failure & SyncRun persistence (TDD)

**Files:**
- Modify: `TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs`

- [ ] **Step 1: Failing test — 1 de 3 falla → status Partial, los otros 2 quedan mapeados**

Agregar:
```csharp
[Fact]
public async Task PushTodoListsAsync_OneOfThreeFails_StatusPartialAndOthersMapped()
{
    await using var ctx = new TodoContext(NewDbOptions());
    ctx.TodoList.AddRange(
        new TodoApi.Models.TodoList { Id = 1, Name = "L1" },
        new TodoApi.Models.TodoList { Id = 2, Name = "L2" },
        new TodoApi.Models.TodoList { Id = 3, Name = "L3" });
    await ctx.SaveChangesAsync();

    var client = new Mock<IExternalTodoListClient>();
    client.Setup(c => c.CreateTodoListAsync(
            It.Is<CreateExternalTodoListRequest>(r => r.SourceId == "2"),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(new ExternalApiException("boom", 503, "POST", "todolists", null));
    client.Setup(c => c.CreateTodoListAsync(
            It.Is<CreateExternalTodoListRequest>(r => r.SourceId != "2"),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync((CreateExternalTodoListRequest req, CancellationToken _) =>
            new ExternalTodoList($"ext-{req.SourceId}", req.SourceId, req.Name,
                DateTime.UtcNow, DateTime.UtcNow, Array.Empty<ExternalTodoItem>()));

    var sut = new TodoListSyncService(ctx, client.Object, NullLogger<TodoListSyncService>.Instance);

    var result = await sut.PushTodoListsAsync(CancellationToken.None);

    Assert.Equal(3, result.Total);
    Assert.Equal(2, result.Pushed);
    Assert.Equal(1, result.Failed);
    Assert.Equal(SyncRunStatus.Partial, result.Status);

    var mappings = ctx.SyncMappings.OrderBy(m => m.LocalId).ToList();
    Assert.Equal(new[] { 1L, 3L }, mappings.Select(m => m.LocalId));

    var run = Assert.Single(ctx.SyncRuns);
    Assert.Equal(SyncRunStatus.Partial, run.Status);
    Assert.Equal(2, run.ItemsProcessed);
    Assert.Equal(1, run.ItemsFailed);
    Assert.NotNull(run.FinishedAt);
}

[Fact]
public async Task PushTodoListsAsync_AllFail_StatusFailed()
{
    await using var ctx = new TodoContext(NewDbOptions());
    ctx.TodoList.Add(new TodoApi.Models.TodoList { Id = 1, Name = "L1" });
    await ctx.SaveChangesAsync();

    var client = new Mock<IExternalTodoListClient>();
    client.Setup(c => c.CreateTodoListAsync(It.IsAny<CreateExternalTodoListRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new ExternalApiException("nope", 500, "POST", "todolists", null));

    var sut = new TodoListSyncService(ctx, client.Object, NullLogger<TodoListSyncService>.Instance);

    var result = await sut.PushTodoListsAsync(CancellationToken.None);

    Assert.Equal(SyncRunStatus.Failed, result.Status);
    Assert.Empty(ctx.SyncMappings);
    Assert.Single(ctx.SyncRuns);
}
```

- [ ] **Step 2: Correr — deberían pasar (Task 9 ya implementó el status logic)**

```bash
dotnet test --filter "FullyQualifiedName~TodoListSyncServiceTests"
```
Expected: PASS — todos los tests del sync service en verde.

- [ ] **Step 3: Commit**

```bash
git add TodoApi.Tests/Sync/Services/TodoListSyncServiceTests.cs
git commit -m "test(sync): cover partial and total failure paths"
```

---

## Task 12: SyncBackgroundService + smoke test

**Files:**
- Create: `TodoApi.Sync/Hosting/SyncBackgroundService.cs`
- Test: `TodoApi.Tests/Sync/Hosting/SyncBackgroundServiceTests.cs`

- [ ] **Step 1: Implementar `SyncBackgroundService`**

`TodoApi.Sync/Hosting/SyncBackgroundService.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Services;

namespace TodoApi.Sync.Hosting;

public sealed class SyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SyncOptions> _options;
    private readonly ILogger<SyncBackgroundService> _logger;

    public SyncBackgroundService(
        IServiceScopeFactory scopes,
        IOptionsMonitor<SyncOptions> options,
        ILogger<SyncBackgroundService> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = _options.CurrentValue;
        if (!startup.Enabled)
        {
            _logger.LogInformation("Sync background service disabled via config; idling");
            return;
        }

        try
        {
            await Task.Delay(startup.StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ITodoListSyncService>();
                var result = await svc.PushTodoListsAsync(stoppingToken);
                _logger.LogInformation(
                    "Sync tick completed: total={Total} pushed={Pushed} failed={Failed} status={Status}",
                    result.Total, result.Pushed, result.Failed, result.Status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync tick threw — will retry on next interval");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
```

- [ ] **Step 2: Smoke test del background service (start+stop limpio con Enabled=false)**

`TodoApi.Tests/Sync/Hosting/SyncBackgroundServiceTests.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.Hosting;
using Xunit;

namespace TodoApi.Tests.Sync.Hosting;

public class SyncBackgroundServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_StopsImmediately()
    {
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var options = new TestOptionsMonitor<SyncOptions>(new SyncOptions { Enabled = false });

        var sut = new SyncBackgroundService(scopes, options, NullLogger<SyncBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(cts.Token);
    }

    private class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
```

- [ ] **Step 3: Run el test**

```bash
dotnet test --filter "FullyQualifiedName~SyncBackgroundServiceTests"
```
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add TodoApi.Sync/Hosting/ TodoApi.Tests/Sync/Hosting/
git commit -m "feat(sync): add SyncBackgroundService with periodic tick loop"
```

---

## Task 13: AddTodoSync DI extension

**Files:**
- Create: `TodoApi.Sync/DependencyInjection/SyncServiceCollectionExtensions.cs`

- [ ] **Step 1: Implementar la extensión**

`TodoApi.Sync/DependencyInjection/SyncServiceCollectionExtensions.cs`:
```csharp
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using TodoApi.Sync.Configuration;
using TodoApi.Sync.External;
using TodoApi.Sync.Hosting;
using TodoApi.Sync.Services;

namespace TodoApi.Sync.DependencyInjection;

public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Wires the sync engine: typed HttpClient + Polly v8 resilience pipeline,
    /// scoped sync service, hosted background service, and bound options.
    /// Caller must already have the underlying TodoContext registered as
    /// <see cref="ISyncDbContext"/> (use <see cref="AddSyncDbContext{T}"/>).
    /// </summary>
    public static IServiceCollection AddTodoSync(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ExternalApiOptions>()
            .Bind(configuration.GetSection("ExternalApi"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SyncOptions>()
            .Bind(configuration.GetSection("Sync"))
            .ValidateOnStart();

        services.AddHttpClient<IExternalTodoListClient, ExternalTodoListClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<ExternalApiOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseAddress);
        })
        .AddResilienceHandler("external-todo", (builder, ctx) =>
        {
            var opts = ctx.ServiceProvider.GetRequiredService<IOptions<ExternalApiOptions>>().Value;

            builder
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = opts.RetryMaxAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(1),
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .Handle<TimeoutRejectedException>()
                        .HandleResult(r =>
                            (int)r.StatusCode >= 500
                            || r.StatusCode == HttpStatusCode.RequestTimeout
                            || r.StatusCode == HttpStatusCode.TooManyRequests),
                })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                })
                .AddTimeout(TimeSpan.FromSeconds(opts.PerAttemptTimeoutSeconds));
        });

        services.AddScoped<ITodoListSyncService, TodoListSyncService>();
        services.AddHostedService<SyncBackgroundService>();

        return services;
    }
}
```

> **Order matters:** Retry **outer**, CircuitBreaker, Timeout **inner** (per-attempt). Si Timeout va outer comparte ventana entre intentos.

- [ ] **Step 2: Build**

```bash
dotnet build
```
Expected: build succeeds. Si `Polly` no resuelve, agregar referencia: `dotnet add TodoApi.Sync package Polly` (Microsoft.Extensions.Http.Resilience ya lo trae transitivo, este step es solo si el compilador no encuentra `DelayBackoffType` o `PredicateBuilder<>`).

- [ ] **Step 3: Commit**

```bash
git add TodoApi.Sync/DependencyInjection/
git commit -m "feat(sync): add AddTodoSync extension wiring HttpClient + Polly v8 + DI"
```

---

## Task 14: Wire en Program.cs + appsettings + appsettings.Development

**Files:**
- Modify: `TodoApi/Program.cs`
- Modify: `TodoApi/appsettings.json`
- Modify: `TodoApi/appsettings.Development.json` (gitignored — sólo a nivel local)

- [ ] **Step 1: Registrar `ISyncDbContext` y `AddTodoSync` en Program.cs**

Editar `TodoApi/Program.cs`. Justo después del `AddDbContext<TodoContext>`, agregar:
```csharp
builder.Services.AddScoped<TodoApi.Sync.Data.ISyncDbContext>(
    sp => sp.GetRequiredService<TodoApi.Data.TodoContext>());

builder.Services.AddTodoSync(builder.Configuration);
```

Asegurar el `using TodoApi.Sync.DependencyInjection;` arriba si no se infiere.

- [ ] **Step 2: Agregar secciones de config a `appsettings.json`**

`TodoApi/appsettings.json` (mergear con el contenido existente):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ExternalApi": {
    "BaseAddress": "http://localhost:8080",
    "RetryMaxAttempts": 3,
    "PerAttemptTimeoutSeconds": 10
  },
  "Sync": {
    "Interval": "00:01:00",
    "StartupDelay": "00:00:05",
    "Enabled": true
  }
}
```

- [ ] **Step 3: Documentar override en `appsettings.Development.json` (no commitear)**

El archivo está gitignored — solo dejar nota en `NOTES.md` (Task 15) sobre cómo overridear. Para correr localmente, el dev puede agregar:
```json
{
  "ExternalApi": { "BaseAddress": "http://localhost:3000" },
  "Sync": { "Enabled": false }
}
```
(Setting `Enabled=false` en dev evita que el sync corra en cada `dotnet run` mientras la API externa no esté levantada.)

- [ ] **Step 4: Build + format**

```bash
dotnet build
dotnet csharpier .
```

- [ ] **Step 5: Smoke run del API (opcional, requiere SqlServer accesible)**

```bash
dotnet run --project TodoApi
```
Expected: la app levanta sin throw. Si la migration aplica clean y `ExternalApi.BaseAddress` es válido (URL parseable), el host arranca. Si `Sync:Enabled=true` y la API externa no está corriendo, los ticks fallarán y se loguearán warnings — pero el host sigue vivo.

`Ctrl+C` para parar.

- [ ] **Step 6: Commit**

```bash
git add TodoApi/Program.cs TodoApi/appsettings.json
git commit -m "$(cat <<'EOF'
feat(sync): wire AddTodoSync from TodoApi composition root

Registers ISyncDbContext as TodoContext (one-line bridge) and adds the sync
engine via the extension method. appsettings.json gets ExternalApi and Sync
sections with safe defaults; per-developer overrides go in appsettings.Development.json.
EOF
)"
```

---

## Task 15: End-to-end verification + NOTES.md Decision Log

**Files:**
- Modify: `NOTES.md`

- [ ] **Step 1: Correr la suite completa**

```bash
dotnet test
```
Expected: TODOS los tests verde — TodoApi tests existentes + nuevos sync tests (`TodoListSyncServiceTests`, `ExternalTodoListClientTests`, `SyncBackgroundServiceTests`).

Salida esperada (resumen): "Passed! - Failed: 0, Passed: N, Skipped: 0".

- [ ] **Step 2: Format check (matchea CI)**

```bash
dotnet csharpier --check .
```
Expected: "All files are formatted correctly." Si falla, correr `dotnet csharpier .` y re-commit.

- [ ] **Step 3: Build clean**

```bash
dotnet build --no-restore
```
Expected: 0 errors, 0 warnings nuevos.

- [ ] **Step 4: Apendear entrada al Decision Log de NOTES.md**

Agregar al final de `NOTES.md` (no editar nada anterior):
```markdown
### 2026-05-09 — Slice 1: Sync engine scaffolding + PUSH de TodoLists

- **Decisión:** sync engine modelado como class library `TodoApi.Sync` referenciado desde `TodoApi`, con tres etapas explícitas — `SyncBackgroundService` (trigger), `TodoListSyncService` (lógica), `IExternalTodoListClient` typed HttpClient (cliente). Persistencia en `TodoContext` con dos tablas nuevas (`SyncMapping`, `SyncRun`) expuestas vía interface `ISyncDbContext`. Resilience con `Microsoft.Extensions.Http.Resilience` (Polly v8): retry exponencial + jitter + circuit breaker + timeout per-attempt. Configuración tipada con `IOptions<ExternalApiOptions>` y `IOptions<SyncOptions>`. Slice 1 cubre solo PUSH de TodoLists local→externo (sin items, sin pull, sin updates/deletes).
- **Alternativas descartadas:**
  - Worker SDK separado — más prod-ready pero duplica config y orquestación; el challenge se ejecuta en un proceso.
  - `ExternalId` inline en `TodoList` — acoplaría el dominio al concern de sync; la tabla `SyncMapping` mantiene el desacoplamiento y soporta multi-target en el futuro.
  - Polly directo (paquete `Polly` + `Microsoft.Extensions.Http.Polly`) — `Microsoft.Extensions.Http.Resilience` es la línea oficial de Microsoft post-Polly v8, con integración nativa a `IHttpClientFactory`.
  - Save por batch (commit al final del run) — peor blast radius en crash mid-run (dups externos sin mapping local). Save por lista da idempotencia simple.
- **Por qué:** la decisión cardinal es **desacoplar**. El user explícitamente lo pidió: el sync no debe contaminar `TodoListService` ni `TodoListItemService`. Logramos ese desacoplamiento con (a) proyecto separado, (b) interface `ISyncDbContext` minimalista, (c) tabla de mapping en lugar de inline. Polly + IHttpClientFactory típico para evitar agotamiento de sockets sin singleton.
- **Supuestos nuevos:**
  - El `source_id` del externo se usa como correlation key bidireccional — se manda nuestro local Id en cada push, y en pulls futuros podemos detectar entries originados localmente sin tabla mágica.
  - `db.Database.Migrate()` solo en Development (comportamiento heredado del repo). En Production hay que aplicar migrations out-of-band antes de levantar la app, o el sync explota al primer save.
  - El typed HttpClient asume URLs relative (`PostAsJsonAsync("todolists", ...)`). El `BaseAddress` viene de config y debe terminar con `/` o el path se resuelve mal — los defaults lo cubren.
  - InMemory provider no enforza unique indices — los tests no detectan colisiones de mapping. La migration en SqlServer real sí los enforza.
- **Deuda / follow-ups:**
  - PULL externo→local con reconciliación por `updated_at` (slice 2).
  - Sync de `TodoListItem` — la spec externa no expone POST aislado de items; merece slice propio para resolver el workaround.
  - DELETE / UPDATE bidireccional + conflict resolution (slice 3+).
  - Outbox pattern para garantía exactly-once en push (riesgo actual: crash entre `client.Create` y save de mapping → duplicado externo).
  - Telemetría / métricas de sync runs (Prometheus exporter o similar). Hoy solo logs.
  - Endpoint `POST /api/sync/run` para trigger manual (útil en desarrollo).
  - Documentar en README cómo levantar la API externa para verificación end-to-end real.
```

- [ ] **Step 5: Commit final**

```bash
git add NOTES.md
git commit -m "docs(sync): record slice 1 decision log entry in NOTES"
```

- [ ] **Step 6: Validar el state final**

```bash
git log --oneline -20
git status
```
Expected: working tree limpio, ~15 commits nuevos en la rama (uno por task), todos con el mismo prefijo (`feat(sync):` / `test(sync):` / `docs(sync):`).

---

## Self-Review Checklist (run before declaring slice closed)

- [ ] **Spec coverage:** los 3 etapas pedidas (trigger / lógica / client) están implementadas con tests. Polly v8 con exponential backoff + jitter + circuit breaker — sí. `IHttpClientFactory` typed client — sí. `IOptions<T>` para config — sí. Structured logging — sí.
- [ ] **Placeholder scan:** sin "TBD", sin "implement later", sin "add validation".
- [ ] **Type consistency:** `ISyncDbContext` exportado en `TodoApi.Sync.Data`; consumido por `TodoListSyncService` (constructor) y registrado en `Program.cs` (`AddScoped<ISyncDbContext>(sp => sp.GetRequiredService<TodoContext>())`). `SyncRunResult` mismo en interface y todos los tests. `SourceId` siempre = `local.Id.ToString()`.
- [ ] **CHALLENGE.md** no editado (regla dura del repo).
- [ ] **NOTES.md** sólo apendeado al final (regla append-mostly).
- [ ] **Csharpier clean** y **dotnet test** verde.
- [ ] **No paquete superfluo:** `Microsoft.EntityFrameworkCore` (necesario para `DbSet<>`), `Microsoft.Extensions.Hosting.Abstractions` (BackgroundService), `Microsoft.Extensions.Http` + `.Resilience` (typed clients + Polly), `Microsoft.Extensions.Options.DataAnnotations` (validación), `Moq` (test mocking) — cada uno con justificación.

---

## Verification: end-to-end manual (opcional, fuera del slice formal)

Para validar el sync corriendo contra una API real (slice futuro va a docker-compose'arla):

1. Clonar e iniciar la API externa del upstream `crunchloop/challenge-senior-engineer` (si publican un mock en docker-compose) o stub'earla con WireMock/Mockoon en `http://localhost:8080`.
2. En `appsettings.Development.json` setear `Sync:Enabled=true` y `ExternalApi:BaseAddress` al mock.
3. `dotnet run --project TodoApi`. Crear un TodoList vía `POST /api/todolists` con Swagger. Esperar ~65s (StartupDelay 5s + Interval 60s). Verificar log: `Pushed TodoList {Id} to external as {ExternalId}`.
4. Query a la BD: `SELECT * FROM SyncMappings WHERE EntityType = 1` debe tener el mapping. `SELECT * FROM SyncRuns ORDER BY StartedAt DESC` debe tener el último run con `Status = 2 (Succeeded)` e `ItemsProcessed = 1`.

Si esto pasa, el slice 1 cierra. Si no — el log estructurado dirá qué se rompió.
