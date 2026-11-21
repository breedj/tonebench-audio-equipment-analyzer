import { describe, expect, it } from 'vitest'
import { FOOTPRINT_HEIGHT, FOOTPRINT_WIDTH, IMD_FOOTPRINTS, imdFootprint } from './footprints'
import { IMD_PRESETS, IMD_SECTIONS, formatPresetHz, imdPresetChips, imdSections } from './presets'

/** Every x and y a path puts ink at. These are written as absolute M / V pairs only. */
function points(d: string): { xs: number[]; ys: number[] } {
  const xs: number[] = []
  const ys: number[] = []
  let x = 0
  for (const token of d.match(/[MV][^MV]*/g) ?? []) {
    const n = [...token.matchAll(/-?\d+(?:\.\d+)?/g)].map((m) => Number(m[0]))
    if (token[0] === 'M') {
      x = n[0]
      xs.push(n[0])
      ys.push(n[1])
    } else {
      xs.push(x)
      ys.push(n[0])
    }
  }
  return { xs, ys }
}

describe('the IMD footprints', () => {
  it('draws one for every preset, inside its own box', () => {
    for (const preset of IMD_PRESETS) {
      const figure = imdFootprint(preset.value)
      expect(figure, `${preset.value} has no footprint`).not.toBeNull()
      for (const d of [figure!.grid, figure!.f1, figure!.f2, figure!.products]) {
        if (!d) continue
        const { xs, ys } = points(d)
        for (const v of xs) {
          expect(v, `${preset.value} puts ink at x=${v}`).toBeGreaterThanOrEqual(0)
          expect(v, `${preset.value} puts ink at x=${v}`).toBeLessThanOrEqual(FOOTPRINT_WIDTH)
        }
        for (const v of ys) {
          expect(v, `${preset.value} puts ink at y=${v}`).toBeGreaterThanOrEqual(0)
          expect(v, `${preset.value} puts ink at y=${v}`).toBeLessThanOrEqual(FOOTPRINT_HEIGHT)
        }
      }
    }
    expect(imdFootprint('nonesuch')).toBeNull()
  })

  /**
   * The one that matters: the bars are at the real logarithmic positions of the real frequencies,
   * which is the whole of what the picture is for. A footprint drawn in the wrong part of the band
   * would look perfectly deliberate.
   */
  it('puts each preset where in the band it really sits', () => {
    const at = (d: string) => points(d).xs[0]
    const smpte = imdFootprint('Smpte')!
    const ccif = imdFootprint('CcifItu')!
    // 60 Hz is near the left edge and 19 kHz near the right, on a 20 Hz - 40 kHz log axis.
    expect(at(smpte.f1)).toBeLessThan(FOOTPRINT_WIDTH * 0.25)
    expect(at(ccif.f1)).toBeGreaterThan(FOOTPRINT_WIDTH * 0.85)
    // DIN's low tone is above SMPTE's, which is the whole difference between them.
    expect(at(imdFootprint('Din')!.f1)).toBeGreaterThan(at(smpte.f1))
    // And the twin-tone pair's own difference product lands far below the pair itself.
    expect(at(ccif.products)).toBeLessThan(at(ccif.f1) - 20)
  })

  it('gives the modulation pair its 12 dB ratio and the twin pair equal tones', () => {
    const top = (d: string) => points(d).ys[1]
    const smpte = imdFootprint('Smpte')!
    // 4:1 in amplitude is 12.04 dB, so f2 is drawn lower than f1 by that much of the box.
    expect(top(smpte.f2)).toBeGreaterThan(top(smpte.f1))
    const ccif = imdFootprint('CcifItu')!
    expect(top(ccif.f1)).toBe(top(ccif.f2))
  })

  it('draws Custom dashed and with no products, because it promises neither', () => {
    const custom = imdFootprint('Custom')!
    expect(custom.dashed).toBe(true)
    expect(custom.products).toBe('')
    for (const preset of IMD_PRESETS) {
      if (preset.value === 'Custom') continue
      expect(IMD_FOOTPRINTS[preset.value].dashed, preset.value).toBe(false)
      expect(IMD_FOOTPRINTS[preset.value].products.length, preset.value).toBeGreaterThan(0)
    }
  })
})

describe('the IMD picker’s sections', () => {
  it('offers every preset exactly once', () => {
    const offered = imdSections().flatMap((entry) => entry.items.map((item) => item.value))
    expect(offered.sort()).toEqual(IMD_PRESETS.map((p) => p.value).sort())
  })

  it('sorts by method, which is the thing being chosen', () => {
    const shown = imdSections()
    expect(shown.map((entry) => entry.section.id)).toEqual(IMD_SECTIONS.map((s) => s.id))
    expect(shown[0].items.map((p) => p.value)).toEqual(['Smpte', 'Din'])
    expect(shown[1].items.map((p) => p.value)).toEqual(['CcifItu', 'Ccif18p5'])
    expect(shown[2].items.map((p) => p.value)).toEqual(['Custom'])
  })

  it('marks Custom as the one that is not a standard', () => {
    // Slate rather than an accent: a hue there would make it look like a third named test.
    expect(IMD_SECTIONS.find((s) => s.id === 'custom')?.tone).toBe('slate')
  })

  it('writes the chips in the figures the preset really plays', () => {
    expect(imdPresetChips(IMD_PRESETS.find((p) => p.value === 'Smpte')!).map((c) => c.text)).toEqual([
      '60 Hz',
      '7 kHz',
      '4:1',
    ])
    expect(imdPresetChips(IMD_PRESETS.find((p) => p.value === 'Ccif18p5')!).map((c) => c.text)).toEqual([
      '18.5 kHz',
      '19.5 kHz',
      '1:1',
    ])
    // Custom has no figures of its own, so it names its fields instead.
    expect(imdPresetChips(IMD_PRESETS[0]).map((c) => c.text)).toEqual(['f1', 'f2', 'ratio'])
  })

  it('writes a frequency the way a chip wants it', () => {
    expect(formatPresetHz(60)).toBe('60 Hz')
    expect(formatPresetHz(7000)).toBe('7 kHz')
    expect(formatPresetHz(18500)).toBe('18.5 kHz')
  })

  it('gives every preset a name and a sentence of its own', () => {
    for (const preset of IMD_PRESETS) {
      expect(preset.name.length, preset.value).toBeGreaterThan(2)
      expect(preset.description.length, preset.value).toBeGreaterThan(30)
      // The short name is not the long one: the tile carries the figures as chips beside it.
      if (preset.value !== 'Custom') expect(preset.name).not.toBe(preset.label)
    }
  })
})
