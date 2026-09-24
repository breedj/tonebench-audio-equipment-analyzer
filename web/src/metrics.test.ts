import { describe, expect, it } from 'vitest'
import {
  absoluteLevelDbv,
  AVAILABLE_METRIC_LABELS,
  channelTiles,
  dbvToDbu,
  dbvToVrms,
  formatAveraging,
  formatFftSize,
  formatVrms,
  formatNvPerRootHz,
  formatWatts,
  generatorTile,
  multitoneToneCount,
  noiseFigureCaveat,
  noiseInputReferredNvPerRootHz,
  NOT_APPLICABLE,
  noteName,
  powerWatts,
  ratioPercentToDb,
  stimulusSummary,
  systemTiles,
  vrmsToDbv,
} from './metrics'
import type { IntermodulationFrame, MetersFrame, NoiseFiguresFrame } from './streaming/protocol'
import type { GeneratorTone, MultitoneSettings, WhiteNoiseSettings } from './api/client'

// Golden values below are cross-checked against the reference QA40x screenshot's own numbers
// (e.g. THD dB / THD+N dB / N-D dBr all reproduce independently from other tiles in that same
// screenshot), not invented — see the plan's "Metric formulas" table.

describe('dbvToVrms / vrmsToDbv', () => {
  it('round-trip and match the 0 dBV = 1 Vrms convention', () => {
    expect(dbvToVrms(0)).toBeCloseTo(1, 6)
    expect(vrmsToDbv(1)).toBeCloseTo(0, 6)
    expect(dbvToVrms(-17.6)).toBeCloseTo(0.1318, 3) // ~131.8 mVrms, matches the screenshot's RMS Volts
    expect(vrmsToDbv(dbvToVrms(-42.7))).toBeCloseTo(-42.7, 6)
  })
})

describe('dbvToDbu', () => {
  it('adds the 0.775V-vs-1V reference offset (~2.218 dB)', () => {
    // Screenshot: RMS dBV -17.60 -> RMS dBu -15.38.
    expect(dbvToDbu(-17.6)).toBeCloseTo(-15.38, 1)
  })
})

describe('ratioPercentToDb', () => {
  it('matches the screenshot THD/THD+N dB conversions', () => {
    expect(ratioPercentToDb(0.00861)).toBeCloseTo(-81.3, 1) // screenshot: THD dB -81.30
    expect(ratioPercentToDb(0.0988)).toBeCloseTo(-60.1, 1) // screenshot: THD+N dB -60.10
  })
})

describe('absoluteLevelDbv', () => {
  it('recombines a dBc ratio with the fundamental into an absolute dBV level', () => {
    // Screenshot: fundamental -17.60 dBV, THD dB -81.30 -> THD dBr -98.90.
    expect(absoluteLevelDbv(-17.6, -81.3)).toBeCloseTo(-98.9, 1)
    // fundamental -17.60 dBV, THD+N dB -60.10 -> THD+N dBr -77.70 (screenshot shows -77.71, 0.01 rounding).
    expect(absoluteLevelDbv(-17.6, -60.1)).toBeCloseTo(-77.7, 1)
  })
})

describe('powerWatts', () => {
  it('reproduces the screenshot Pk/RMS power readings at 8Ω', () => {
    // Screenshot: 131.7 mVrms @ 8Ω -> 2.16 mW.
    expect(powerWatts(0.1317, 8)).toBeCloseTo(0.00217, 4)
    // Screenshot: 131.8 mV @ 8Ω -> 2.17 mW.
    expect(powerWatts(0.1318, 8)).toBeCloseTo(0.00217, 4)
  })
})

describe('formatVrms / formatWatts', () => {
  it('switches to milli-units below 1', () => {
    expect(formatVrms(0.1317)).toBe('131.7 mVrms')
    expect(formatVrms(1.5)).toBe('1.5000 Vrms')
    expect(formatWatts(0.00217, 8)).toBe('2.17 mW (8Ω)')
    expect(formatWatts(1.2, 8)).toBe('1.200 W (8Ω)')
  })
})

describe('formatFftSize', () => {
  it('formats binary-K sizes and passes through anything else', () => {
    expect(formatFftSize(131072)).toBe('128k') // screenshot: Sys:FFT = 128k
    expect(formatFftSize(65536)).toBe('64k')
    expect(formatFftSize(4096)).toBe('4k')
    expect(formatFftSize(1000)).toBe('1000')
  })
})

