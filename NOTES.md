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
