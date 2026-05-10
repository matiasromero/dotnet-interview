# Real-Time Frontend Integration — `TodoSyncHub`

> **Audience:** the frontend team that owns
> [`/Users/matiasromero/Repositories/react-interview`](https://github.com/matiasromero/react-interview) (or its
> equivalent path inside your environment). The backend already exposes a SignalR hub
> that broadcasts every relevant change. This document is everything you need to wire
> the React app to it — no more backend changes are required for v1.

## 1. Overview

The TodoApi backend now publishes a lightweight notification on a **SignalR hub** every
time a `TodoList` or `TodoListItem` is created, updated, or deleted. The notification
is intentionally **small** — `{ eventId, entityType, entityId, operation, occurredAt }`
— and the recommended consumption pattern is **"refetch the affected REST endpoint"**.

Why no full payload?

- Zero contract duplication. The REST endpoints are already the source of truth for
  shape, validation, and serialization. The hub never disagrees with them.
- Forward-compatible. If `TodoList` adds a field tomorrow, no hub-side change is
  needed and no risk of stale shapes leaking through the websocket channel.
- Cheap to dedupe. The `eventId` is a monotonic `long` — a `Set<number>` covers the
  reconnection-replay window without any custom protocol.

The trade-off is **one extra REST round-trip per event**. For a single-tenant
interactive app with a handful of concurrent users, that's invisible. If telemetry
ever shows it's not, the backend can switch to push-with-payload behind the same hub
contract — coordinate at that point.

## 2. Hub endpoint

| | |
|---|---|
| URL (relative to API root) | `/hubs/todosync` |
| Transport | WebSocket with automatic fallback (Server-Sent Events / Long Polling) |
| Auth | None (consistent with the rest of the API in v1) |
| Broadcast trigger | Every row written to `OutboxEvents` (i.e., every CRUD on `TodoList` / `TodoListItem`) |

CORS is configured for `http://localhost:5173` by default (the Vite dev port) with
`AllowCredentials = true`. SignalR negotiation requires explicit origins (browser
spec — `*` is rejected when credentials are sent). To target another origin, edit
`TodoApi/appsettings.json`:

```json
{
  "Cors": {
    "AllowedOrigins": [ "http://localhost:5173", "https://your-staging-host" ]
  }
}
```

## 3. Notification payload

The server invokes one of two methods on every connected client:

- `TodoListChanged(notification)` — when the affected entity is a `TodoList`
- `TodoListItemChanged(notification)` — when it's a `TodoListItem`

Both methods receive the same `ChangeNotification`:

```ts
interface ChangeNotification {
  /** Monotonic outbox event id. Use for client-side dedupe. */
  eventId: number;
  /** 1 = TodoList, 2 = TodoListItem (matches the backend SyncEntityType enum) */
  entityType: 1 | 2;
  /** Local primary key of the affected row */
  entityId: number;
  /** 1 = Create, 2 = Update, 3 = Delete (matches OutboxOperation enum) */
  operation: 1 | 2 | 3;
  /** UTC timestamp when the change was recorded locally */
  occurredAt: string;
}
```

> The numeric enums match what the REST API already uses (e.g., `SyncRunStatus`,
> `OutboxOperation`); your existing `client.ts` likely already deals with them. If
> you'd rather have string discriminants, JSON.NET on the backend can be configured
> to serialize as strings — propose it and we'll do it. For v1 we kept the same
> shape as the REST DTOs to minimise surface area.

## 4. Recommended client setup (Vite + React 19 + TS strict)

### 4.1 Install the SignalR client

```bash
npm i @microsoft/signalr
```

Single dependency, ~50 KB gzipped. No peer requirements.

### 4.2 Vite proxy — add WebSocket support

In `vite.config.ts`, add a `'/hubs'` entry alongside the existing `'/api'` proxy and
enable the `ws` flag so the upgrade request flows through:

```ts
server: {
  proxy: {
    '/api': {
      target: env.VITE_API_PROXY_TARGET ?? 'https://host.docker.internal:7027',
      changeOrigin: true,
      secure: false,
    },
    '/hubs': {
      target: env.VITE_API_PROXY_TARGET ?? 'https://host.docker.internal:7027',
      ws: true,        // <-- this
      changeOrigin: true,
      secure: false,   // self-signed dev certs
    },
  },
},
```

The client connects to `/hubs/todosync` (relative URL) and inherits the proxy + cert
exception — no separate origin config in the React app.

### 4.3 Hub connection factory

`src/api/hub.ts`:

```ts
import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel,
} from '@microsoft/signalr';

export function createHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/todosync')
    // Reconnect aggressively for the first 10s, then back off.
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build();
}
```

### 4.4 React hook — `useTodoSyncHub`

`src/hooks/useTodoSyncHub.ts`:

```ts
import { useEffect, useRef } from 'react';
import { HubConnection, HubConnectionState } from '@microsoft/signalr';
import { createHubConnection } from '../api/hub';
import type { ChangeNotification } from '../api/types';

interface Callbacks {
  onListChanged: (entityId: number, op: number) => void;
  onItemChanged: (entityId: number, op: number) => void;
  /** Called after a reconnect — clients should re-bootstrap (full refetch). */
  onResync: () => void;
}

const DEDUPE_WINDOW_MS = 60_000;
const DEDUPE_MAX = 500;

export function useTodoSyncHub(callbacks: Callbacks): void {
  const cbRef = useRef(callbacks);
  cbRef.current = callbacks;

  useEffect(() => {
    const connection = createHubConnection();
    const seen = new Map<number, number>(); // eventId -> timestamp

    const dedupe = (n: ChangeNotification): boolean => {
      const now = Date.now();
      // expire old entries
      for (const [id, ts] of seen) {
        if (now - ts > DEDUPE_WINDOW_MS) seen.delete(id);
      }
      if (seen.has(n.eventId)) return true;
      seen.set(n.eventId, now);
      // bound the map size
      if (seen.size > DEDUPE_MAX) {
        const oldest = seen.keys().next().value;
        if (oldest !== undefined) seen.delete(oldest);
      }
      return false;
    };

    connection.on('TodoListChanged', (n: ChangeNotification) => {
      if (dedupe(n)) return;
      cbRef.current.onListChanged(n.entityId, n.operation);
    });
    connection.on('TodoListItemChanged', (n: ChangeNotification) => {
      if (dedupe(n)) return;
      cbRef.current.onItemChanged(n.entityId, n.operation);
    });

    connection.onreconnected(() => cbRef.current.onResync());

    connection.start().catch((err) => console.error('Hub start failed', err));

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        connection.stop().catch(() => {});
      }
    };
  }, []);
}
```