describe('formatAveraging', () => {
  it('describes Off/Linear/Exponential', () => {
    expect(formatAveraging('Off', 8)).toBe('Off') // screenshot: Sys:Averages = Off
    expect(formatAveraging('Linear', 8)).toBe('Linear x8')
    expect(formatAveraging('Exponential', 16)).toBe('Exponential τ=16')
  })

  it('appends a taken/target progress count, capped at the target, when taken is given', () => {
    expect(formatAveraging('Linear', 8, 3)).toBe('Linear x8 (3/8)')
    expect(formatAveraging('Linear', 8, 8)).toBe('Linear x8 (8/8)')
    expect(formatAveraging('Linear', 8, 20)).toBe('Linear x8 (8/8)') // capped, never over target
    expect(formatAveraging('Off', 8, 0)).toBe('Off') // Off ignores taken entirely
  })
})

describe('noteName', () => {
  it('identifies exact concert pitch with no deviation symbols', () => {
    expect(noteName(440)).toBe('A4')
  })

  it('identifies a sharp note with deviation symbols, roughly matching the screenshot B5+++', () => {
    // 1000.488 Hz is ~22 cents sharp of B5 (987.77 Hz) — same fundamental as the screenshot.
    expect(noteName(1000.488)).toMatch(/^B5\+{1,3}$/)
  })

  it('returns an em dash for non-positive frequencies', () => {
    expect(noteName(0)).toBe('—')
    expect(noteName(-10)).toBe('—')
  })
})

