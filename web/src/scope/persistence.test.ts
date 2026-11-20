import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  ALPHA_KNEE,
  hexRgb,
  intensityAlpha,
  intensityPaletteIndex,
  intensityPixels,
  intensitySignature,
  scopeCaptureSignature,
  type ScopeCaptureFacts,
  persistenceDecay,
  persistenceDraws,
  ScopeIntensityBuffer,
  SCOPE_PERSISTENCE_OPTIONS,
  SCOPE_PERSISTENCE_RECORDS,
  type ScopePersistence,
} from './persistence'

describe('persistenceDecay', () => {
  it('fades a single hit to one step of a 256-level ramp after the records it names', () => {
    // A half-life or a 1/e time would leave a third of the brightness at N, which reads as a
    // setting about three times longer than it says.
    for (const name of ['Short', 'Medium', 'Long'] as ScopePersistence[]) {
      const records = SCOPE_PERSISTENCE_RECORDS[name]!
      const survives = Math.pow(persistenceDecay(name), records)
      expect(survives, name).toBeCloseTo(1 / 255, 9)
    }
  })

  it('keeps everything for ever when asked to', () => {
    expect(persistenceDecay('Infinite')).toBe(1)
  })

  it('keeps nothing at all when off', () => {
    expect(persistenceDecay('Off')).toBe(0)
  })

  it('fades faster the shorter the setting', () => {
    expect(persistenceDecay('Short')).toBeLessThan(persistenceDecay('Medium'))
    expect(persistenceDecay('Medium')).toBeLessThan(persistenceDecay('Long'))
  })
})

describe('the dropdown', () => {
  it('offers every setting the table knows, and nothing it does not', () => {
    expect(SCOPE_PERSISTENCE_OPTIONS.map((o) => o.value).sort()).toEqual(
      Object.keys(SCOPE_PERSISTENCE_RECORDS).sort(),
    )
  })

  it('labels each one with the records it keeps, so the unit is never in doubt', () => {
    // A record count rather than a time: records arrive at a rate set by the record length, the
    // trigger and the DUT, so "two seconds" is anywhere between one record and two hundred.
    for (const option of SCOPE_PERSISTENCE_OPTIONS) {
      const records = SCOPE_PERSISTENCE_RECORDS[option.value]
      if (records) expect(option.label).toContain(String(records))
    }
  })

  it('draws nothing when off and something otherwise', () => {
    expect(persistenceDraws('Off')).toBe(false)
    for (const name of ['Short', 'Medium', 'Long', 'Infinite'] as ScopePersistence[]) {
      expect(persistenceDraws(name), name).toBe(true)
    }
  })
})

describe('intensityAlpha', () => {
  it('paints an untouched pixel not at all', () => {
    // Zero hits must be transparent rather than the palette's own floor colour: a wash over every
    // pixel the trace has never been near would be a second, false trace.
    expect(intensityAlpha(0, 'Single')).toBe(0)
    expect(intensityAlpha(0, 'Multi')).toBe(0)
    expect(intensityAlpha(-1, 'Multi')).toBe(0)
  })

  it('is full at the peak under either mode', () => {
    expect(intensityAlpha(1, 'Single')).toBeCloseTo(1, 9)
    expect(intensityAlpha(1, 'Multi')).toBeCloseTo(1, 9)
    expect(intensityAlpha(5, 'Multi')).toBeCloseTo(1, 9)
  })

  it('lifts the faint end under a single colour, where nothing else can carry it', () => {
    // Linear alpha makes everything but the newest trace invisible the moment one record dominates.
    expect(intensityAlpha(0.01, 'Single')).toBeGreaterThan(0.05)
  })

  it('is opaque above the knee under a colour map, leaving the rest to the hue', () => {
    expect(intensityAlpha(ALPHA_KNEE, 'Multi')).toBeCloseTo(1, 9)
    expect(intensityAlpha(ALPHA_KNEE / 2, 'Multi')).toBeCloseTo(0.5, 9)
  })

  it('rises with intensity under both', () => {
    for (const mode of ['Single', 'Multi'] as const) {
      expect(intensityAlpha(0.3, mode), mode).toBeGreaterThan(intensityAlpha(0.1, mode))
    }
  })
})

describe('intensityPaletteIndex', () => {
  it('reaches the palette own floor, unlike every decay surface here', () => {
    // Those lift the mapping off the floor because a near-black foot makes a *level* scale
    // unreadable; here the faint end is the alpha's job, and lifting the colour too would make a
    // pixel touched once read as warm as one touched fifty times.
    expect(intensityPaletteIndex(0)).toBe(0)
    expect(intensityPaletteIndex(1)).toBe(255)
    expect(intensityPaletteIndex(0.5)).toBeGreaterThan(100)
    expect(intensityPaletteIndex(0.5)).toBeLessThan(155)
  })
})

