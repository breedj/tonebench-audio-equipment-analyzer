/** Longer than any message this app writes, and short enough that a banner stays a banner. */
const MAX_LENGTH = 500

/**
 * The server's own sentence out of a failed response's body, or an empty string when the body is
 * not one.
 *
 * <p><b>Most of this app's refusals arrive as a JSON string</b>, quotes and all — `Results.Conflict`
 * and `Results.BadRequest` take a `string` and serialize it, so the wire carries
 * <code>"A measurement type named 'X' already exists in this project."</code> with the quote marks
 * in the bytes. Unwrapping that is the difference between a banner that reads like a sentence and
 * one that reads like a fragment of a protocol.</p>
 *
 * <p>The remaining shapes are bodies nobody wrote for a reader, and each is better replaced by the
 * caller's own wording than shown: ASP.NET's ProblemDetails object (whose `detail`/`title` is worth
 * unwrapping when it carries one), the developer exception page's HTML, and an empty body.</p>
 */
export function serverSentence(body: string): string {
  const text = body.trim()
  if (!text) return ''

  // A JSON string or object. Parsed rather than pattern-matched: the quotes are escaped inside, and
  // `\"` is exactly what a message naming a measurement would carry.
  if (text.startsWith('"') || text.startsWith('{')) {
    try {
      const doc: unknown = JSON.parse(text)
      if (typeof doc === 'string') return doc.trim()
      if (doc !== null && typeof doc === 'object') {
        const problem = doc as Record<string, unknown>
        const detail = [problem.detail, problem.title, problem.message].find(
          (v) => typeof v === 'string' && v.trim(),
        )
        return typeof detail === 'string' ? detail.trim() : ''
      }
      return ''
    } catch {
      // Not JSON after all — fall through and take it as the text it is.
    }
  }

  if (text.startsWith('<')) return ''

  // A stack trace arrives whole; one paragraph of it is all a banner can hold.
  return text.length > MAX_LENGTH ? `${text.slice(0, MAX_LENGTH).trimEnd()}…` : text
}

/**
 * The sentence an error banner shows when a request fails.
 *
 * <p>Its own module because it is the one branchy part of the API client and nothing else in
 * client.ts is unit-tested — the rest of that file is one-line wrappers over `fetch`.</p>
 *
 * <p>The ordinary case is {@link serverSentence}: this app's own refusals are written for a person
 * ("A measurement type named 'Frequency response' already exists in this project."), and that is
 * the whole message. Until 2026-10-11 every banner also carried the method, the path and the status
 * in front of it — a line of plumbing that made each refusal read as a fault in the app rather than
 * an answer to what was asked (Joost), and that two panels had already grown their own regex to
 * strip back off. The method and path are still kept on the error object (`ApiError.request`) for a
 * log or a bug report; they just do not reach the screen.</p>
 *
 * <p>The status is named in the fallback because without the server's own words it is the only
 * thing that distinguishes one failure from another.</p>
 */
export function apiErrorMessage(status: number, body: string): string {
  return serverSentence(body) || `The request failed (HTTP ${status}).`
}
