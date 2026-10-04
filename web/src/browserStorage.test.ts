import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { readStoredPreference, writeStoredPreference } from './browserStorage'

/**
 * The fallback exists so the rename from QA403 Audio Analyzer to Tonebench did not reset everyone's
 * theme, manual edition and dismissed update banner. It runs once per key and then gets out of the
 * way.
 *
 * <p>vitest runs in the node environment here, where there is no `localStorage` at all — which is
 * both why the bare calls this module replaced were untestable, and why the store below is a stub
 * installed on `globalThis` rather than a jsdom window.</p>
 */

function installStore(): Map<string, string> {
  const entries = new Map<string, string>()
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: (k: string) => entries.get(k) ?? null,
      setItem: (k: string, v: string) => void entries.set(k, v),
      removeItem: (k: string) => void entries.delete(k),
    },
  })
  return entries
}

function installThrowingStore(): void {
  const deny = () => {
    throw new Error('site data blocked')
  }
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: { getItem: deny, setItem: deny, removeItem: deny },
  })
}

describe('readStoredPreference', () => {
  let store: Map<string, string>

  beforeEach(() => {
    store = installStore()
  })

  afterEach(() => {
    Reflect.deleteProperty(globalThis, 'localStorage')
  })

  it('reads the current key', () => {
    store.set('tonebench.themeMode', 'Light')
    expect(readStoredPreference('tonebench.themeMode')).toBe('Light')
  })

  it('falls back to the pre-rename key, and carries the value onto the new one', () => {
    store.set('qa403.themeMode', 'Light')

    expect(readStoredPreference('tonebench.themeMode')).toBe('Light')

    // Carried forward, so the next read needs no fallback and the old key stops lingering.
    expect(store.get('tonebench.themeMode')).toBe('Light')
    expect(store.has('qa403.themeMode')).toBe(false)
  })

  it('prefers the current key when both exist', () => {
    store.set('qa403.themeMode', 'Light')
    store.set('tonebench.themeMode', 'Dark')

    expect(readStoredPreference('tonebench.themeMode')).toBe('Dark')
    // The stale one is left alone rather than deleted: nothing read it, so nothing may remove it.
    expect(store.get('qa403.themeMode')).toBe('Light')
  })

  it('returns null when neither key is set', () => {
    expect(readStoredPreference('tonebench.themeMode')).toBeNull()
  })

  it('does not look for a legacy twin of a key that is not ours', () => {
    store.set('qa403.themeMode', 'Light')
    expect(readStoredPreference('themeMode')).toBeNull()
  })

  /**
   * In a private window, or with site data blocked, the accessor throws rather than returning null.
   * An unreadable preference is the default, never an error on screen — and never a throw out of a
   * composable's module-level initialiser, which would blank the whole app.
   */
  it('survives storage that throws', () => {
    installThrowingStore()

    expect(readStoredPreference('tonebench.themeMode')).toBeNull()
    expect(() => writeStoredPreference('tonebench.themeMode', 'Dark')).not.toThrow()
  })
})

describe('writeStoredPreference', () => {
  it('stores under the key it was given', () => {
    const store = installStore()
    writeStoredPreference('tonebench.manual.edition', 'guide')
    expect(store.get('tonebench.manual.edition')).toBe('guide')
    Reflect.deleteProperty(globalThis, 'localStorage')
  })
})