describe('ScopeIntensityBuffer', () => {
  it('counts a span once per record, every pixel of it', () => {
    const buffer = new ScopeIntensityBuffer(4, 4)
    buffer.addSpan(1, 1, 3)
    expect(buffer.peak).toBe(1)
    expect(Array.from(buffer.data)).toEqual([0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0])

    buffer.addSpan(1, 1, 3)
    expect(buffer.peak).toBe(2)
  })

  it('reads a span either way round', () => {
    const up = new ScopeIntensityBuffer(2, 4)
    const down = new ScopeIntensityBuffer(2, 4)
    up.addSpan(0, 0, 3)
    down.addSpan(0, 3, 0)
    expect(Array.from(up.data)).toEqual(Array.from(down.data))
  })

  it('clips to the plot rather than wrapping on to the next row', () => {
    // A column index off the end would otherwise land in the next row's cells, which draws a trace
    // nothing measured.
    const buffer = new ScopeIntensityBuffer(3, 3)
    buffer.addSpan(5, 0, 2)
    buffer.addSpan(-1, 0, 2)
    expect(buffer.peak).toBe(0)

    buffer.addSpan(0, -10, 10)
    expect(Array.from(buffer.data)).toEqual([1, 0, 0, 1, 0, 0, 1, 0, 0])
  })

  it('fades what is already there without touching what arrives next', () => {
    const buffer = new ScopeIntensityBuffer(2, 1)
    buffer.addSpan(0, 0, 0)
    buffer.decay(0.5)
    buffer.addSpan(1, 0, 0)
    expect(buffer.data[0]).toBeCloseTo(0.5, 9)
    expect(buffer.data[1]).toBeCloseTo(1, 9)
    expect(buffer.peak).toBeCloseTo(1, 9)
  })

  it('keeps everything at a factor of one, and nothing at zero', () => {
    const buffer = new ScopeIntensityBuffer(1, 1)
    buffer.addSpan(0, 0, 0)
    buffer.decay(1)
    expect(buffer.data[0]).toBe(1)
    buffer.decay(0)
    expect(buffer.data[0]).toBe(0)
    expect(buffer.peak).toBe(0)
  })

  it('renormalises without changing a single ratio', () => {
    // Only the ratios are ever read, so scaling changes nothing on screen -- but counts left to
    // grow for a day under infinite persistence would lose their low end to rounding.
    const buffer = new ScopeIntensityBuffer(2, 1)
    for (let i = 0; i < 10; i++) buffer.addSpan(0, 0, 0)
    buffer.addSpan(1, 0, 0)
    const before = buffer.data[1] / buffer.data[0]

    buffer.renormalise(5)
    expect(buffer.peak).toBeCloseTo(1, 9)
    // Six places, which is what a Float32Array carries -- the point is that the ratio survives,
    // and asserting past single precision would be asserting against the storage.
    expect(buffer.data[1] / buffer.data[0]).toBeCloseTo(before, 6)
  })

  it('leaves a small buffer alone', () => {
    const buffer = new ScopeIntensityBuffer(1, 1)
    buffer.addSpan(0, 0, 0)
    buffer.renormalise(1e6)
    expect(buffer.peak).toBe(1)
  })
})

describe('intensitySignature', () => {
  it('changes whenever a pixel would stop meaning the same time and the same level', () => {
    const base = intensitySignature(700, 600, -0.005, 0.015, -2, 2)
    expect(intensitySignature(701, 600, -0.005, 0.015, -2, 2)).not.toBe(base)
    expect(intensitySignature(700, 601, -0.005, 0.015, -2, 2)).not.toBe(base)
    expect(intensitySignature(700, 600, -0.004, 0.015, -2, 2)).not.toBe(base)
    expect(intensitySignature(700, 600, -0.005, 0.016, -2, 2)).not.toBe(base)
    expect(intensitySignature(700, 600, -0.005, 0.015, -1, 2)).not.toBe(base)
    expect(intensitySignature(700, 600, -0.005, 0.015, -2, 4)).not.toBe(base)
    expect(intensitySignature(700, 600, -0.005, 0.015, -2, 2, 'other')).not.toBe(base)
  })

  it('is the same for the same picture', () => {
    expect(intensitySignature(700, 600, -0.005, 0.015, -2, 2, 'm1')).toBe(
      intensitySignature(700, 600, -0.005, 0.015, -2, 2, 'm1'),
    )
  })
})

