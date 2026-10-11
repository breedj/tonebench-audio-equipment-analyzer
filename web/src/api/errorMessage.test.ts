import { describe, expect, it } from 'vitest'
import { apiErrorMessage, serverSentence } from './errorMessage'

describe('apiErrorMessage', () => {
  // The case this exists for, in the words it was reported in — and in the bytes the server
  // actually sends: Results.Conflict(string) serializes its argument, so the quote marks are on
  // the wire and would otherwise end up on screen around the sentence.
  it('is the server’s own sentence, with nothing in front of it and no quotes around it', () => {
    const refusal = "A measurement type named 'Frequency response' already exists in this project."

    const message = apiErrorMessage(409, JSON.stringify(refusal))

    expect(message).toBe(refusal)
    expect(message).not.toContain('409')
    expect(message).not.toContain('/api')
    expect(message).not.toContain('failed')
    expect(message).not.toContain('"')
  })

  it('reads a plain-text body as the sentence it is', () => {
    expect(apiErrorMessage(409, 'No project is open. Open or create a project first.')).toBe(
      'No project is open. Open or create a project first.',
    )
  })

  it('still says something, and which status, when the body is empty', () => {
    expect(apiErrorMessage(503, '')).toBe('The request failed (HTTP 503).')
    expect(apiErrorMessage(500, '   \n ')).toBe('The request failed (HTTP 500).')
  })

  describe('a body nobody wrote for a reader', () => {
    it('unwraps ProblemDetails, preferring the detail over the title', () => {
      const problem = JSON.stringify({
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        detail: 'The load must be greater than zero.',
      })

      expect(apiErrorMessage(400, problem)).toBe('The load must be greater than zero.')
    })

    it('falls back to the title when that is all there is', () => {
      expect(apiErrorMessage(400, JSON.stringify({ title: 'Bad Request', status: 400 }))).toBe('Bad Request')
    })

    it('falls back to the plain statement for JSON that names no reason', () => {
      expect(apiErrorMessage(500, JSON.stringify({ status: 500, traceId: '00-abc-def-01' }))).toBe(
        'The request failed (HTTP 500).',
      )
    })

    // The developer exception page. Its first line is <!DOCTYPE html>, which says nothing.
    it('does not put a page of HTML in a banner', () => {
      expect(apiErrorMessage(500, '<!DOCTYPE html>\n<html lang="en">\n<head><title>Error</title>')).toBe(
        'The request failed (HTTP 500).',
      )
    })

    it('shows text that only looks like JSON as the text it is', () => {
      expect(apiErrorMessage(400, '{ this was never JSON')).toBe('{ this was never JSON')
      expect(apiErrorMessage(400, '"unterminated')).toBe('"unterminated')
    })

    it('cuts a stack trace down to something a banner can hold', () => {
      const trace = `System.InvalidOperationException: it went wrong\n${'   at Tonebench.App.Something()\n'.repeat(60)}`

      const message = apiErrorMessage(500, trace)

      expect(message.length).toBeLessThanOrEqual(501)
      expect(message.startsWith('System.InvalidOperationException: it went wrong')).toBe(true)
      expect(message.endsWith('…')).toBe(true)
    })
  })
})

describe('serverSentence', () => {
  // The distinction apiErrorMessage is built on: this says whether the server gave a reason at all,
  // where that one always produces something to show. issueReport.ts needs the first question.
  it('is empty for every body that is not a sentence', () => {
    expect(serverSentence('')).toBe('')
    expect(serverSentence('   ')).toBe('')
    expect(serverSentence('<!DOCTYPE html><html></html>')).toBe('')
    expect(serverSentence(JSON.stringify({ status: 500, traceId: 'abc' }))).toBe('')
  })

  it('unwraps the JSON string the refusals arrive as', () => {
    expect(serverSentence('"It said so itself."')).toBe('It said so itself.')
  })
})
