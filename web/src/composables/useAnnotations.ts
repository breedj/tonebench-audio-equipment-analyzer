/**
 * The one list of annotations the screen is holding, shared by every measurement kind.
 *
 * <p>A module-level singleton, the pattern useChartDisplay and useMeasurements already use, and for
 * a reason that is specific to this feature: exactly one measurement is on screen at a time, and a
 * measurement keeps one flat list across all of its charts. So the FFT screen's rail, a sweep
 * screen's rail and the impulse screen's five charts can all read and write the same `ref` with no
 * props threaded through anything, which is what lets {@link AnnotationsPanel} be dropped into seven
 * display panels as one tag.</p>
 *
 * <p>It is <b>not</b> what a headless report render reads. `renderChartPng` shares module singletons
 * with the live app, so a stored result rendered through this one would draw whatever is on screen;
 * every chart therefore takes its list as a prop, and only the live screens pass this. That is the
 * same reason the FFT's report render injects its display source rather than importing it.</p>
 */

import { ref } from 'vue'
import type { AnnotationStyle, ChartAnnotation } from '../measurements/chartAnnotations'

/** The notes drawn on the open measurement's charts. Replaced, never mutated — see addAnnotation. */
const annotations = ref<ChartAnnotation[]>([])

/**
 * Which style the next click places, or null when no chart is armed — which is how a plain click
 * keeps doing nothing and every chart's drag-zoom keeps working. Transient: never saved, and a
 * report render has none of it.
 */
const armed = ref<AnnotationStyle | null>(null)

/**
 * Whether the ordinal badges are drawn on the labels. On everywhere except a report whose
 * Annotations table was not included, where a number would tie a label to a row nobody printed.
 */
const numbers = ref(true)

export function useAnnotations() {
  /** Arms a chart for the next click, or disarms it. */
  function arm(style: AnnotationStyle | null) {
    armed.value = style
  }

  /**
   * Replaces the list from a measurement's stored params, without marking it dirty.
   *
   * <p>Called with an empty list for a measurement that has none, which is the part that matters:
   * every per-kind restore watcher only fires when its type has stored params, so a measurement that
   * has never been saved would otherwise keep the <i>previous</i> measurement's notes on screen.
   * That is deliberate for display preferences and wrong for notes, which belong to one
   * measurement.</p>
   *
   * <p>It carries no `restoring` flag of its own: every caller is inside its kind's own
   * `applyStoredXDisplay`, which has already set that kind's flag, so the dirty watcher is suppressed
   * by the time this writes. A second flag here would be one more thing to keep in step for no
   * benefit.</p>
   */
  function applyLoaded(loaded: readonly ChartAnnotation[]) {
    annotations.value = [...loaded]
    armed.value = null
  }

  return { annotations, armed, numbers, arm, applyLoaded }
}
