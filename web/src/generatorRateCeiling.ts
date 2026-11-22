/**
 * Whether the bench's generator can play the rate the FFT screen is staged at, and what to say when
 * it cannot.
 *
 * <p>Two different refusals live here, and they are two because the generator can fail in two ways.
 * A **ceiling** is a hardware-protection rule: a QA403 captures at 384 kHz and emits garbage from
 * its DAC there — about 12 dB low at roughly 44 % distortion, with enough near-DC content to cycle
 * the device's own output protection relay, which a software stop does not clear — so
 * `DeviceSessionService.ConfigureGeneratorCoreAsync` refuses a *voiced* generator above
 * `MaxGeneratorSampleRateHz` and throws rather than clamping. A **ladder** miss is simply a device
 * that does not have that rate: a split whose generating half is a sound card offers whatever its
 * driver accepted, and asking it for one of the others is refused by
 * `RequireGeneratorRateUsable`.</p>
 *
 * <p>Both are said *before* Run, so the Generator panel stands down instead of the user composing a
 * stimulus and being handed a 409. Silence is always allowed, so neither blocks capturing at the
 * rate — which is the whole point of offering it.</p>
 *
 * <p><b>The advice stopped being "use an external generator" on 2026-11-14.</b> That was written
 * when a second device was something to go and find; a split analyzer makes it a dropdown, so the
 * notice names the lever this bench actually has — the split's own **Generator rate** where one is
 * already configured, and **Split (two devices)** where it is not.</p>
 *
 * <p><b>And it stopped offering to turn the generator off on 2026-11-22</b> (reported: "when it is
 * shown the generator cannot be turned off, the control is not shown"). It was exactly backwards:
 * this notice <em>replaces</em> the panel's body — `GeneratorPanel` hides everything that composes
 * a stimulus behind it — so the switch it named is not on screen, and the generator is silenced
 * already. What the reader needs instead is the one fact that was missing: the capture still runs.
 * So both messages now say the generator is silent and the capture goes ahead without it, and then
 * name a lever that exists.</p>
 *
 * Its own module, like {@link stimulusFloorNotice} next door, because nothing in a `.vue` file is
 * testable under the node environment vitest runs in.
 */
export interface GeneratorRateBench {
  /** The rate the FFT screen is staged at, which the generator follows unless one is pinned. */
  stagedSampleRateHz: number
  /**
   * The rate the generating half is pinned to, or 0 where it follows the capture rate.
   *
   * <p>Pinned, the staged rate cannot make the generator fail and there is nothing to warn about —
   * which is the whole answer for a QA403 capturing at 384 kHz while a DAC plays at 192.</p>
   */
  generatorRateOverrideHz?: number | null
  /** The rates the generating half can play. Empty or absent means "not known", and warns nothing. */
  generatorRatesHz?: readonly number[] | null
  /** Its ceiling. Null is a device with no ceiling; infinity is an older server not sending it. */
  maxGeneratorRateHz?: number | null
  /** Whether the bench is a split, which decides which lever the notice names. */
  isSplit?: boolean
}

export function generatorRateCeilingNotice(bench: GeneratorRateBench): string | null {
  const { stagedSampleRateHz } = bench
  if (!Number.isFinite(stagedSampleRateHz)) return null

  // A pinned generator plays its own rate whatever the capture is staged at. The server validates
  // that rate when it is set; nothing the FFT screen does can break it.
  const pinned = bench.generatorRateOverrideHz
  if (typeof pinned === 'number' && Number.isFinite(pinned) && pinned > 0) return null

  const ceiling = bench.maxGeneratorRateHz
  const hasCeiling = typeof ceiling === 'number' && Number.isFinite(ceiling)
  if (hasCeiling && stagedSampleRateHz > ceiling) {
    return (
      `This analyzer's generator cannot be used at ${format(stagedSampleRateHz)} kHz — it is an ` +
      `input-only rate. It stays silent and the capture runs without it. ${wayOut(bench, 'play')}`
    )
  }

  // A rate the generating half simply does not have. Only checked against a list that exists: a
  // device that reports none is one this notice knows nothing about.
  const rates = bench.generatorRatesHz
  if (rates && rates.length > 0 && !rates.some((r) => Math.abs(r - stagedSampleRateHz) < 0.5)) {
    return (
      `This bench's generator cannot run at ${format(stagedSampleRateHz)} kHz. It accepts ` +
      `${rates.map(format).join(', ')} kHz, and stays silent at this one — the capture runs ` +
      `without it. ${wayOut(bench, 'run at')}`
    )
  }

  return null
}

/**
 * The lever, which is the half that used to be wrong — twice.
 *
 * <p>On a split the generating half is already chosen and its rate is a control in the split
 * settings, so the notice names it. On a single analyzer the second device is the thing that does
 * not exist yet, so it names the analyzer picker instead of sending the reader off to find a signal
 * generator — which is what "use an external generator" did, and which stopped being the only
 * answer the day a split could be selected.</p>
 *
 * <p>Both arms name a control the reader can <b>see from here</b>, which is what the old "or turn
 * the generator off" could not: that switch is inside the panel this notice has replaced. The
 * capture rate is the other real lever and is named first, since on a QA403 at 384 kHz dropping
 * back to 192 is usually what was meant.</p>
 */
function wayOut(bench: GeneratorRateBench, verb: string): string {
  return bench.isSplit
    ? `Capture at a rate the generator can ${verb}, or set Generator rate in the split settings.`
    : `Capture at a rate it can ${verb}, or select Split (two devices) as the analyzer to drive ` +
        'the generator from a second device.'
}

function format(hz: number): string {
  return Number((hz / 1000).toFixed(3)).toString()
}
