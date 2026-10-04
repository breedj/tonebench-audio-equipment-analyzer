import { describe, expect, it } from 'vitest'
import type { Project } from '../api/client'
import {
  initialForm,
  locationFor,
  showsIncludeResults,
  showsLocation,
  submittedForm,
  validateForm,
} from './projectDialogModel'

const current: Project = { id: 1, name: 'GM70 amp', notes: 'the big one', createdUtc: '2026-09-19T10:00:00Z' }

describe('initialForm', () => {
  it('starts New empty', () => {
    expect(initialForm('new', current)).toEqual({ name: '', notes: '', location: '', includeResults: true })
  })

  it('prefills Save As with the open project but no location, results included', () => {
    expect(initialForm('saveAs', current)).toEqual({ name: 'GM70 amp', notes: 'the big one', location: '', includeResults: true })
  })

  it('prefills Properties with the open project', () => {
    const form = initialForm('properties', current)
    expect(form.name).toBe('GM70 amp')
    expect(form.notes).toBe('the big one')
    expect(initialForm('properties', { ...current, notes: null }).notes).toBe('')
  })

  it('shows the location everywhere but Properties, and the checkbox only on Save As', () => {
    expect(showsLocation('new')).toBe(true)
    expect(showsLocation('saveAs')).toBe(true)
    expect(showsLocation('properties')).toBe(false)
    expect(showsIncludeResults('saveAs')).toBe(true)
    expect(showsIncludeResults('new')).toBe(false)
  })
})

describe('locationFor', () => {
  it('follows the name until the user edits the location', () => {
    expect(locationFor('new', 'GM70: amp', 'C:\\P', '\\', false)).toBe('C:\\P\\GM70 amp.tbench')
    expect(locationFor('new', '', 'C:\\P', '\\', false)).toBe('')
    expect(locationFor('new', 'GM70', 'C:\\P', '\\', true)).toBeNull()
  })

  it('never suggests one for Save As', () => {
    expect(locationFor('saveAs', 'GM70', 'C:\\P', '\\', false)).toBeNull()
  })
})

describe('validateForm', () => {
  const ok = { name: 'x', notes: '', location: 'C:\\P\\x.tbench', includeResults: true }

  it('needs a name, and an absolute location outside Properties', () => {
    expect(validateForm('new', ok)).toBeNull()
    expect(validateForm('new', { ...ok, name: '  ' })).toMatch(/name/)
    expect(validateForm('new', { ...ok, location: '' })).toMatch(/where/)
    expect(validateForm('new', { ...ok, location: 'x.tbench' })).toMatch(/absolute/)
    expect(validateForm('saveAs', { ...ok, location: '/home/j/x' })).toBeNull()
    expect(validateForm('properties', { ...ok, location: '' })).toBeNull()
  })
})

describe('submittedForm', () => {
  it('trims and appends the extension', () => {
    expect(submittedForm({ name: ' a ', notes: ' d ', location: ' C:\\P\\a ', includeResults: false }))
      .toEqual({ name: 'a', notes: 'd', location: 'C:\\P\\a.tbench', includeResults: false })
    expect(submittedForm({ name: 'a', notes: '', location: '', includeResults: true }).location).toBe('')
  })
})
