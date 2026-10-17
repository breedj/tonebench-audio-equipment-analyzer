/**
 * Splits a message into plain and code segments on backtick pairs.
 *
 * The simulation import and re-analysis services write their warnings and refusals with the SPICE
 * directives in them marked the way source comments mark them -- ``This is a stepped (`.step`)
 * run``, ``use `.ac dec 100 <start> <stop>` `` -- and until 2026-10-17 the dialogs printed that
 * text verbatim, so the backticks themselves were on screen. They are markup, not punctuation: the
 * author meant "this is something you type", which is what the manual's own `<code>` says. Nothing
 * else in the app renders them, so this exists to be the one place that reads them.
 *
 * Two rules keep a message safe rather than pretty. An **unpaired** backtick is literal text, so a
 * stray tick can never swallow the rest of a sentence -- the whole point of a warning is the part
 * after the thing that went wrong. And an **empty** pair is literal too, since ``` `` ``` marks
 * nothing and treating it as a span would delete two characters the writer did type.
 *
 * Pure, and here rather than in a `.vue`, because nothing in a `.vue` is testable under the node
 * environment vitest runs in.
 */
export interface InlineSegment {
  /** The text itself, with the delimiting backticks removed on a code segment. Never empty. */
  readonly text: string
  /** True where this segment stood between a pair of backticks. */
  readonly code: boolean
}

/** Reads `text` as plain prose with backtick-delimited code spans in it. */
export function inlineCodeSegments(text: string): InlineSegment[] {
  const out: InlineSegment[] = []
  let plain = ''
  let i = 0

  while (i < text.length) {
    if (text[i] !== '`') {
      plain += text[i]
      i += 1
      continue
    }

    const close = text.indexOf('`', i + 1)
    if (close < 0 || close === i + 1) {
      // Unpaired, or an empty pair: literal, per the rules above.
      plain += text[i]
      i += 1
      continue
    }

    if (plain) {
      out.push({ text: plain, code: false })
      plain = ''
    }
    out.push({ text: text.slice(i + 1, close), code: true })
    i = close + 1
  }

  if (plain) out.push({ text: plain, code: false })
  return out
}
