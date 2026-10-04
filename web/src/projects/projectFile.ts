/**
 * Path arithmetic for project files, done client-side on strings the server handed us: the
 * server says which separator it uses (FileBrowseResponse.separator), and everything here takes
 * it as a parameter rather than guessing from the platform the browser runs on — the browser and
 * the server are the same machine today, but nothing in the API requires that.
 */

/** One project per file — mirrors ProjectPaths.Extension on the server. What new files are written with. */
export const PROJECT_EXTENSION = '.tbench'

/** What project files were called while the app was named after the QA403 — ProjectPaths.LegacyExtension. */
export const LEGACY_PROJECT_EXTENSION = '.qa403'

/**
 * Listed by the open dialog and left alone by {@link ensureExtension} — ProjectPaths.OpenableExtensions.
 * Opening does not consult it: the server validates a project file by its contents, so this only
 * decides what the dialog shows and what Save As declines to rename.
 */
export const OPENABLE_PROJECT_EXTENSIONS = [PROJECT_EXTENSION, LEGACY_PROJECT_EXTENSION]

/**
 * A project name as a file name, valid on every platform at once — the same rule as
 * ProjectPaths.SanitizeFileName: the fixed Windows set of forbidden characters, control characters
 * dropped, whitespace runs collapsed, leading/trailing dots and spaces stripped, "Project" for nothing.
 */
export function sanitizeFileName(name: string): string {
  const forbidden = '<>:"/\\|?*'
  let out = ''
  let pendingSpace = false
  for (const c of name) {
    if (/\s/.test(c)) {
      pendingSpace = out.length > 0
      continue
    }
    if (forbidden.includes(c) || c.charCodeAt(0) < 0x20 || c.charCodeAt(0) === 0x7f) continue
    if (pendingSpace) {
      out += ' '
      pendingSpace = false
    }
    out += c
  }
  out = out.replace(/^[ .]+|[ .]+$/g, '')
  return out.length === 0 ? 'Project' : out
}

export function ensureExtension(path: string): string {
  const lower = path.toLowerCase()
  // The legacy extension counts, or saving an existing amp.qa403 writes amp.qa403.tbench.
  return OPENABLE_PROJECT_EXTENSIONS.some((e) => lower.endsWith(e)) ? path : path + PROJECT_EXTENSION
}

/** `folder` + `name` with exactly one separator between them, whatever the folder ends in. */
export function joinPath(folder: string, name: string, separator: string): string {
  if (folder.length === 0) return name
  const trimmed = folder.endsWith(separator) || folder.endsWith('/') ? folder : folder + separator
  return trimmed + name
}

/** The last path segment, whichever separator was used. */
export function fileNameOf(path: string): string {
  const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'))
  return i < 0 ? path : path.slice(i + 1)
}

/** Everything before the last segment, or '' when there is none. A drive root keeps its separator. */
export function parentPath(path: string): string {
  const i = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'))
  if (i < 0) return ''
  const parent = path.slice(0, i)
  // "C:" alone is the drive's current directory, not its root, so keep "C:\"; likewise "/" stays "/".
  if (parent.length === 0 || /^[A-Za-z]:$/.test(parent)) return path.slice(0, i + 1)
  return parent
}

/** Absolute on either platform: a drive letter, a UNC path, a POSIX root, or `~`. */
export function isRooted(path: string): boolean {
  return /^([A-Za-z]:[\\/]|\\\\|\/|~([\\/]|$))/.test(path)
}

/** The file name a project called `name` gets under `folder`. */
export function suggestLocation(name: string, folder: string, separator: string): string {
  return joinPath(folder, sanitizeFileName(name) + PROJECT_EXTENSION, separator)
}
