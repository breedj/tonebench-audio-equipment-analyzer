import type { Project } from '../api/client'
import { ensureExtension, isRooted, suggestLocation } from './projectFile'

/**
 * The one project dialog serves three menu items, and this is what differs between them: what the
 * form starts with and which fields it shows. Kept out of ProjectDialog.vue so it can be tested —
 * nothing in a .vue file is, under the node environment vitest runs in.
 */
export type ProjectDialogMode = 'new' | 'saveAs' | 'properties'

export interface ProjectForm {
  name: string
  notes: string
  /** Absolute path of the file to create; unused in `properties` mode. */
  location: string
  /** Save As only: copy the whole file (results included) rather than the measurement types alone. */
  includeResults: boolean
}

export function dialogTitle(mode: ProjectDialogMode): string {
  switch (mode) {
    case 'new':
      return 'New project'
    case 'saveAs':
      return 'Save project as…'
    case 'properties':
      return 'Project properties'
  }
}

export function confirmLabel(mode: ProjectDialogMode): string {
  switch (mode) {
    case 'new':
      return 'Create'
    case 'saveAs':
      return 'Save'
    case 'properties':
      return 'Save'
  }
}

export function showsLocation(mode: ProjectDialogMode): boolean {
  return mode !== 'properties'
}

export function showsIncludeResults(mode: ProjectDialogMode): boolean {
  return mode === 'saveAs'
}

/**
 * What the form opens with. New: empty, with the location following the name (see
 * `locationFor`). Save As: the open project's name and description, an empty location — the user
 * chooses where the copy goes — and results included. Properties: the open project's name and
 * description, nothing else.
 */
export function initialForm(mode: ProjectDialogMode, current: Project | null): ProjectForm {
  const fromCurrent = mode !== 'new' && current !== null
  return {
    name: fromCurrent ? current.name : '',
    notes: fromCurrent ? (current.notes ?? '') : '',
    location: '',
    includeResults: true,
  }
}

/**
 * The location the form suggests while the user has not typed one: the default folder plus the
 * sanitized name. Returns null (leave the field alone) once the user has edited the location, and
 * always for Save As, whose location starts empty on purpose: the suggestion would be the open
 * project's own file, which the server refuses, and the user is choosing where the copy goes.
 */
export function locationFor(
  mode: ProjectDialogMode, name: string, defaultFolder: string, separator: string, locationTouched: boolean,
): string | null {
  if (locationTouched || mode === 'saveAs') return null
  if (name.trim().length === 0) return ''
  return suggestLocation(name.trim(), defaultFolder, separator)
}

/** The first thing wrong with the form, or null when it can be submitted. */
export function validateForm(mode: ProjectDialogMode, form: ProjectForm): string | null {
  if (form.name.trim().length === 0) return 'The project needs a name.'
  if (!showsLocation(mode)) return null
  const location = form.location.trim()
  if (location.length === 0) return 'Choose where to save the file.'
  if (!isRooted(location)) return 'The file location must be an absolute path.'
  return null
}

/** The form as the server wants it: trimmed, `.qa403` appended, empty description as null. */
export function submittedForm(form: ProjectForm): ProjectForm {
  const notes = form.notes.trim()
  return {
    name: form.name.trim(),
    notes,
    location: form.location.trim().length > 0 ? ensureExtension(form.location.trim()) : '',
    includeResults: form.includeResults,
  }
}