describe('intensityPixels', () => {
  const blue: [number, number, number] = [0x3b, 0x82, 0xf6]

  it('paints an empty buffer as nothing at all', () => {
    const buffer = new ScopeIntensityBuffer(2, 1)
    const pixels = intensityPixels(buffer, 'Single', 'Jet', blue)
    expect(Array.from(pixels)).toEqual([0, 0, 0, 0, 0, 0, 0, 0])
  })

  it('paints the channel own colour under Single, with the alpha carrying the count', () => {
    const buffer = new ScopeIntensityBuffer(2, 1)
    for (let i = 0; i < 4; i++) buffer.addSpan(0, 0, 0)
    buffer.addSpan(1, 0, 0)

    const pixels = intensityPixels(buffer, 'Single', 'Jet', blue)
    expect(Array.from(pixels.slice(0, 3))).toEqual(blue)
    expect(Array.from(pixels.slice(4, 7))).toEqual(blue)
    expect(pixels[3]).toBe(255)
    // One hit of four is a quarter, read through the square root.
    expect(pixels[7]).toBe(Math.round(Math.sqrt(0.25) * 255))
  })

  it('paints the colour map under Multi, so a rare pixel is a different hue from a common one', () => {
    const buffer = new ScopeIntensityBuffer(2, 1)
    for (let i = 0; i < 20; i++) buffer.addSpan(0, 0, 0)
    buffer.addSpan(1, 0, 0)

    const pixels = intensityPixels(buffer, 'Multi', 'Jet', blue)
    const hot = Array.from(pixels.slice(0, 3))
    const cold = Array.from(pixels.slice(4, 7))
    expect(hot).not.toEqual(cold)
    // Jet runs cold to hot, so the common pixel is redder than the rare one.
    expect(hot[0]).toBeGreaterThan(cold[0])
    expect(cold[2]).toBeGreaterThan(hot[2])
  })

  it('reuses the array it is given', () => {
    const buffer = new ScopeIntensityBuffer(2, 1)
    buffer.addSpan(0, 0, 0)
    const out = new Uint8ClampedArray(8)
    expect(intensityPixels(buffer, 'Single', 'Jet', blue, out)).toBe(out)
    expect(out[3]).toBe(255)
  })
})

describe('ScopeIntensityBuffer.addLine', () => {
  function cells(buffer: ScopeIntensityBuffer): number[][] {
    const rows: number[][] = []
    for (let y = 0; y < buffer.height; y++) {
      const row: number[] = []
      for (let x = 0; x < buffer.width; x++) row.push(buffer.data[y * buffer.width + x])
      rows.push(row)
    }
    return rows
  }

  // A line and not a point is the whole difference between a goniometer and a cloud of dots: at
  // any usable sample rate the consecutive pairs of an X-Y figure are pixels apart wherever the
  // locus moves quickly, so plotting the pairs alone leaves gaps exactly where the signal is
  // fastest -- which reads as a dropout rather than as the sampling it is.
  it('lights every cell between its two ends', () => {
    const buffer = new ScopeIntensityBuffer(5, 1)
    buffer.addLine(0, 0, 4, 0)

    expect(cells(buffer)[0]).toEqual([1, 1, 1, 1, 1])
  })

  it('walks a diagonal without skipping a cell', () => {
    const buffer = new ScopeIntensityBuffer(4, 4)
    buffer.addLine(0, 0, 3, 3)

    expect(cells(buffer)).toEqual([
      [1, 0, 0, 0],
      [0, 1, 0, 0],
      [0, 0, 1, 0],
      [0, 0, 0, 1],
    ])
  })

  it('runs in either direction and is the same line', () => {
    const forward = new ScopeIntensityBuffer(6, 4)
    const backward = new ScopeIntensityBuffer(6, 4)
    forward.addLine(0, 3, 5, 0)
    backward.addLine(5, 0, 0, 3)

    expect(cells(backward)).toEqual(cells(forward))
  })

  // Across-record frequency is what this buffer measures. Counting a cell twice within one call
  // would let a slow stretch of the locus outshine a fast one inside the same record, which is a
  // different quantity quietly mixed into the one on screen.
  it('counts each cell at most once per call', () => {
    const buffer = new ScopeIntensityBuffer(3, 3)
    buffer.addLine(1, 1, 1, 1)

    expect(buffer.peak).toBe(1)
    expect(cells(buffer)[1][1]).toBe(1)
  })

  it('counts a cell once per call and so builds up across records', () => {
    const buffer = new ScopeIntensityBuffer(3, 1)
    buffer.addLine(0, 0, 2, 0)
    buffer.addLine(0, 0, 2, 0)

    expect(cells(buffer)[0]).toEqual([2, 2, 2])
    expect(buffer.peak).toBe(2)
  })

  // Dropped rather than clamped: a clamped hit would pile the whole off-screen stretch of the
  // locus onto one edge cell, which draws a bright line along the frame that is not a measurement.
  it('drops what falls outside rather than piling it on the edge', () => {
    const buffer = new ScopeIntensityBuffer(3, 3)
    buffer.addLine(-10, 1, 10, 1)

    expect(cells(buffer)[1]).toEqual([1, 1, 1])
    expect(buffer.peak).toBe(1)
  })

  it('writes nothing at all for a segment wholly outside', () => {
    const buffer = new ScopeIntensityBuffer(3, 3)
    buffer.addLine(-50, -50, -40, -40)

    expect(buffer.peak).toBe(0)
  })

  it('ignores a segment that is not a pair of numbers, rather than throwing on a draw path', () => {
    const buffer = new ScopeIntensityBuffer(3, 3)
    buffer.addLine(Number.NaN, 0, 1, 1)
    buffer.addLine(0, 0, Number.POSITIVE_INFINITY, 1)

    expect(buffer.peak).toBe(0)
  })
})

