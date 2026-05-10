# diagrams/ — Style system

Visual system for self-contained HTML explainers of technical concepts in this project.

**Aesthetic: "Terminal Schematic"** — dark CRT engineering console, mono-first, restrained, industrial. Mood: Linear changelog dark dashboards + Teenage Engineering + vintage PCB schematics. NOT "phosphor-green hacker terminal".

Living reference: [`outbox-syncmapping-flow.html`](./outbox-syncmapping-flow.html). When tokens are changed (palette, typography, etc.), update this doc first, then regenerate the existing explainers.

## When to apply

When a diagram, visual flow, or HTML explainer is requested to make sense of an internal concept (sync engine, entity lifecycle, layer architecture, slice timeline). It is NOT for an official README or production docs — it is the project's personal look, loose files in `diagrams/`.

## Design tokens

### Palette (dark only — the system is dark-only by intent)

```css
:root {
  --bg: #0E1014;
  --surface: #161A21;
  --surface-elevated: #1C2129;
  --text: #D4D4D0;
  --text-strong: #F0F0EC;
  --muted: #5A6270;
  --muted-strong: #7A8290;
  --accent-amber: #E8B84F;       /* "what to do" / pending / actions */
  --accent-amber-dim: #B8923A;
  --accent-cyan: #5DAFCC;        /* "persistent state" / completed */
  --accent-cyan-dim: #4889A0;
  --rule: #252A33;
  --rule-strong: #353B47;
  --amber-bg: rgba(232, 184, 79, 0.10);
  --cyan-bg: rgba(93, 175, 204, 0.10);
}
```

**Semantic assignment** (keep consistent across diagrams so the reader learns the color code):

| Color | Semantics | Examples |
|---|---|---|
| Amber `--accent-amber` | "what to do", pending, transient events | OutboxEvent, in-flight request, queue items |
| Cyan `--accent-cyan` | Persistent state, completed, correlation | SyncMapping, data store, external IDs |
| Muted strong `--muted-strong` | Metadata, telemetry, audit | SyncRun, timestamps, line numbers |

### Typography

Google Fonts (all open-source, variable weight):

```html
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;700&family=IBM+Plex+Sans:wght@400;500;700&display=swap" rel="stylesheet">
```

| Role | Font | Typical weights |
|---|---|---|
| H1, H2, diagram boxes, IDs, code | IBM Plex Mono | 700 (titles), 500 (mid), 400 (body) |
| Prose / body | IBM Plex Sans | 400 (body), 500/600 (strong), italic |
| Code snippets | IBM Plex Mono | 400 |

Body baseline:
```css
body {
  font-family: 'IBM Plex Sans', system-ui, sans-serif;
  font-size: 16px;
  line-height: 1.65;
  font-feature-settings: "tnum" on, "ss01" on;  /* tabular nums + ss01 alt */
  -webkit-font-smoothing: antialiased;
}
```

### Layout

- Container `max-width: 880px`, centered
- Body padding: `4rem 2rem 5rem` desktop, `2.5rem 1rem 3rem` mobile
- Section `margin-bottom: 4rem`
- Universal border-radius: **4px** (boxes, cards, code snippets)
- Background: dark with two subtle `radial-gradient`s (cyan top-left ~4.5% opacity, amber bottom-right ~3.5%) + nearly imperceptible CRT scanlines via `repeating-linear-gradient(0deg, transparent 0 2px, rgba(255,255,255,0.008) 2px 3px)` on a fixed `::before`

## Building blocks (recurring patterns)

| Block | Visual marker | Reference class |
|---|---|---|
| **Hero** | meta-tag with pulsing amber dot + H1 mono clamp(2rem, 5.5vw, 3.6rem) + muted subtitle | `.hero` `.meta-tag` `h1` `.subtitle` |
| **TL;DR operational** | card border-left 2px amber, label uppercase mono amber 0.7rem letter-spacing 0.15em, list with `→` prefix | `.tldr-op` |
| **Section heading** | `>` cyan prefix + `§NN` muted: `> §01 Title` | `.section h2 .num` |
| **Entity card** | border-top 2px in the color, hover `translateY(-2px)` + box-shadow in the color | `.entity-card.{amber\|cyan\|muted}` |
| **Diagram box** | border 1px in the color + glow `box-shadow`, title mono 700 + fields grid mono 0.78rem | `.diagram-box.{amber\|cyan\|processed}` |
| **Vertical connector** | line 1.5px `--rule-strong` + arrowhead via `::after` triangle | `.connector.{short\|medium}` |
| **Tick boundary** | repeating-linear-gradient amber dim + centered label with `background: var(--surface)` that "cuts" the line | `.tick-boundary` |
| **Y-split** | small inline SVG (60px tall) with 2 converging Bezier paths + polygon arrowheads, drawSplit anim | `.split-svg` |
| **Code snippet** | bg `#0A0C10` (darker than surface), lang badge top-right mono muted, syntax: `.comment` muted italic, `.keyword` `#C586C0`, `.type` cyan, `.string` amber, `.key-highlight` amber bg | `.code-snippet` |
| **Numbered reasons** | CSS `counter()` with `decimal-leading-zero`, large mono cyan number on the left | `.reasons .reason` |
| **Final TL;DR** | text-align center, words colored according to entity, blinking amber `_` caret at the end | `.tldr-final` `.caret` |
| **Footer** | border-top dashed `--rule`, mono 0.72rem muted, refs in `<code>` and `<a>` | `footer` |