### 4.5 Wiring into `App.tsx`

```tsx
import { useTodoSyncHub } from './hooks/useTodoSyncHub';
// inside the component, alongside your existing state setters:

useTodoSyncHub({
  onListChanged: async (_entityId, _op) => {
    // Simplest correct strategy: refetch all lists and replace state.
    // Items with negative tempIds (optimistic) are preserved by your existing
    // merge logic.
    const lists = await api.getLists();
    setLists(lists);
  },
  onItemChanged: async (entityId, op) => {
    // For Delete we can shortcut without a roundtrip:
    if (op === 3) {
      setItemsByList((prev) => {
        const next = { ...prev };
        for (const listId of Object.keys(next)) {
          next[+listId] = next[+listId].filter((i) => i.id !== entityId);
        }
        return next;
      });
      return;
    }
    // Create / Update: refetch the parent list's items.
    // The backend doesn't include parent listId in the notification (intentional
    // — keeps the contract stable). We refetch all lists and merge per-list.
    const lists = await api.getLists();
    const itemsByList: Record<number, TodoListItem[]> = {};
    for (const l of lists) {
      itemsByList[l.id] = await api.getItems(l.id);
    }
    setItemsByList(itemsByList);
  },
  onResync: async () => {
    // Reconnected after a drop — bootstrap from scratch.
    const lists = await api.getLists();
    setLists(lists);
    // ... (mirror the original mount-time bootstrap)
  },
});
```

> **Optimistic vs broadcast:** the user who triggered the mutation will receive the
> broadcast for their own action (the hub is anonymous and broadcasts to all). Your
> existing optimistic update + REST commit already covers this case — the broadcast
> arrives, the dedupe-by-eventId may or may not match (depends on whether the REST
> response wrote a `setState` first), and the worst case is one redundant refetch.
> The merge is idempotent so the UI doesn't flicker.

### 4.6 Add the type to `src/api/types.ts`

```ts
export interface ChangeNotification {
  eventId: number;
  entityType: 1 | 2;
  entityId: number;
  operation: 1 | 2 | 3;
  occurredAt: string;
}
```

## 5. Failure modes — what to expect

| Scenario | What happens | What to do |
|---|---|---|
| Backend restarts | Client auto-reconnects via the schedule passed to `withAutomaticReconnect`; on success `onreconnected` fires | Implement `onResync` as a full bootstrap. Events generated during the downtime are NOT replayed by the hub (the broadcaster's cursor resets to `MAX(Id)` on restart) — the bootstrap covers it. |
| Long disconnect (> retry budget) | Connection enters `Disconnected` state | Surface a banner if you want; `connection.start()` again to reconnect manually. The simplest fallback is "reload the page". |
| Local optimistic delete + late broadcast Update | The client refetches `/items/{id}`, gets 404 | Treat 404 as a no-op in `onItemChanged`. The eventual-consistency window is one tick (≤ 2 s by default). |
| Two backend instances (production multi-host) | Each instance broadcasts to its own clients with its own cursor → potential duplicates | **Out of scope for v1.** The single-host assumption is documented in `NOTES.md`. If multi-host becomes a requirement, the backend will add a Redis backplane and the contract from the client's perspective stays the same. |
| Misconfigured CORS | Browser console shows CORS error during negotiation | Add the frontend origin to `TodoApi/appsettings.json` → `Cors:AllowedOrigins`. SignalR with credentials cannot use `*`. |

## 6. Smoke test without React

To verify the backend before wiring the React side, you can connect with `wscat` or
the SignalR JS client in a one-file HTML page:

```bash
# from the React app's dev server (port 5173) the path is /hubs/todosync;
# directly against Kestrel it's the same.
curl -X POST http://localhost:5054/api/todolists \
  -H "Content-Type: application/json" \
  -d '{"name":"smoke"}'
# Watch the WS frame on the open hub connection — TodoListChanged with operation: 1.
```

`POST /api/sync/run` also writes outbox events for any pending changes, useful as a
nudge. `GET /api/sync/status` shows `pendingOutboxCount` if you suspect events are
queued but not broadcasting.

## 7. Versioning

`ChangeNotification` is the public contract on the wire. Adding fields is
forward-compatible (extra properties are ignored by deserializers). Removing or
renaming requires coordination — open a PR against this doc and ping backend before
merging. Keep both sides in lockstep.

## 8. Out of scope (v1)

- Auth on the hub (challenge-internal, single-tenant).
- Per-user / per-list groups (everyone sees everything; matches the REST surface).
- Replay of events generated during disconnect (covered by bootstrap on reconnect).
- Redis backplane for multi-host (single-host assumption).
- Hub metrics / rate limiting (use the existing `GET /api/sync/status` for visibility).

If any of these become real requirements, the backend has a clean extension path —
talk to the backend team before designing the client around them.
