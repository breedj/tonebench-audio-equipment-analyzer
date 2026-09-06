// The set of measurement kinds the app can create — one entry per backend `MeasurementType.kind`
// value. New kinds get a new entry here plus a matching case in MeasurementKindIcon.vue, and
// immediately show up in the "new measurement" picker.
export interface MeasurementKindMeta {
  kind: string
  label: string
  description: string
}

/** Mirrors MeasurementSaveService.ThdVsFrequencyKind — a sweep of frequencies at one drive level. */
export const THD_VS_FREQUENCY_KIND = 'ThdVsFrequency'

/** Mirrors MeasurementSaveService.ThdVsAmplitudeKind — a sweep of drive levels at one frequency. */
export const THD_VS_AMPLITUDE_KIND = 'ThdVsAmplitude'

/** Mirrors MeasurementSaveService.FrequencyResponseKind — a chirp-driven frequency response. */
export const FREQUENCY_RESPONSE_KIND = 'FrequencyResponse'

/** Mirrors MeasurementSaveService.SteppedResponseKind — a stepped-sine Bode plot. */
export const STEPPED_RESPONSE_KIND = 'SteppedResponse'

/** Mirrors MeasurementSaveService.ImpulseResponseKind — a chirp-driven impulse response. */
export const IMPULSE_RESPONSE_KIND = 'ImpulseResponse'

/** The kind name a single-FFT-capture result carries — used to scope the overlay budget and colors that are specific to the FFT Spectrum chart (see spectrumOverlayColors.ts). */
export const FFT_SPECTRUM_KIND = 'FFTSpectrum'

/**
 * Whether a kind's data is a run of sweep points rather than one spectrum. Mirrors
 * `MeasurementSaveService.IsSweepKind`, and is what every branch outside the two sweep screens
 * actually cares about: sweep results save through a different endpoint, load through
 * `getStoredSweep`, and draw a dashed run marker in the history checklist.
 */
export function isSweepKind(kind: string | null | undefined): boolean {
  return kind === THD_VS_FREQUENCY_KIND || kind === THD_VS_AMPLITUDE_KIND
}

/**
 * Whether a kind's data is a set of frequency-response curves. Its own predicate rather than a third
 * arm of `isSweepKind`, mirroring `MeasurementSaveService.IsFrequencyResponseKind`: a response
 * result saves and loads through its own endpoints, and a kind that answered true to "is a sweep"
 * would be fetched as sweep points and come back empty.
 *
 * Both response kinds answer true, and that is the point of the predicate rather than an oversight:
 * a chirp and a stepped sine are acquired completely differently but produce the same curves, are
 * stored in the same rows, and are read back, exported and overlaid by exactly the same code. Only
 * the config panel and the start endpoint have to tell them apart — `isSteppedResponseKind` below.
 */
export function isFrequencyResponseKind(kind: string | null | undefined): boolean {
  return kind === FREQUENCY_RESPONSE_KIND || kind === STEPPED_RESPONSE_KIND
}

/** Whether this response is stepped rather than chirp-driven — which config panel to mount, and which endpoints to drive. */
export function isSteppedResponseKind(kind: string | null | undefined): boolean {
  return kind === STEPPED_RESPONSE_KIND
}

/**
 * Whether a kind's result is an impulse response. Mirrors
 * `MeasurementSaveService.IsImpulseResponseKind`, and deliberately <b>not</b> a third arm of
 * `isFrequencyResponseKind` even though an impulse-response result does carry the same gated
 * magnitude and phase curves. That predicate decides which start endpoint the UI drives and which
 * reader a stored result loads through, and an impulse response has more to read back than curves —
 * the impulse itself and the harmonic curves — so a kind answering true to it would load half a
 * measurement with no sign of the rest.
 */
export function isImpulseResponseKind(kind: string | null | undefined): boolean {
  return kind === IMPULSE_RESPONSE_KIND
}

export const MEASUREMENT_KINDS: MeasurementKindMeta[] = [
  {
    kind: 'FFTSpectrum',
    label: 'FFT analysis',
    description: 'Spectrum, THD, harmonics and noise floor from a single FFT capture.',
  },
  {
    kind: THD_VS_FREQUENCY_KIND,
    label: 'THD vs frequency',
    description: 'Steps a tone across the band and plots THD, D2–D5 and D6+ against frequency.',
  },
  {
    kind: THD_VS_AMPLITUDE_KIND,
    label: 'THD vs amplitude',
    description:
      'Steps the generator level at one fixed frequency and plots THD, D2–D5 and D6+ against output voltage, output power or input voltage.',
  },
  {
    kind: FREQUENCY_RESPONSE_KIND,
    label: 'Frequency response',
    description:
      'Sweeps a chirp across the band and plots gain or level against frequency, with −1 dB and −3 dB bandwidth markers.',
  },
  {
    kind: IMPULSE_RESPONSE_KIND,
    label: 'Impulse response',
    description:
      'Sweeps a chirp and deconvolves it to the impulse response, then reads a gated (reflection-free) magnitude and phase off it, plus harmonic distortion against frequency.',
  },
  {
    kind: STEPPED_RESPONSE_KIND,
    label: 'Stepped sine response',
    description:
      'Steps one tone at a time across the band and plots the same level, gain and phase curves — slower than the chirp, but nothing else is playing while each point is measured.',
  },
]