## Narrative patterns

These are the "devices" that make the explainer tell a story, not just display data:

1. **Value-propagation via color repetition** — when a value (e.g., a GUID, an ID) travels between two entities, render it identically (same color, same background, same font) in EVERY place it appears. No arrows needed — visual recognition does the work. If the flow is not obvious, add a small mono muted note `↑ copied from X`.

2. **Timeline with tick boundary** — for flows that cross temporal moments (t0 sync vs t1 background tick), separate the zones with a labeled `tick-boundary`. Do NOT use `<hr>`.

3. **Y-split at the end** — when an operation produces 2+ parallel outputs, use the small SVG split + 2-col grid that stacks on mobile. Do NOT use elaborate divergent arrows.

4. **Double TL;DR** — operational at the top (what each thing does, scannable in 30s), conceptual at the close (mnemonic that survives closing the tab). NEVER repeat the same TL;DR top and bottom.

5. **Comparison via side-by-side cards**, not tables — `display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr))`. Collapses naturally on mobile without explicit media queries.

## Animation conventions

- **On-load reveal**: `fadeInUp` 0.6-0.8s ease-out, staggered with `animation-delay` every 0.12-0.15s in order hero → tldr → sections.
- **Decorative**:
  - Meta-tag dot: `pulse` 2.4s ease-in-out infinite
  - Final caret: `blink` 1.1s steps(2) infinite
  - SVG split: `drawSplit` with `stroke-dashoffset` 200→0, 1.2s ease-out 0.8s delay
- **MANDATORY** — always include:
  ```css
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after {
      animation-duration: 0.001ms !important;
      animation-iteration-count: 1 !important;
      transition-duration: 0.001ms !important;
    }
    .caret { opacity: 1; }
    .meta-tag::before { opacity: 1; }
    .split-svg path { stroke-dashoffset: 0; }
  }
  ```

## Information architecture conventions

1. **Double TL;DR** (see narrative pattern above)
2. **Diagram in the middle** of the document, not at the top — the reader needs the names loaded BEFORE so the diagram reveals instead of confusing
3. **Headings with `> §NN Title`** consistent across ALL sections, consecutive numbering (§01, §02, §03, §04)
4. **Embedded code only when load-bearing** — at most 1 snippet per document, and only if the code itself is the answer (not walkthrough)
5. **Footer** always with useful refs: commit hash, repo files, link to upstream/issue

## Don'ts

- **NO** Inter, Roboto, Space Grotesk, Arial, Helvetica (generic / "AI default" fonts)
- **NO** purple gradients on white (AI cliché)
- **NO** light mode (this system is dark-only by intent — if light is needed, that is ANOTHER system)
- **NO** emojis in headings, body, or diagrams (they break the mono register and introduce visual noise)
- **NO** distracting animations (particles, animated gradient mesh, hover spins)
- **NO** more than 1 embedded code snippet (breaks the rhythm)
- **NO** HTML tables for entity comparisons — use cards
- **NO** frameworks (no React, no Tailwind, no Bootstrap) — vanilla HTML/CSS, at most 1 small inline SVG where needed
- **NO** runtime dependencies (no npm install) — only CDN for Google Fonts

## How to start a new diagram

1. **Copy** `outbox-syncmapping-flow.html` as a template
2. **Keep intact**: `<head>` (Google Fonts), `:root` (tokens), CSS reset, keyframes, `prefers-reduced-motion` block, `body::before` scanlines
3. **Customize**:
   - Hero (meta-tag + H1 + subtitle)
   - Operational TL;DR (3-4 bullets, one per entity/concept)
   - Sections (`> §01..§NN`) with prose, cards, code snippets, diagrams as appropriate
   - Central diagram (boxes + connectors + tick boundary if there is a timeline)
   - Final TL;DR with blinking caret
   - Footer (commit hash + relevant files)
4. **File naming**: `diagrams/<topic>-<kind>.html`. Examples: `pull-tick-timeline.html`, `outbox-retention-flow.html`, `slice7-concurrency-overview.html`
5. **Verify before closing**:
   - Mobile viewport (~375px): boxes wrap, split stacks, code does not overflow
   - `prefers-reduced-motion: reduce` from DevTools: animations disabled
   - Console with no errors
   - 3-5 colored occurrences of the main concept are visually coherent

## Future variants

If a specific variant is needed that breaks some convention (e.g., horizontal timeline instead of vertical, ER diagram with tables, sequence diagram with actors), add it as a subsection here with a reference file. For now, the documented pattern is **vertical flow with tick boundary and optional Y-split**.
