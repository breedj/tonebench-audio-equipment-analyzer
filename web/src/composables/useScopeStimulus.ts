import { createGeneratorStimulus, type GeneratorStimulusStore } from './useGeneratorStimulus'

/**
 * The oscilloscope screen's staged generator form — a second instance of the same store the FFT
 * screen uses, never the same one.
 *
 * <p><b>Why the split, rather than sharing one.</b> `MeasurementSaveService.ApplyAsync` answers a
 * kind in <code>HasNoLiveConfiguration</code> with the <em>live session's</em> generator, so a scope
 * panel restored from <code>loadedParams</code> would stage whatever the device happens to be set
 * to rather than what the measurement saved. The scope therefore restores from its own
 * <code>OscilloscopeParameters.Generator</code> (see scope/generator.ts), which means it needs
 * somewhere of its own to stage into: with one shared store, selecting a scope measurement would
 * overwrite the FFT screen's tone and vice versa, and neither screen's stimulus would survive a
 * trip to the other.</p>
 *
 * <p>Module level like every other store here, so a stimulus composed on this screen survives
 * navigating away from it — the same reason <code>useOscilloscope</code> is.</p>
 */
const scope = createGeneratorStimulus()

export function useScopeStimulus(): GeneratorStimulusStore {
  return scope
}