describe('hexRgb', () => {
  it('reads the channel colours the chart draws with', () => {
    expect(hexRgb('#3b82f6')).toEqual([0x3b, 0x82, 0xf6])
    expect(hexRgb('f97316')).toEqual([0xf9, 0x73, 0x16])
  })

  it('answers white for anything it cannot read, rather than throwing on a draw path', () => {
    expect(hexRgb('rebeccapurple')).toEqual([255, 255, 255])
  })
})

describe('what the charts throw the picture away for', () => {
  // Both halves of this live in .vue files — a paint hook and three watchers — so this reads them
  // as text, the way display.test.ts audits its own channel gate: the components are Vue against a
  // real DOM and the suite runs on environment: 'node'. What is pinned is not the wiring but the one
  // invariant the feature turns on: an accumulated picture may never outlive the thing it is a
  // picture of — and both ways it could, a channel taken off the chart and a Clear, are reached for
  // while the capture is *stopped*, which is exactly when no record arrives to notice.
  const read = (name: string) => readFileSync(resolve(import.meta.dirname, '..', name), 'utf8')

  const waveform = read('components/OscilloscopeScreenChart.vue')
  const xy = read('components/ScopeXyChart.vue')
  const eye = read('components/ScopeEyeChart.vue')
  const rail = read('components/ScopeDisplayPanel.vue')
  const app = read('App.vue')

  it('paints only the layers whose channel is still drawn', () => {
    // The reported fault: the layer set is rebuilt to match the drawn channels when a record
    // arrives, so unticking Left on a stopped capture left its haze painted under a trace that had
    // gone. An unfiltered read of the layers here is what that was, and is what this fails on.
    expect(waveform).toContain('const drawnChannels = intensityChannels.value')
    expect(waveform).toContain('const layers = intensityLayers.value.filter((layer) =>')
    expect(waveform).toContain('drawnChannels.some((channel) => channel.index === layer.index)')
  })

  it('asks one definition of which channels the haze is of, in both places', () => {
    // Two spellings of this rule is how a paint and an accumulation come to disagree about the
    // same picture -- the ImpedanceTraceBuilder lesson, one component along.
    expect(waveform).toContain('const intensityChannels = computed(()')
    expect(waveform.match(/intensityChannels\.value/g)?.length).toBe(2)
  })

  it('folds the capture configuration into the key, so a changed setting throws the picture away', () => {
    // The chart's own signature is in pixels, so it clears on a resize, a zoom or a V/div step and
    // cannot see a sample rate, a record length or a trigger level move -- each of which leaves
    // every axis exactly where it was while making the traces already accumulated a picture of
    // something else. The coupling is in it for the same reason: AC moves the whole trace by the
    // record's own mean.
    expect(app).toContain('scopeCaptureSignature(scopeCaptureFacts.value)')
    expect(app).toContain('${scopePath.value.coupling}')
  })

  it('takes those facts from the request, which is the only thing that carries all of them', () => {
    // Half the capture configuration never reaches App.vue as a model -- the slope, the holdoff,
    // the width bounds, which inputs are captured all live inside ScopeConfigPanel's own form. A
    // signature assembled from the refs beside it would silently miss them.
    expect(app).toContain('scopeCaptureFacts.value = request')
    // Both ways a configuration reaches the analyzer: a fresh Run, and a retune of one in flight.
    expect(app.match(/scopeCaptureFacts\.value = request/g)?.length).toBe(2)
  })

  it('has every persistence chart watch the key, not only fold it into the signature', () => {
    // A signature is read when a record arrives. A Clear pressed on a stopped capture has no
    // record coming, so without the watcher it would clear nothing until the next one.
    for (const chart of [waveform, xy, eye]) {
      expect(chart).toContain('() => props.persistenceKey')
      expect(chart).toContain('resetIntensity()')
    }
  })

  it('offers the Clear where the picture is, and clears all three charts with it', () => {
    expect(rail).toContain("emit('clearPersistence')")
    expect(rail).toContain('Clear persistence')
    expect(app).toContain('@clear-persistence="scopePersistenceCleared++"')
    // One key read by all three: the goniometer and the eye accumulate the same way, and a Clear
    // that reached only the view on screen would leave the other two holding a stale picture.
    expect(app).toContain('const scopePersistenceKey = computed(')
    expect(app.match(/:persistence-key="scopePersistenceKey"/g)?.length).toBe(3)
  })
})

