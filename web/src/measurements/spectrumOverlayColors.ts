import type { OverlayEntry } from '../composables/useMeasurements'
import { FFT_SPECTRUM_KIND } from './kinds'

/**
 * Fixed run colors for the FFT Spectrum chart (SpectrumChart.vue) and its overlay list
 * (OverlayList.vue) — color identifies *which measurement* a trace belongs to (live, or one of up
 * to MAX_VISIBLE_OVERLAYS history items); channel is told apart by dash instead. Chosen to match
 * ThdSweepChart's D2/D3/D4 hues, so the same run reads the same way on either chart.
 *
 * Note the sweep chart splits these dimensions the other way round (hue = quantity, dash = run,
 * lightness = channel — see sweepOverlayStyles.ts): it draws up to eight quantities at once and has
 * no hue left to spend on identifying a run, whereas this chart draws one trace per channel.
 */
export const FFT_LIVE_COLOR = '#1D6FD0'
export const FFT_OVERLAY_COLORS = ['#E07B16', '#1A7A3C', '#C62828']
/** Swatch color for a loaded-but-hidden overlay — it has no position among the visible set below, so no run color to show yet. */
export const FFT_OVERLAY_HIDDEN_COLOR = '#9ca3af'

/** This kind's overlays, in useMeasurements' own (add) order — the sweep screen's history items are filtered out here, not just by shape (missing channelsData) but explicitly by kind. */
export function fftOverlays(overlays: OverlayEntry[]): OverlayEntry[] {
  return overlays.filter((o) => o.detail.kind === FFT_SPECTRUM_KIND)
}

/**
 * The overlay whose stored frequency axis the spectrum chart draws on when nothing is streaming —
 * the first visible FFT overlay that actually carries a channel, or null when there is none.
 *
 * <p>SpectrumChart.computeFrame used `overlays[0]` for this, and that list is app-wide: it is never
 * cleared when the measurement changes (unchecking an item only hides it), so its first entry is
 * routinely a sweep, a response curve or an impedance run belonging to a different screen. Those
 * carry no spectrum channels, so the chart concluded it had nothing to draw and destroyed itself
 * while a perfectly good FFT overlay sat checked underneath. Reported 2026-09-27 — check a history
 * item on the stepped-response screen, then check one on an FFT measurement, and nothing appears.
 * Latent until a measurement switch stopped leaving a live frame behind (measurements/liveOwner.ts),
 * which is what had been supplying the axis.</p>
 *
 * <p>Here rather than in the chart so it can be tested: nothing in a `.vue` file is, under the node
 * environment vitest runs in.</p>
 */
export function spectrumAxisOverlay(overlays: OverlayEntry[]): OverlayEntry | null {
  return (
    fftOverlays(overlays).find(
      (o) => o.visible && o.detail.channelsData.some((c) => c.channel === 0 || c.channel === 1),
    ) ?? null
  )
}

/**
 * The color one FFT overlay draws in, by its position among the other currently-*visible* FFT
 * overlays — recomputed on every call rather than assigned once when the overlay was added, the
 * same convention ThdSweepChart's OVERLAY_DASH indexing uses. That is what guarantees the up-to-3
 * overlays on screen at once are always 3 *distinct* colors: MAX_VISIBLE_OVERLAYS caps the visible
 * set at exactly FFT_OVERLAY_COLORS.length, so the position never has to wrap around and collide
 * with another currently-visible overlay's color the way a persistent add-order index eventually
 * would once enough have been checked and unchecked.
 *
 * Returns FFT_OVERLAY_HIDDEN_COLOR for an overlay that isn't currently visible (or isn't an FFT
 * overlay at all) — SpectrumChart never plots that trace regardless, but OverlayList's swatch
 * still needs something to draw for a hidden row.
 */
export function fftOverlayColor(overlays: OverlayEntry[], id: number): string {
  const idx = fftOverlays(overlays)
    .filter((o) => o.visible)
    .findIndex((o) => o.id === id)
  return idx >= 0 ? (FFT_OVERLAY_COLORS[idx % FFT_OVERLAY_COLORS.length] ?? FFT_OVERLAY_HIDDEN_COLOR) : FFT_OVERLAY_HIDDEN_COLOR
}