describe('channelTiles', () => {
  const meters: MetersFrame = {
    header: { msgType: 3, channel: 0, flags: 0, seq: 1, x0: 1000.488, dx: 1.46, nPoints: 0 },
    fundamentalFreqHz: 1000.488,
    binSizeHz: 1.46,
    thdPercent: 0.00861,
    thdNPercent: 0.0988,
    snrDb: 60.7,
    fundamentalDbv: -17.6,
    noiseFloorDbv: -78.3, // fundamentalDbv - snrDb, as SpectrumPipeline already computes it
    peakDbv: -17.61,
    totalRmsDbv: -17.6,
    harmonics: [],
    otherPeaks: [],
    averagesTaken: 0,
    held: false,
  }

  it('returns an empty list when there is no data yet', () => {
    expect(channelTiles(null, [], 8)).toEqual([])
  })

  it('derives N-D/SNR dBr directly from the already-sent noiseFloorDbv field', () => {
    const tiles = channelTiles(meters, [], 8)
    expect(tiles.find((t) => t.label === 'N-D dBr')?.value).toBe('-78.30 dBr')
    expect(tiles.find((t) => t.label === 'SNR dBr')?.value).toBe('-78.30 dBr')
  })

  it('computes Gain dB against the channel\'s own first generator tone', () => {
    const tones: GeneratorTone[] = [{ freqHz: 1000, ampDbv: -17.58 }]
    const tiles = channelTiles(meters, tones, 8)
    // -17.60 (measured) - (-17.58) (generator) = -0.02 dB, matching the screenshot's Gain dB.
    expect(tiles.find((t) => t.label === 'Gain dB')?.value).toBe('-0.02 dB')
  })

  it('shows Gain dB as unavailable when the channel has no generator tone', () => {
    const tiles = channelTiles(meters, [], 8)
    expect(tiles.find((t) => t.label === 'Gain dB')?.value).toBe('—')
  })

  it('no longer carries a per-tone GenN tile — see generatorTile() below', () => {
    const tones: GeneratorTone[] = [{ freqHz: 1000, ampDbv: -6 }]
    const tiles = channelTiles(meters, tones, 8)
    expect(tiles.some((t) => /^Gen/.test(t.label))).toBe(false)
  })

  const imd: IntermodulationFrame = {
    header: { msgType: 10, channel: 0, flags: 0, seq: 1, x0: 58.59, dx: 7001.95, nPoints: 0 },
    toneLowHz: 58.59,
    toneHighHz: 7001.95,
    method: 'Smpte',
    maxOrder: 5,
    toneLowDbv: -1.94,
    toneHighDbv: -13.98,
    imdPercent: 0.01,
    sineEquivalentDbv: 0,
    combinedRmsDbv: -2.28,
    referenceDbv: -13.98,
    perOrder: [],
    products: [],
    held: false,
  }

  it('adds IMD % and IMD dB tiles when an intermodulation frame is present', () => {
    const tiles = channelTiles(meters, [], 8, imd)
    expect(tiles.find((t) => t.label === 'IMD %')?.value).toBe('0.01000 %')
    expect(tiles.find((t) => t.label === 'IMD dB')?.value).toBe('-80.00 dB')
    expect(AVAILABLE_METRIC_LABELS).toContain('IMD %')
    expect(AVAILABLE_METRIC_LABELS).toContain('IMD dB')
  })

  it('omits the IMD tiles entirely without one — no reading is not a reading', () => {
    const tiles = channelTiles(meters, [], 8)
    expect(tiles.some((t) => t.label.startsWith('IMD'))).toBe(false)
  })

  const noise: NoiseFiguresFrame = {
    header: { msgType: 12, channel: 0, flags: 0, seq: 1, x0: 20, dx: 20000, nPoints: 0 },
    measurementLoHz: 20,
    measurementHiHz: 20000,
    bandRmsDbv: -17.61,
    noiseDensityDbvPerRootHz: -150,
    enbwHz: 2.2,
    weighting: { standard: 'None', user1: false, user2: false },
    held: false,
  }

  it('adds the band RMS, density and input-referred noise tiles from the noise frame', () => {
    const tiles = channelTiles(meters, [], 8, null, false, noise, 0)
    expect(tiles.find((t) => t.label === 'RMS dBV(bw)')?.value).toBe('-17.61 dBV')
    expect(tiles.find((t) => t.label === 'Noise dBV/√Hz')?.value).toBe('-150.00 dBV/√Hz')
    expect(tiles.find((t) => t.label === 'Noise in nV/√Hz')?.value).toBe('31.6 nV/√Hz')
    expect(channelTiles(meters, [], 8, null, false, noise, 20).find((t) => t.label === 'Noise in nV/√Hz')?.value).toBe('3.16 nV/√Hz')
    for (const label of ['RMS dBV(bw)', 'Noise dBV/√Hz', 'Noise in nV/√Hz']) expect(AVAILABLE_METRIC_LABELS).toContain(label)
  })

  it('shows the noise tiles alone when there is no fundamental, and none without a noise frame', () => {
    const alone = channelTiles(null, [], 8, null, false, noise, 0)
    expect(alone.map((t) => t.label)).toEqual(['RMS dBV(bw)', 'Noise dBV/√Hz', 'Noise in nV/√Hz'])
    expect(channelTiles(meters, [], 8).some((t) => t.label.startsWith('Noise'))).toBe(false)
  })

  it('qualifies only the noise-integrated tiles with the weighting', () => {
    const weighted = { ...noise, weighting: { standard: 'A' as const, user1: true, user2: false } }
    const tiles = channelTiles(meters, [], 8, null, false, weighted, 0)
    const qualified = tiles.filter((t) => t.qualifier).map((t) => t.label)
    expect(qualified).toEqual([
      'THD+N %', 'THD+N dB', 'THD+N dBr', 'THD+N dBV', 'N+D dBr', 'N+D dBV', 'N-D dBr', 'N-D dBV', 'SNR dB', 'SNR dBr',
      'RMS dBV(bw)', 'Noise dBV/√Hz', 'Noise in nV/√Hz',
    ])
    expect(tiles.find((t) => t.label === 'SNR dB')?.qualifier).toBe('A+U1')
    expect(tiles.find((t) => t.label === 'THD %')?.qualifier).toBeUndefined()
    expect(tiles.find((t) => t.label === 'RMS dBV')?.qualifier).toBeUndefined()
    // Unweighted: no qualifier anywhere.
    expect(channelTiles(meters, [], 8, null, false, noise, 0).some((t) => t.qualifier)).toBe(false)
  })

  // The bench case this was written for: SMPTE 60 Hz + 7 kHz at -6 dBV sine-equivalent through a
  // loopback read THD+N 399.83521 % and SNR -12.04 dB on a perfectly healthy QA403, because the
  // 7 kHz tone is 12.04 dB under the 60 Hz one and lands in the noise-and-distortion integral.
  const twoTone = { kind: 'Tones' as const, toneCount: 2 }

  it('reads the noise figures as n/a against a two-tone stimulus, with the reason on the tile', () => {
    const tiles = channelTiles(meters, [], 8, imd, false, noise, 0, null, twoTone)
    const na = tiles.filter((t) => t.value === NOT_APPLICABLE).map((t) => t.label)
    expect(na).toEqual([
      'THD+N %', 'THD+N dB', 'THD+N dBr', 'THD+N dBV', 'N+D dBr', 'N+D dBV', 'N-D dBr', 'N-D dBV', 'SNR dB', 'SNR dBr',
      'RMS dBV(bw)', 'Noise dBV/√Hz', 'Noise in nV/√Hz',
    ])
    expect(tiles.find((t) => t.label === 'SNR dB')?.caveat).toMatch(/two-tone pair/)
    expect(tiles.every((t) => (t.caveat === undefined) === (t.value !== NOT_APPLICABLE))).toBe(true)
  })

  it('leaves THD, IMD, the levels and the fundamental alone — those are what a two-tone test is for', () => {
    const tiles = channelTiles(meters, [], 8, imd, false, noise, 0, null, twoTone)
    const valueOf = (label: string) => tiles.find((t) => t.label === label)?.value
    expect(valueOf('THD %')).toBe('0.00861 %')
    expect(valueOf('IMD %')).toBe('0.01000 %')
    expect(valueOf('Freq Hz')).toBe('1000.488 Hz')
    expect(valueOf('RMS dBV')).toBe('-17.60 dBV')
    expect(valueOf('Pk dBV')).toBe('-17.61 dBV')
  })

  it('suppresses exactly the tiles a weighting qualifies, and drops the qualifier with the number', () => {
    const weighted = { ...noise, weighting: { standard: 'A' as const, user1: false, user2: false } }
    const qualified = channelTiles(meters, [], 8, null, false, weighted, 0).filter((t) => t.qualifier).map((t) => t.label)
    const suppressed = channelTiles(meters, [], 8, null, false, weighted, 0, null, twoTone)
      .filter((t) => t.value === NOT_APPLICABLE)
    expect(suppressed.map((t) => t.label)).toEqual(qualified)
    expect(suppressed.some((t) => t.qualifier)).toBe(false)
  })

  it('suppresses them for a multitone comb too, and points at TD+N', () => {
    const tiles = channelTiles(meters, [], 8, null, false, noise, 0, null, { kind: 'Multitone', toneCount: 29 })
    expect(tiles.find((t) => t.label === 'THD+N %')?.value).toBe(NOT_APPLICABLE)
    expect(tiles.find((t) => t.label === 'THD+N %')?.caveat).toMatch(/TD\+N/)
  })

  it('leaves every figure alone for one tone, for no staged stimulus, and for a silent generator', () => {
    for (const stimulus of [null, { kind: 'Tones' as const, toneCount: 1 }, { kind: 'Off' as const, toneCount: 0 }]) {
      const tiles = channelTiles(meters, [], 8, null, false, noise, 0, null, stimulus)
      expect(tiles.some((t) => t.value === NOT_APPLICABLE)).toBe(false)
    }
  })
})