describe('scopeCaptureSignature', () => {
  const base: ScopeCaptureFacts = {
    sampleRateHz: 192_000,
    recordSeconds: 0.02,
    preTriggerFraction: 0.25,
    mode: 'Auto',
    triggerKind: 'Edge',
    triggerSource: 'ChannelA',
    triggerSlope: 'Rising',
    triggerLevelV: 0,
  }

  it('is stable for a configuration that has not moved', () => {
    expect(scopeCaptureSignature({ ...base })).toBe(scopeCaptureSignature({ ...base }))
  })

  it('answers an empty string before anything has been captured', () => {
    expect(scopeCaptureSignature(null)).toBe('')
  })

  // Each of these leaves every axis exactly where it was, which is why the chart's own pixel
  // signature cannot notice them -- and each makes the records already accumulated a picture of
  // something else.
  const moves: [string, Partial<ScopeCaptureFacts>][] = [
    ['the sample rate', { sampleRateHz: 48_000 }],
    ['the record length', { recordSeconds: 0.05 }],
    ['the pre-trigger fraction', { preTriggerFraction: 0.5 }],
    ['the run mode', { mode: 'Normal' }],
    ['the trigger kind', { triggerKind: 'PulseWidth' }],
    ['the trigger source', { triggerSource: 'ChannelB' }],
    ['the slope', { triggerSlope: 'Falling' }],
    ['the trigger level', { triggerLevelV: 0.1 }],
    ['the hysteresis', { triggerHysteresisV: 0.01 }],
    ['the second threshold', { triggerLevelLowV: -0.1 }],
    ['the width condition', { pulseWidthLoSeconds: 1e-4 }],
    ['its upper bound', { pulseWidthHiSeconds: 2e-4 }],
    ['the dropout timeout', { dropoutSeconds: 0.01 }],
    ['the holdoff', { holdoffSeconds: 0.005 }],
    ['the accumulation', { accumulation: 'Average' }],
    ['the average count', { averageCount: 16 }],
    ['which inputs are captured', { processRight: true }],
  ]

  it.each(moves)('changes when %s does', (_what, change) => {
    expect(scopeCaptureSignature({ ...base, ...change })).not.toBe(scopeCaptureSignature(base))
  })

  it('leaves the generator out, so sweeping a stimulus keeps the picture it is being read against', () => {
    const withGenerator = {
      ...base,
      generatorLeft: { enabled: true, tones: [{ frequencyHz: 1000, amplitudeDbv: -6 }] },
    } as ScopeCaptureFacts
    expect(scopeCaptureSignature(withGenerator)).toBe(scopeCaptureSignature(base))
  })
})

describe('the persistence control\u2019s two labels', () => {
  it('gives every option a short label, because the control is a row of buttons on a narrow rail', () => {
    for (const option of SCOPE_PERSISTENCE_OPTIONS) {
      expect(option.shortLabel.length, option.value).toBeGreaterThan(0)
      // Short enough for a fifth of the rail. "128" is the longest figure the ladder reaches.
      expect(option.shortLabel.length, option.value).toBeLessThanOrEqual(3)
    }
  })

  it('keeps the long label, which is what the button says when you ask it', () => {
    const long = SCOPE_PERSISTENCE_OPTIONS.find((o) => o.value === 'Short')
    expect(long?.label).toBe('8 records')
    expect(long?.shortLabel).toBe('8')
  })

  it('still names every setting the ladder holds', () => {
    expect(SCOPE_PERSISTENCE_OPTIONS.map((o) => o.value)).toEqual(
      Object.keys(SCOPE_PERSISTENCE_RECORDS),
    )
  })
})
