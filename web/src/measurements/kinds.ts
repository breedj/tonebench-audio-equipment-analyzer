// The set of measurement kinds the app can create — one entry per backend `MeasurementType.kind`
// value. Only 'FFTSpectrum' is implemented today; new kinds get a new entry here plus a matching
// case in MeasurementKindIcon.vue, and immediately show up in the "new measurement" picker.
export interface MeasurementKindMeta {
  kind: string
  label: string
  description: string
}

export const MEASUREMENT_KINDS: MeasurementKindMeta[] = [
  {
    kind: 'FFTSpectrum',
    label: 'FFT analysis',
    description: 'Spectrum, THD, harmonics and noise floor from a single FFT capture.',
  },
]