describe('noiseFigureCaveat', () => {
  it('names the tone count once there are more than two', () => {
    expect(noiseFigureCaveat({ kind: 'Tones', toneCount: 3 })).toMatch(/3 tones/)
  })

  it('says nothing for a stimulus with no second tone to confuse the integral', () => {
    expect(noiseFigureCaveat(null)).toBeNull()
    expect(noiseFigureCaveat({ kind: 'Tones', toneCount: 1 })).toBeNull()
    expect(noiseFigureCaveat({ kind: 'Tones', toneCount: 0 })).toBeNull()
    expect(noiseFigureCaveat({ kind: 'Off', toneCount: 0 })).toBeNull()
    // Deliberately unflagged: a chirp or a noise generator has no fundamental either, but the
    // measurements that play them do not show these tiles, and a bare noise floor with the
    // generator off is exactly what they are for.
    expect(noiseFigureCaveat({ kind: 'WhiteNoise', toneCount: 0 })).toBeNull()
    expect(noiseFigureCaveat({ kind: 'EchoChirp', toneCount: 0 })).toBeNull()
  })
})

describe('stimulusSummary', () => {
  const multitone = { tonesPerOctave: 5, totalRmsDbv: -50, rolloffDbPerOctave: 0 }

  it('counts the staged tones for Tones and derives the comb for Multitone', () => {
    expect(stimulusSummary('Tones', [{ freqHz: 60, ampDbv: -2 }, { freqHz: 7000, ampDbv: -14 }], multitone))
      .toEqual({ kind: 'Tones', toneCount: 2 })
    expect(stimulusSummary('Multitone', [], multitone))
      .toEqual({ kind: 'Multitone', toneCount: multitoneToneCount(5) })
  })
})

