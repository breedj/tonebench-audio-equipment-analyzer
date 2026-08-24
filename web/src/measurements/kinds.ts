// The set of measurement kinds the app can create — one entry per backend `MeasurementType.kind`
// value. Only 'FFTSpectrum' is implemented today; new kinds get a new entry here plus a matching
// case in MeasurementKindIcon.vue, and immediately show up in the "new measurement" picker.
export interface MeasurementKindMeta {
  kind: string
  label: string
  description: string
}

/** Mirrors MeasurementSaveService.ThdVsFrequencyKind — the one kind whose data is sweep points rather than a spectrum. */
export const THD_VS_FREQUENCY_KIND = 'ThdVsFrequency'

export const MEASUREMENT_KINDS: MeasurementKindMeta[] = [
  {
    kind: 'FFTSpectrum',
    label: 'FFT analysis',
    description: 'Spectrum, THD, harmonics and noise floor from a single FFT capture.',
  },
  {
    kind: 'ThdVsFrequency',
    label: 'THD vs frequency',
    description: 'Steps a tone across the band and plots THD, D2–D5 and D6+ against frequency.',
  },
]
