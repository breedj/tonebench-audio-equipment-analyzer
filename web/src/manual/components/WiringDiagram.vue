<script setup lang="ts">
import {
  CONNECTOR_IDS,
  CONNECTOR_RADIUS,
  DODGE,
  PANEL_HEIGHT,
  PANEL_WIDTH,
  cableRoute,
  hexPoints,
  linkRoute,
  orthPath,
  pin,
  type CableOptions,
  type ConnectorId,
  type Pt,
} from './wiring'

/**
 * A wiring diagram: the QA403's front panel drawn to scale, with whatever the page hangs off it.
 *
 * Inline SVG for the same reasons ManualChart is — it follows the page's theme, scales with the
 * column and ships no image file. The panel itself keeps its real colours in both themes, because
 * it is a photograph of an object rather than a chart; everything the page draws uses currentColor
 * and the cable classes below, so the wiring reads either way round.
 *
 * The page draws its own blocks and cables in the default slot, which receives the helpers it needs
 * to place them: `pin` for a connector's centre and `cable` for a routed path between one and a
 * point. Hand-placed coordinates, like the fixture figure on the impedance page — a diagram is a
 * drawing, and a layout engine for six of them would be more code than the six.
 */

const props = withDefaults(
  defineProps<{
    /** Connectors carrying a shorting plug. Inputs only — never fit one to an output. */
    terminate?: ConnectorId[]
    caption?: string
    /** Total viewBox height. The panel takes the top 150 of it; the rest is the wiring area. */
    height?: number
    /** Screen-reader description of the whole figure. */
    alt?: string
  }>(),
  { terminate: () => [], height: 400 },
)

function toPt(p: Pt | readonly [number, number]): Pt {
  return Array.isArray(p) ? { x: p[0], y: p[1] } : (p as Pt)
}

/** A cable from a connector, down to `laneY`, across, and into `to`. */
function cable(
  id: ConnectorId,
  laneY: number,
  to: Pt | readonly [number, number],
  options: CableOptions = {},
): string {
  return orthPath(cableRoute(pin(id), laneY, toPt(to), options.exitDx ?? DODGE[id], options))
}

/** A cable straight between two connectors: down from one, along a lane, up into the other. */
function link(a: ConnectorId, b: ConnectorId, laneY: number): string {
  return orthPath(linkRoute(pin(a), pin(b), laneY, DODGE[a], DODGE[b]))
}

/** A free path through hand-placed waypoints, rounded the same way a cable is. */
function path(points: readonly (Pt | readonly [number, number])[]): string {
  return orthPath(points.map(toPt))
}

const PANEL_RED = '#a3202b'

const isTerminated = (id: ConnectorId) => props.terminate.includes(id)
</script>

