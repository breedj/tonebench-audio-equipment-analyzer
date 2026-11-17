import type { GeneratorChannelRequest, OscilloscopeParameters, StoredGeneratorConfig } from '../api/client'

/**
 * The oscilloscope measurement's own staged stimulus, in and out of its `params_json`.
 *
 * <p><b>Why this exists at all.</b> Every other screen restores its generator from
 * <code>loadedParams</code> — the applied measurement type — but `MeasurementSaveService.ApplyAsync`
 * answers a kind in <code>HasNoLiveConfiguration</code> with the <em>live session's</em> generator
 * config, not the stored one. On the FFT screen that is right, because the session is what that
 * screen just configured. On this one it would mean arriving at a measurement and being shown
 * whatever the device happens to be set to. So the scope reads its stimulus out of its own stored
 * parameters, which is the only place that holds what <em>this</em> measurement plays.</p>
 */

/** What a measurement stores: both channels and the snap flag, exactly as the server writes it. */
export function scopeGeneratorFromParams(paramsJson: string | null | undefined): StoredGeneratorConfig | null {
  if (!paramsJson) return null

  let stored: Partial<OscilloscopeParameters>
  try {
    stored = JSON.parse(paramsJson) as Partial<OscilloscopeParameters>
  } catch {
    return null
  }

  const generator = stored?.generator
  // Both channels or neither: GeneratorPanel reads the pair, and half a restore would stage one
  // channel's enable against the other's default.
  if (!generator || typeof generator !== 'object') return null
  if (!generator.left || !generator.right) return null
  return generator
}

/**
 * The generator half of an oscilloscope type's `params_json`, as the start request's own channels.
 *
 * <p>The request shape rather than the device's effective config, because that is what this screen
 * has: a channel carries the block for the kind it is playing and no other, which is what the
 * server's own channel mapping fills in defaults for. Restoring it is lossless for the selected
 * kind — <code>GeneratorChannelPanel</code>'s restore leaves a block it is not given alone — and
 * the kinds that were not selected keep this build's defaults, which is what a measurement that
 * never played them should say.</p>
 */
export function scopeGeneratorToParams(
  generator: { left: GeneratorChannelRequest; right: GeneratorChannelRequest; snapToBin: boolean } | null | undefined,
  stored: StoredGeneratorConfig | null = null,
): Partial<OscilloscopeParameters> {
  // Nothing composed means the panel is not mounted to ask -- a save from a screen this measurement
  // is not open on. Writing nothing there would drop the stimulus it already had, so what it had is
  // written back.
  if (!generator) return stored ? { generator: stored } : {}
  return {
    generator: {
      left: generator.left,
      right: generator.right,
      snapToBin: generator.snapToBin,
    } as StoredGeneratorConfig,
  }
}