describe('noise density helpers', () => {
  it('refers the density to the input and formats it', () => {
    expect(noiseInputReferredNvPerRootHz(-150, 0)).toBeCloseTo(31.62, 1)
    expect(noiseInputReferredNvPerRootHz(-150, 20)).toBeCloseTo(3.162, 2)
    expect(noiseInputReferredNvPerRootHz(-150, Number.NaN)).toBeCloseTo(31.62, 1)
    expect(formatNvPerRootHz(31.62)).toBe('31.6 nV/√Hz')
    expect(formatNvPerRootHz(3162)).toBe('3.16 µV/√Hz')
    expect(formatNvPerRootHz(Number.NaN)).toBe('—')
  })
})

describe('multitoneToneCount', () => {
  it('matches MultitoneGenerator.Build\'s f_i = 5 * 2^(i/n) walk over the fixed 5 Hz-20 kHz band', () => {
    // Default tonesPerOctave (5) comb — the "9 Gen metrics (...Gen (60 tones))" bug report's own number.
    expect(multitoneToneCount(5)).toBe(60)
    expect(multitoneToneCount(1)).toBe(12)
  })
})

describe('generatorTile', () => {
  const multitone: MultitoneSettings = { tonesPerOctave: 5, totalRmsDbv: -50, rolloffDbPerOctave: 0 }
  const noise: WhiteNoiseSettings = { levelDbv: -20 }

  it('lists every staged Sine tone in one tile, not one tile each', () => {
    const tones: GeneratorTone[] = [
      { freqHz: 1000, ampDbv: -6 },
      { freqHz: 5000, ampDbv: -12 },
    ]
    const tile = generatorTile('Tones', tones, multitone, noise)
    expect(tile?.label).toBe('Gen')
    expect(tile?.value).toBe('1000.000 Hz, -6.00 dBV\n5000.000 Hz, -12.00 dBV')
  })

  it('returns null for Sine with no tones staged yet', () => {
    expect(generatorTile('Tones', [], multitone, noise)).toBeNull()
  })

  it('summarises Multitone as a tone count over its fixed band', () => {
    const tile = generatorTile('Multitone', [], multitone, noise)
    expect(tile).toEqual({ label: 'Gen', value: '(60 tones), 5 Hz - 20 kHz' })
  })

  it('summarises White noise as its configured level', () => {
    const tile = generatorTile('WhiteNoise', [], multitone, noise)
    expect(tile).toEqual({ label: 'Gen', value: 'White noise, -20.00 dBV' })
  })

  it('returns null for kinds with no summary (Off/EchoChirp)', () => {
    expect(generatorTile('Off', [], multitone, noise)).toBeNull()
    expect(generatorTile('EchoChirp', [], multitone, noise)).toBeNull()
  })
})

describe('systemTiles', () => {
  it('formats the channel-independent Sys: tiles', () => {
    const tiles = systemTiles({
      fftSize: 131072,
      window: 'Hann',
      averaging: 'Off',
      averageCount: 8,
      averagesTaken: undefined,
      inputRangeDbv: 42,
      binSizeHz: 1.46,
    })

    expect(tiles.find((t) => t.label === 'Sys:FFT')?.value).toBe('128k')
    expect(tiles.find((t) => t.label === 'Sys:Window')?.value).toBe('Hann')
    expect(tiles.find((t) => t.label === 'Sys:FullScale')?.value).toBe('+42 dBV')
    expect(tiles.find((t) => t.label === 'Sys:Resolution')?.value).toBe('1.46 Hz')
  })
})