<template>
  <figure class="manual-figure manual-wiring">
    <svg :viewBox="`0 0 ${PANEL_WIDTH} ${height}`" role="img" :aria-label="alt ?? caption">
      <!-- ── The front panel ───────────────────────────────────────────────────────────────── -->
      <g class="w-panel">
        <rect x="0" y="0" :width="PANEL_WIDTH" :height="PANEL_HEIGHT" rx="7" :fill="PANEL_RED" />
        <circle v-for="c in [16, 624]" :key="`s${c}`" :cx="c" cy="17" r="5" class="w-screw" />
        <circle v-for="c in [16, 624]" :key="`s2${c}`" :cx="c" cy="133" r="5" class="w-screw" />

        <rect x="46" y="8" width="112" height="21" rx="3" fill="#ffffff" />
        <text x="102" y="23" text-anchor="middle" font-size="12" class="w-brand">QuantAsylum</text>
        <text x="46" y="44" font-size="10.5" class="w-silk">QA403 Audio Analyzer</text>

        <text x="74" y="80" text-anchor="end" font-size="7" class="w-silk">Link</text>
        <text x="74" y="96" text-anchor="end" font-size="7" class="w-silk">Run</text>
        <text x="74" y="112" text-anchor="end" font-size="7" class="w-silk">Atten</text>
        <circle cx="83" cy="77" r="3.6" fill="#4ade80" />
        <circle cx="83" cy="93" r="3.6" fill="#4ade80" />
        <circle cx="83" cy="109" r="3.6" fill="#4ade80" />

        <text x="165" y="93" text-anchor="middle" font-size="6.5" class="w-silk">Expansion</text>
        <rect x="136" y="98" width="58" height="18" rx="2" fill="#1c1917" />

        <!-- Connector groups -->
        <g class="w-group">
          <rect x="230" y="9" width="156" height="124" rx="8" />
          <rect x="434" y="9" width="154" height="124" rx="8" />
        </g>
        <g class="w-silk-fill">
          <!-- The group label sits in a break in its own outline. -->
          <rect x="278" y="126" width="60" height="14" :fill="PANEL_RED" stroke="none" />
          <rect x="474" y="126" width="54" height="14" :fill="PANEL_RED" stroke="none" />
          <text x="308" y="137" text-anchor="middle" font-size="11">Output</text>
          <text x="501" y="137" text-anchor="middle" font-size="11">Input</text>
          <text x="236" y="44" font-size="11">+</text>
          <text x="380" y="44" text-anchor="end" font-size="11">+</text>
          <text x="236" y="103" font-size="11">−</text>
          <text x="380" y="103" text-anchor="end" font-size="11">−</text>
          <text x="236" y="75" font-size="10">L</text>
          <text x="380" y="75" text-anchor="end" font-size="10">R</text>
          <text x="440" y="44" font-size="11">+</text>
          <text x="582" y="44" text-anchor="end" font-size="11">+</text>
          <text x="440" y="103" font-size="11">−</text>
          <text x="582" y="103" text-anchor="end" font-size="11">−</text>
          <text x="440" y="75" font-size="10">L</text>
          <text x="582" y="75" text-anchor="end" font-size="10">R</text>
          <text x="588" y="146" text-anchor="end" font-size="6.5" class="w-warn">+32 dBV max</text>
        </g>

        <!-- The eight BNCs -->
        <g v-for="id in CONNECTOR_IDS" :key="id" class="w-bnc">
          <polygon :points="hexPoints(pin(id))" />
          <circle :cx="pin(id).x" :cy="pin(id).y" r="7.5" class="w-bnc-barrel" />
          <circle :cx="pin(id).x" :cy="pin(id).y" r="3.2" class="w-bnc-bore" />
          <circle :cx="pin(id).x" :cy="pin(id).y" r="1.3" class="w-bnc-pin" />
          <!-- A shorting plug: a cap over the connector, with its own outline so it reads as
               something fitted rather than as part of the panel. -->
          <template v-if="isTerminated(id)">
            <circle :cx="pin(id).x" :cy="pin(id).y" :r="CONNECTOR_RADIUS - 3" class="w-plug" />
            <line
              :x1="pin(id).x - 5"
              :y1="pin(id).y"
              :x2="pin(id).x + 5"
              :y2="pin(id).y"
              class="w-plug-mark"
            />
            <line
              :x1="pin(id).x"
              :y1="pin(id).y"
              :x2="pin(id).x"
              :y2="pin(id).y + 4"
              class="w-plug-mark"
            />
            <line
              :x1="pin(id).x - 3"
              :y1="pin(id).y + 4"
              :x2="pin(id).x + 3"
              :y2="pin(id).y + 4"
              class="w-plug-mark"
            />
          </template>
        </g>
      </g>

      <!-- ── Whatever this page wires to it ───────────────────────────────────────────────── -->
      <slot :pin="pin" :cable="cable" :link="link" :path="path" />
    </svg>
    <figcaption v-if="caption">{{ caption }}</figcaption>
  </figure>
</template>

<style>
/* Unscoped, like ManualChart's: the slot content is written by the page, so a scoped rule would
   not reach the cables and blocks the pages draw. */
