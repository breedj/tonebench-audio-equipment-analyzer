import { describe, expect, it } from 'vitest'
import {
  claimLive,
  foreignLiveStores,
  liveOwnerKey,
  liveScreenStore,
  liveStoreForKind,
  releaseLive,
  releaseOwners,
  type LiveOwners,
} from './liveOwner'
import { MEASUREMENT_KINDS } from './kinds'

// Two measurements of one project. Non-null by construction — both ids are given.
const A = liveOwnerKey(1, 4)!
const B = liveOwnerKey(1, 5)!

describe('liveOwnerKey', () => {
  it('carries the project, so the first measurement of two projects are two owners', () => {
    expect(liveOwnerKey(1, 1)).not.toBe(liveOwnerKey(2, 1))
  })

  it('is null when nothing is open', () => {
    expect(liveOwnerKey(null, 1)).toBeNull()
    expect(liveOwnerKey(1, null)).toBeNull()
  })
})

describe('liveStoreForKind', () => {
  it('gives every kind with a runner of its own a store', () => {
    // FFT analysis is the one kind whose result *is* the live display, so it has no second store.
    const withoutStore = MEASUREMENT_KINDS.filter((k) => liveStoreForKind(k.kind) === null).map((k) => k.kind)
    expect(withoutStore).toEqual(['FFTSpectrum'])
  })

  it('shares a store only between the two kinds that share a runner', () => {
    const sweep = MEASUREMENT_KINDS.filter((k) => liveStoreForKind(k.kind) === 'sweep').map((k) => k.kind)
    expect(sweep).toEqual(['ThdVsFrequency', 'ThdVsAmplitude'])
  })
})

describe('claimLive', () => {
  it('takes the live display as well as the run its own store', () => {
    expect(claimLive({}, 'stepped', A)).toEqual({ fft: A, stepped: A })
  })

  it('takes the live display alone for a kind with no runner of its own', () => {
    expect(claimLive({}, null, A)).toEqual({ fft: A })
  })

  it('leaves the map alone when no measurement is open', () => {
    const owners: LiveOwners = { fft: A }
    expect(claimLive(owners, 'sweep', null)).toBe(owners)
  })
})

describe('liveScreenStore', () => {
  it('sends the FFT analysis screen to the shared live display', () => {
    expect(liveScreenStore('FFTSpectrum')).toBe('fft')
  })

  it('sends every other screen to its own runner', () => {
    expect(liveScreenStore('SteppedResponse')).toBe('stepped')
    expect(liveScreenStore('Impedance')).toBe('impedance')
  })
})

// The candidate list a switch actually passes: the one store the screen being switched *to* draws
// from. See discardForeignLiveState in App.vue.
const arrivingAt = (kind: string) => [liveScreenStore(kind)]

describe('foreignLiveStores', () => {
  it('reports the live display when another measurement filled it', () => {
    // The reported bug: a stepped sweep's last record on an FFT measurement's screen.
    const owners = claimLive({}, 'stepped', A)
    expect(foreignLiveStores(owners, B, arrivingAt('FFTSpectrum'))).toEqual(['fft'])
  })

  it('leaves the screen of the measurement that filled it alone', () => {
    const owners = claimLive({}, 'stepped', A)
    expect(foreignLiveStores(owners, A, arrivingAt('SteppedResponse'))).toEqual([])
  })

  it('reports a kind store filled by a different measurement of the same kind', () => {
    const owners = claimLive({}, 'stepped', A)
    expect(foreignLiveStores(owners, B, arrivingAt('SteppedResponse'))).toEqual(['stepped'])
  })

  it('keeps an FFT capture across a visit to a screen that cannot show it', () => {
    // Capture on A, glance at stepped measurement B, come back. B writes the live display only by
    // running, and it did not run — so A's capture must survive both switches.
    const owners = claimLive({}, null, A)
    expect(foreignLiveStores(owners, B, arrivingAt('SteppedResponse'))).toEqual([])
    expect(foreignLiveStores(owners, A, arrivingAt('FFTSpectrum'))).toEqual([])
  })

  it('keeps curves a measurement took itself when the live display was taken by another', () => {
    // Sweep on A, then an FFT capture on B, then back to A: the stepped curves are still A's own.
    const owners = claimLive(claimLive({}, 'stepped', A), null, B)
    expect(foreignLiveStores(owners, A, arrivingAt('SteppedResponse'))).toEqual([])
    expect(foreignLiveStores(owners, A, arrivingAt('FFTSpectrum'))).toEqual(['fft'])
  })

  it('never reports a store nothing has filled, so a reload still re-attaches', () => {
    expect(foreignLiveStores({}, A, ['fft', 'stepped'])).toEqual([])
  })

  it('reports a store once even when the same one is asked for twice', () => {
    const owners = claimLive({}, 'chirp', A)
    expect(foreignLiveStores(owners, B, ['fft', 'fft'])).toEqual(['fft'])
  })
})

describe('releaseOwners', () => {
  it('forgets every store a destroyed measurement held', () => {
    // A permanent delete from the trash: A is gone, so nothing may go on recording that it owns
    // anything -- in the client's map or, through api.forgetRunOwners, on the server.
    const owners = claimLive(claimLive({}, 'chirp', A), 'sweep', B)

    expect(releaseOwners(owners, [A])).toEqual({ fft: B, sweep: B })
  })

  it('leaves the stores of the measurements that still exist', () => {
    const owners = claimLive({}, 'impedance', B)

    expect(releaseOwners(owners, [A])).toEqual({ fft: B, impedance: B })
  })

  it('is by owner, not by store -- a destroyed measurement may be in any of them or in none', () => {
    expect(releaseOwners({}, [A, B])).toEqual({})
  })
})

describe('releaseLive', () => {
  it('forgets exactly what was discarded', () => {
    const owners = claimLive(claimLive({}, 'stepped', A), null, B)
    expect(releaseLive(owners, ['fft'])).toEqual({ stepped: A })
  })

  it('is the same map when nothing was discarded', () => {
    const owners = claimLive({}, 'sweep', A)
    expect(releaseLive(owners, [])).toBe(owners)
  })
})
