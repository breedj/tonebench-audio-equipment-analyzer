/**
 * The highest sample rate the connected analyzer can *play* a stimulus at, and what to say when the
 * FFT screen is staged above it.
 *
 * A QA403 captures at 384 kHz and emits garbage from its DAC there — about 12 dB low at roughly
 * 44 % distortion, with enough near-DC content to cycle the device's own output protection relay,
 * which a software stop does not clear. So `DeviceSessionService.ConfigureGeneratorCoreAsync`
 * refuses a *voiced* generator above `MaxGeneratorSampleRateHz` and throws rather than clamping.
 * That refusal is hardware protection and stays; this is the same limit said *before* Run, so the
 * Generator panel stands down instead of the user composing a stimulus and being handed a 409.
 *
 * Silence is always allowed, so nothing here blocks capturing at the rate — which is the whole
 * point of offering it.
 *
 * Its own module, like {@link stimulusFloorNotice} next door, because nothing in a `.vue` file is
 * testable under the node environment vitest runs in.
 */
export function generatorRateCeilingNotice(
  sampleRateHz: number,
  maxGeneratorRateHz: number | null | undefined,
): string | null {
  if (!Number.isFinite(sampleRateHz)) return null
  // Null is a device with no ceiling (a sound card); infinity is the same thing arriving from an
  // older server that did not send the field at all, and must not be compared against.
  if (maxGeneratorRateHz == null || !Number.isFinite(maxGeneratorRateHz)) return null
  if (sampleRateHz <= maxGeneratorRateHz) return null

  return `This analyzer's generator cannot be used at ${format(sampleRateHz)} kHz — it is an input-only rate. Use an external generator.`
}

function format(hz: number): string {
  return Number((hz / 1000).toFixed(3)).toString()
}
