import { describe, expect, it, beforeEach } from 'vitest'
import { nextTick } from 'vue'
import { useChartDisplay } from './useChartDisplay'

/**
 * The FFT data / Min/max linkage. The band is the *minimum* of each decimated column and the line
 * above it is the maximum — one capture — so hiding the trace has to take the band with it, or
 * "hide the FFT" leaves most of the FFT on screen. Everything here is the module singleton, hence
 * the reset in beforeEach.
 */
describe('useChartDisplay: FFT data and the min/max band', () => {
  const display = useChartDisplay()

  beforeEach(async () => {
    // Through applyLoaded rather than by assignment, so each test starts from the same place the
    // app does when a measurement is selected — and so the previous test's remembered band value
    // is discarded the way loading a type discards it.
    display.applyLoaded(null)
    await nextTick()
  })

  it('defaults to the trace shown and the band with it', () => {
    expect(display.showFftTrace.value).toBe(true)
    expect(display.showMinMax.value).toBe(true)
  })

  it('clears the band when the trace is hidden, and gives it back when it returns', async () => {
    display.showFftTrace.value = false
    await nextTick()
    expect(display.showMinMax.value).toBe(false)

    display.showFftTrace.value = true
    await nextTick()
    expect(display.showMinMax.value).toBe(true)
  })

  it('gives back the band as it was, not as it is checked', async () => {
    display.showMinMax.value = false
    display.showFftTrace.value = false
    await nextTick()
    expect(display.showMinMax.value).toBe(false)

    display.showFftTrace.value = true
    await nextTick()
    expect(display.showMinMax.value).toBe(false)
  })

  it('leaves a band ticked back on while the trace was hidden alone', async () => {
    display.showFftTrace.value = false
    await nextTick()
    display.showMinMax.value = true
    await nextTick()

    // The newer choice is the user's, so restoring the remembered `true`/`false` over it would undo
    // something they just did.
    display.showFftTrace.value = true
    await nextTick()
    expect(display.showMinMax.value).toBe(true)
  })

  it('does not rewrite a loaded measurement’s band', async () => {
    // A type saved before the linkage existed can hold this pair; loading is not an edit, so the
    // watcher must stay out of it (the `restoring` guard) and the stored band must survive.
    display.applyLoaded({ chartShowFftTrace: false, chartShowMinMax: true })
    await nextTick()
    expect(display.showFftTrace.value).toBe(false)
    expect(display.showMinMax.value).toBe(true)
  })
})
