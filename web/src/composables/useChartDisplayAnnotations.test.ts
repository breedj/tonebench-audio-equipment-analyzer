import { beforeEach, describe, expect, it } from 'vitest'
import { useChartDisplay } from './useChartDisplay'
import { useAnnotations } from './useAnnotations'
import type { ChartAnnotation } from '../measurements/chartAnnotations'

/**
 * The FFT screen's display restore must not touch the shared annotation store.
 *
 * <p>`App.vue` calls `applyLoaded` from its `loadedParams` watcher for **every** measurement kind,
 * because that watcher is where the applied type arrives; the annotations, meanwhile, belong to
 * whichever measurement is open, of whatever kind. So a write here overwrote what a response, sweep
 * or impedance screen had just restored from its own `params_json` — with the empty list
 * `ApplyAsync` hands back for those kinds, since its `HasNoLiveConfiguration` arm returns live state
 * and a non-FFT measurement's notes live only on its type.</p>
 *
 * <p>The symptom was that every non-FFT measurement lost its notes on reload while the FFT's
 * survived, which is a confusing enough split to be worth a test of its own: the two writers raced
 * and the FFT one, being driven by an HTTP response, happened to land last. Found 2026-10-18 by
 * asking whether annotations survive a reload. The restore is now one kind-gated line in that
 * watcher, and this pins the half that can be tested.</p>
 */
describe('useChartDisplay.applyLoaded', () => {
  beforeEach(() => {
    useAnnotations().applyLoadedLive([])
  })

  function note(text: string): ChartAnnotation {
    return {
      id: 1,
      style: 'point',
      text,
      view: 'Bode',
      x: 1000,
      y: -40,
      x2: null,
      y2: null,
      xQuantity: 'hz',
      yQuantity: 'dbv',
      channel: 0,
      labelOffsetX: 0.45,
      labelOffsetY: 14,
    }
  }

  it('leaves the open measurement’s annotations alone, whatever the document carries', () => {
    const annotations = useAnnotations()
    annotations.applyLoadedLive([note('the response screen just restored this')])

    // What ApplyAsync hands back for a non-FFT kind: live state, with no annotations on it.
    useChartDisplay().applyLoaded({ chartYTopDbv: 10 })
    expect(annotations.live.value.map((a) => a.text)).toEqual(['the response screen just restored this'])

    // And a document that *does* carry some is still not this function's to apply.
    useChartDisplay().applyLoaded({ chartAnnotations: [note('from a stored FFT type')] })
    expect(annotations.live.value.map((a) => a.text)).toEqual(['the response screen just restored this'])
  })

  it('still restores the display fields it does own', () => {
    const display = useChartDisplay()
    display.applyLoaded({ chartYTopDbv: 5, chartYBottomDbv: -95, chartXFromHz: 30, chartXToHz: 30000 })

    expect(display.yTop.value).toBe(5)
    expect(display.yBottom.value).toBe(-95)
    expect(display.xFrom.value).toBe(30)
    expect(display.xTo.value).toBe(30000)
  })
})
