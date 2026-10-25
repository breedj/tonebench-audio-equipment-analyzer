/**
 * <p>Whether this browser can draw the GPU decay surface, and the only thing that decides it.</p>
 *
 * <p><b>This is a correctness gate, not an optimisation.</b> Without WebGPU the ProEssentials 3D
 * surface does not fail and does not come up blank — it draws a plausible lit surface that is
 * <em>not the data</em>, silently. Measured 2026-10-25 against proessentials 11.0.1 with
 * <c>navigator.gpu</c> hidden: two charts holding genuinely different datasets (60x400 with a
 * height range of -31.6…+4.6, and 241x200 with -10499…+503) drew canvases that were
 * <b>91.2 % identical pixel for pixel</b> — the engine's own placeholder mesh, with no height
 * colouring, no axes and no grid, and nothing reported anywhere.</p>
 *
 * <p>So a bench that cannot run this must be given the Canvas 2D chart instead, and the test has
 * to be the runtime one. <c>PeControl.FEATURES.gpuLayer</c> is <em>not</em> it: that read
 * <c>1</c> in the same run, because it describes the build rather than the machine.</p>
 */

/** Hoisted so the probe runs once per page rather than once per chart. */
let probe: Promise<boolean> | null = null

/**
 * Resolves true only when a WebGPU adapter is actually obtainable.
 *
 * <p>Both halves are needed. <c>navigator.gpu</c> exists on a Chrome whose driver or GPU is not
 * on the supported list — which is the ordinary case on Linux, where the surface is gated by
 * hardware and driver rather than by the browser version — and there
 * <c>requestAdapter()</c> resolves <c>null</c>.</p>
 */
export function decaySurfaceGpuAvailable(): Promise<boolean> {
  probe ??= detect()
  return probe
}

async function detect(): Promise<boolean> {
  const gpu = typeof navigator === 'undefined' ? undefined : navigator.gpu
  if (!gpu) return false
  try {
    return (await gpu.requestAdapter()) != null
  } catch {
    // A browser that throws out of requestAdapter is one we are not going to draw on either.
    return false
  }
}

/**
 * The synchronous half, for a caller that cannot await — false here is final, true only means the
 * adapter is worth asking for. Nothing selects a chart on this alone.
 */
export function hasWebGpuObject(nav: { gpu?: unknown } | undefined = typeof navigator === 'undefined' ? undefined : navigator): boolean {
  return !!nav && nav.gpu != null
}

/** Test seam: forget the cached answer. */
export function resetGpuProbe(): void {
  probe = null
}
