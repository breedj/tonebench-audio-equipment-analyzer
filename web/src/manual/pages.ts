// The manual's table of contents. One entry per page; `kind` is what wires a measurement screen's
// own help button to the right page (see manualPageForKind), so a new measurement kind needs a
// page here as well as an entry in measurements/kinds.ts.
//
// Page bodies are plain-markup Vue components under ./pages, loaded lazily by ManualDialog so the
// manual costs nothing until it is opened.
import type { Component } from 'vue'
import { defineAsyncComponent } from 'vue'
import {
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  STEPPED_RESPONSE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from '../measurements/kinds'

export interface ManualPage {
  id: string
  title: string
  /** One line under the title in the index. */
  summary: string
  /** Which measurement kind's help button lands here, if any. */
  kind?: string
  /** Grouping header in the index sidebar. */
  group: 'Basics' | 'Measurements' | 'Reference'
  component: Component
}

export const MANUAL_PAGES: ManualPage[] = [
  {
    id: 'getting-started',
    title: 'Getting started',
    summary: 'The device, projects, measurements, and the Run/Stop model.',
    group: 'Basics',
    component: defineAsyncComponent(() => import('./pages/GettingStartedPage.vue')),
  },
  {
    id: 'charts',
    title: 'Reading the charts',
    summary: 'Cursors, zooming, autofit, colours and the off-scale markers.',
    group: 'Basics',
    component: defineAsyncComponent(() => import('./pages/ChartsPage.vue')),
  },
  {
    id: 'fft-spectrum',
    title: 'FFT analysis',
    summary: 'Spectrum, THD, harmonics and noise floor from a single FFT capture.',
    kind: FFT_SPECTRUM_KIND,
    group: 'Measurements',
    component: defineAsyncComponent(() => import('./pages/FftSpectrumPage.vue')),
  },
  {
    id: 'thd-vs-frequency',
    title: 'THD vs frequency',
    summary: 'Distortion against frequency at one drive level.',
    kind: THD_VS_FREQUENCY_KIND,
    group: 'Measurements',
    component: defineAsyncComponent(() => import('./pages/ThdVsFrequencyPage.vue')),
  },
  {
    id: 'thd-vs-amplitude',
    title: 'THD vs amplitude',
    summary: 'Distortion against level at one frequency — the clipping knee.',
    kind: THD_VS_AMPLITUDE_KIND,
    group: 'Measurements',
    component: defineAsyncComponent(() => import('./pages/ThdVsAmplitudePage.vue')),
  },
  {
    id: 'frequency-response',
    title: 'Frequency response',
    summary: 'A chirp sweep: gain, phase, coherence, bandwidth, Nyquist and Nichols.',
    kind: FREQUENCY_RESPONSE_KIND,
    group: 'Measurements',
    component: defineAsyncComponent(() => import('./pages/FrequencyResponsePage.vue')),
  },
  {
    id: 'stepped-response',
    title: 'Stepped sine response',
    summary: 'The same curves, one tone at a time — slow, quiet and literal.',
    kind: STEPPED_RESPONSE_KIND,
    group: 'Measurements',
    component: defineAsyncComponent(() => import('./pages/SteppedResponsePage.vue')),
  },
  {
    id: 'saving',
    title: 'Projects, saving and history',
    summary: 'What gets stored where, overlays, CSV export and deleting.',
    group: 'Reference',
    component: defineAsyncComponent(() => import('./pages/SavingPage.vue')),
  },
  {
    id: 'settings',
    title: 'Configuration and status bar',
    summary: 'Global settings, attenuation, and what the warnings mean.',
    group: 'Reference',
    component: defineAsyncComponent(() => import('./pages/SettingsPage.vue')),
  },
]

export const MANUAL_GROUPS: ManualPage['group'][] = ['Basics', 'Measurements', 'Reference']

export function manualPageById(id: string | null): ManualPage | null {
  if (id === null) return null
  return MANUAL_PAGES.find((p) => p.id === id) ?? null
}

/** The help page a measurement screen's own "?" button opens. Null for a kind with no page yet — the caller falls back to the index. */
export function manualPageForKind(kind: string | null | undefined): ManualPage | null {
  if (!kind) return null
  return MANUAL_PAGES.find((p) => p.kind === kind) ?? null
}