.manual-prose .manual-wiring svg {
  max-width: 38rem;
  --w-drive: #2563eb;
  --w-measure: #ea580c;
  --w-reference: #7c3aed;
  --w-muted: #737373;
}
.dark .manual-prose .manual-wiring svg {
  --w-drive: #60a5fa;
  --w-measure: #fb923c;
  --w-reference: #a78bfa;
  --w-muted: #a3a3a3;
}

/* A colour class sets `stroke` so a cable is drawn in it; a label wearing the same class must not
   pick that stroke up, and the manual's own `.manual-figure svg text` rule is not specific enough
   to stop it. */
.manual-prose .manual-wiring svg text {
  stroke: none;
}

/* The panel keeps its own colours in both themes — it is an object, not a chart. */
.manual-prose .manual-wiring .w-panel text {
  stroke: none;
}
.manual-prose .manual-wiring .w-silk,
.manual-prose .manual-wiring .w-silk-fill text {
  fill: #ffffff;
}
.manual-prose .manual-wiring .w-brand {
  fill: #a3202b;
}
.manual-prose .manual-wiring .w-warn {
  fill: #fcd34d;
}
.manual-prose .manual-wiring .w-screw {
  fill: #6b6b6b;
  stroke: #4a4a4a;
  stroke-width: 1;
}
.manual-prose .manual-wiring .w-group rect {
  fill: none;
  stroke: rgba(255, 255, 255, 0.85);
  stroke-width: 1.4;
}
.manual-prose .manual-wiring .w-bnc polygon {
  fill: #d4d4d4;
  stroke: #8a8a8a;
  stroke-width: 1;
}
.manual-prose .manual-wiring .w-bnc-barrel {
  fill: #b4b4b4;
  stroke: #6b6b6b;
  stroke-width: 1;
}
.manual-prose .manual-wiring .w-bnc-bore {
  fill: #4a4a4a;
  stroke: none;
}
.manual-prose .manual-wiring .w-bnc-pin {
  fill: #e8c33a;
  stroke: none;
}
.manual-prose .manual-wiring .w-plug {
  fill: #3f3f46;
  stroke: #18181b;
  stroke-width: 1.2;
}
.manual-prose .manual-wiring .w-plug-mark {
  stroke: #e5e5e5;
  stroke-width: 1.2;
}

/* What the pages draw. A cable class sets the stroke and currentColor, so a label beside a cable
   picks its colour up with fill: currentColor — the ManualChart convention. */
.manual-prose .manual-wiring .w-cable {
  fill: none;
  stroke-width: 2.4;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.manual-prose .manual-wiring .w-drive {
  stroke: var(--w-drive);
  color: var(--w-drive);
}
.manual-prose .manual-wiring .w-measure {
  stroke: var(--w-measure);
  color: var(--w-measure);
}
.manual-prose .manual-wiring .w-reference {
  stroke: var(--w-reference);
  color: var(--w-reference);
  stroke-dasharray: 7 4;
}
.manual-prose .manual-wiring .w-block {
  fill: none;
  stroke: currentColor;
  stroke-width: 1.4;
}
.manual-prose .manual-wiring .w-block-fill {
  fill: rgba(115, 115, 115, 0.08);
  stroke: currentColor;
  stroke-width: 1.4;
}
.manual-prose .manual-wiring text.w-label {
  fill: currentColor;
  stroke: none;
  font-size: 11px;
}
.manual-prose .manual-wiring text.w-note {
  fill: var(--w-muted);
  stroke: none;
  font-size: 9.5px;
}
.manual-prose .manual-wiring .w-node {
  fill: currentColor;
  stroke: none;
}
.manual-prose .manual-wiring .w-thin {
  fill: none;
  stroke: currentColor;
  stroke-width: 1.2;
}
.manual-prose .manual-wiring .w-dashed {
  stroke-dasharray: 4 3;
}
</style>
