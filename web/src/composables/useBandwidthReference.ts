import { ref } from 'vue'
import type { ResponseReferenceModeName } from '../api/client'

/**
 * What the ±1 dB and −3 dB corners of a frequency response are measured down from: the reference
 * mode and, for the passband median, the band it is taken over.
 *
 * Module-level singleton like useLoadOhms, and for the same reason: the values are edited in one
 * place — the Measurements panel, next to the corners they define — but belong to the response
 * config panels, which save them with the measurement type and send them with every run. The
 * chirp and the stepped panel share them too: the two measurements are read off one Measurements
 * table, so a reference that meant different things on each would make them disagree about a DUT
 * neither measured differently.
 *
 * "Passband median" is the default because the peak is only the right reference when the DUT's
 * response *is* a peak. An output transformer's leakage inductance resonating against its winding
 * capacitance sits above the audio band and is routinely several dB above the passband, so a peak
 * reference puts the −1 dB threshold above the whole passband and both corners land on the
 * resonance's own skirts — a correct measurement of the resonance that says nothing about the
 * amplifier. Pick "Peak" for a bandpass filter, a tweeter, or anything else whose response is one
 * hump.
 */
const bandwidthReference = ref<ResponseReferenceModeName>('BandMedian')
const bandwidthReferenceLowHz = ref(200)
const bandwidthReferenceHighHz = ref(2000)

export function useBandwidthReference() {
  return { bandwidthReference, bandwidthReferenceLowHz, bandwidthReferenceHighHz }
}
