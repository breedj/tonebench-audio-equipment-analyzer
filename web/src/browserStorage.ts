/**
 * Per-viewer preferences kept in `localStorage` — the theme, the manual's edition, whether the
 * update check is allowed to phone GitHub. None of them is measurement data; each is a convenience
 * that may legitimately come back empty.
 *
 * <p>Two reasons this is a module rather than five bare `localStorage` calls. The keys were prefixed
 * `qa403.` before the app was renamed to Tonebench, and renaming them outright would have reset
 * every one of these on the update that did it — a bench that had chosen the light theme and
 * dismissed a version banner would get the dark theme and the banner back, with nothing saying why.
 * {@link readStoredPreference} therefore falls back to the old key once and carries the value
 * forward. And `localStorage` is not always there: in a private window, or with site data blocked,
 * the accessor itself throws rather than returning null, so every call here is wrapped. A
 * preference that cannot be read is simply the default.</p>
 */

const PREFIX = 'tonebench.'

/** The prefix these keys carried while the app was named after the QA403. */
const LEGACY_PREFIX = 'qa403.'

/**
 * The stored value, or null for none. A key with no value under its current name is looked up once
 * under its pre-rename name; a value found there is rewritten under the new key and the old one is
 * dropped, so the fallback costs one lookup on the first read and nothing afterwards.
 */
export function readStoredPreference(key: string): string | null {
  try {
    const current = localStorage.getItem(key)
    if (current !== null) return current
    if (!key.startsWith(PREFIX)) return null

    const legacyKey = LEGACY_PREFIX + key.slice(PREFIX.length)
    const legacy = localStorage.getItem(legacyKey)
    if (legacy === null) return null

    localStorage.setItem(key, legacy)
    localStorage.removeItem(legacyKey)
    return legacy
  } catch {
    // Storage that cannot be read holds no preference, which is what a fresh install looks like.
    return null
  }
}

/** Stores a preference, silently doing nothing where storage is unavailable. */
export function writeStoredPreference(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // A preference that cannot be stored is not worth an error on screen.
  }
}
