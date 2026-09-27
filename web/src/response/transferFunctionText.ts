import { formatHz } from './format'
import type { FitResult, MeasuredBand } from './rationalFit'
import {
  denormalise,
  transferFunctionForm,
  type RealCoefficients,
  type Section,
  type TransferFunctionForm,
} from './transferFunction'

/**
 * The fitted transfer function as text: on screen, and in the four formats worth copying out.
 *
 * Separate from `transferFunction.ts` so the arithmetic and the wording can be tested apart, and
 * because `ResponseFormulaPanel` must hold neither — nothing in a `.vue` file is testable here.
 *
 * **Two conventions are stated on screen rather than implied**, because both are places a reader
 * silently assumes the other thing. Frequencies are printed in **hertz** while the coefficient
 * arrays are in **rad/s** — the usual way to be out by 2π is to read one as the other. And a
 * second-order section's middle coefficient is **2ζ**, not ζ and not 1/Q, so it is printed as a
 * plain number with the convention spelled out beside it; Q would have been more audio-native and
 * cannot be printed, since a zero pair on the jω axis has ζ = 0 and therefore infinite Q.
 */

export type TransferFunctionFormat = 'Text' | 'Latex' | 'SciPy' | 'Matlab' | 'Coefficients'

export const TRANSFER_FUNCTION_FORMATS: { id: TransferFunctionFormat; label: string }[] = [
  { id: 'Text', label: 'Plain text' },
  { id: 'Latex', label: 'LaTeX' },
  { id: 'SciPy', label: 'SciPy' },
  { id: 'Matlab', label: 'MATLAB' },
  { id: 'Coefficients', label: 'Coefficients' },
]

/** How the second-order sections are written, printed beside the formula so 2ζ is never read as ζ. */
export const SECTION_CONVENTION = 'second-order sections are 1 + 2ζ·s/ωₙ + (s/ωₙ)², with ωₙ = 2πf'

/** A run of formula text; `sup` marks it as a superscript so the panel can render it without parsing. */
export interface FormulaPart {
  text: string
  sup?: boolean
}

export interface RenderedFactor {
  parts: FormulaPart[]
}

export interface RenderedFormula {
  /** The leading number, signed. */
  gain: string
  numerator: RenderedFactor[]
  denominator: RenderedFactor[]
  /** Which normalisation is in force and, when it is the root form, why. */
  note: string
  /** The units and section conventions, which the panel prints under the formula. */
  conventions: string
}

/** Four significant figures, which is what the fit is worth and what the pole/zero table shows. */
function number4(value: number): string {
  if (!Number.isFinite(value)) return '—'
  const magnitude = Math.abs(value)
  if (magnitude !== 0 && (magnitude < 1e-3 || magnitude >= 1e5)) return value.toExponential(3)
  if (magnitude >= 100) return value.toFixed(1)
  if (magnitude >= 1) return value.toFixed(3)
  return value.toFixed(4)
}

/**
 * A number that round-trips exactly, and is valid in Python, MATLAB and JSON alike.
 *
 * `String(value)` rather than a fixed `toExponential(n)`: JavaScript's default is the *shortest*
 * representation that parses back to the identical double, so nothing is lost and nothing is padded.
 * A fixed ten significant digits was tried and is not enough — the roots of a high-order polynomial
 * are badly conditioned in its coefficients, so truncating an exported denominator can move a
 * high-Q pole visibly, and an export that does not reproduce the fit is worse than no export.
 */
function exact(value: number): string {
  if (!Number.isFinite(value)) return '0'
  return String(value)
}

const OMEGA = (freqHz: number) => `2π·${formatHz(freqHz)}`

/**
 * One section as display parts.
 *
 * Under `DcNormalised` the sign of a real root's `s` term is the sign of ζ — `(1 + s/ω)` in the left
 * half-plane, `(1 − s/ω)` in the right — which is how an unstable root announces itself in the
 * formula rather than only in a flag.
 */
function factorParts(section: Section, form: TransferFunctionForm['form']): FormulaPart[] {
  const omega = OMEGA(section.freqHz)

  if (form === 'DcNormalised') {
    if (section.kind === 'real') {
      const sign = section.zeta >= 0 ? '+' : '−'
      return [{ text: `(1 ${sign} s/${omega})` }]
    }
    const middle = 2 * section.zeta
    const sign = middle >= 0 ? '+' : '−'
    return [
      { text: `(1 ${sign} ${number4(Math.abs(middle))}·s/${omega} + (s/${omega})` },
      { text: '2', sup: true },
      { text: ')' },
    ]
  }

  if (section.kind === 'real') {
    const sign = section.zeta >= 0 ? '+' : '−'
    return [{ text: `(s ${sign} ${omega})` }]
  }
  const middle = 2 * section.zeta
  const sign = middle >= 0 ? '+' : '−'
  return [
    { text: '(s' },
    { text: '2', sup: true },
    { text: ` ${sign} ${number4(Math.abs(middle))}·${omega}·s + (${omega})` },
    { text: '2', sup: true },
    { text: ')' },
  ]
}

export function renderTransferFunction(fit: FitResult, band: MeasuredBand | null): RenderedFormula {
  const form = transferFunctionForm(fit, band)
  const note =
    form.form === 'DcNormalised'
      ? 'Normalised on DC: every section is 1 at s = 0, so the leading number is H(0).'
      : `Root form — ${form.rootFormReason}.`

  return {
    gain: number4(form.gain),
    numerator: form.numeratorSections.map((s) => ({ parts: factorParts(s, form.form) })),
    denominator: form.denominatorSections.map((s) => ({ parts: factorParts(s, form.form) })),
    note,
    conventions: `Frequencies in hertz, coefficient arrays in rad/s; ${SECTION_CONVENTION}.`,
  }
}

/** The display parts flattened to plain text, superscripts included literally. */
function factorText(section: Section, form: TransferFunctionForm['form']): string {
  return factorParts(section, form)
    .map((part) => (part.sup ? (part.text === '2' ? '²' : `^${part.text}`) : part.text))
    .join('')
}

function productText(sections: Section[], form: TransferFunctionForm['form']): string {
  if (sections.length === 0) return '1'
  return sections.map((s) => factorText(s, form)).join(' · ')
}

function plainText(fit: FitResult, band: MeasuredBand | null): string {
  const form = transferFunctionForm(fit, band)
  const numerator = productText(form.numeratorSections, form.form)
  const denominator = productText(form.denominatorSections, form.form)
  const gain = number4(form.gain)

  const lines = [
    `H(s) = ${gain} * [ ${numerator} ] / [ ${denominator} ]`,
    '',
    form.form === 'DcNormalised'
      ? 'Each section is 1 at s = 0, so the leading number is H(0).'
      : `Root form: ${form.rootFormReason}.`,
    `Frequencies in hertz; ${SECTION_CONVENTION}.`,
    `Fitted order ${fit.numeratorOrder}/${fit.denominatorOrder}, ${fit.rmsErrorDb.toFixed(3)} dB rms error`,
  ]
  if (band) lines.push(`Measured band ${formatHz(band.lowHz)} to ${formatHz(band.highHz)}`)
  return lines.join('\n')
}

/** A LaTeX factor. Kept to \frac and \cdot so it pastes into anything. */
function latexFactor(section: Section, form: TransferFunctionForm['form']): string {
  const omega = `2\\pi\\cdot${latexHz(section.freqHz)}`
  if (form === 'DcNormalised') {
    if (section.kind === 'real') {
      const sign = section.zeta >= 0 ? '+' : '-'
      return `\\left(1 ${sign} \\frac{s}{${omega}}\\right)`
    }
    const middle = 2 * section.zeta
    const sign = middle >= 0 ? '+' : '-'
    return `\\left(1 ${sign} ${number4(Math.abs(middle))}\\frac{s}{${omega}} + \\frac{s^2}{\\left(${omega}\\right)^2}\\right)`
  }
  if (section.kind === 'real') {
    const sign = section.zeta >= 0 ? '+' : '-'
    return `\\left(s ${sign} ${omega}\\right)`
  }
  const middle = 2 * section.zeta
  const sign = middle >= 0 ? '+' : '-'
  return `\\left(s^2 ${sign} ${number4(Math.abs(middle))}\\cdot${omega}\\cdot s + \\left(${omega}\\right)^2\\right)`
}

function latexHz(freqHz: number): string {
  if (!Number.isFinite(freqHz)) return '0'
  if (freqHz >= 1000) return `${number4(freqHz / 1000)}\\,\\mathrm{kHz}`
  return `${number4(freqHz)}\\,\\mathrm{Hz}`
}

function latex(fit: FitResult, band: MeasuredBand | null): string {
  const form = transferFunctionForm(fit, band)
  const product = (sections: Section[]) =>
    sections.length === 0 ? '1' : sections.map((s) => latexFactor(s, form.form)).join(' ')
  return `H(s) = ${number4(form.gain)}\\cdot\\frac{${product(form.numeratorSections)}}{${product(
    form.denominatorSections,
  )}}`
}

/**
 * Coefficients highest power first.
 *
 * **The reverse of `num[]`/`den[]`**, which are lowest power first. Both scipy and MATLAB take
 * descending arrays, and getting it backwards yields a model that is a valid transfer function of
 * something else entirely — the second classic way to ship a plausible wrong answer here, after 2π,
 * and pinned by its own test.
 */
function descending(coefficients: readonly number[]): number[] {
  return [...coefficients].reverse()
}

function scipy(coefficients: RealCoefficients): string {
  const num = descending(coefficients.num).map(exact).join(', ')
  const den = descending(coefficients.den).map(exact).join(', ')
  return [
    '# H(s), coefficients in rad/s, highest power first',
    'from scipy import signal',
    `H = signal.TransferFunction([${num}], [${den}])`,
  ].join('\n')
}

function matlab(coefficients: RealCoefficients): string {
  const num = descending(coefficients.num).map(exact).join(' ')
  const den = descending(coefficients.den).map(exact).join(' ')
  return ['% H(s), coefficients in rad/s, highest power first', `H = tf([${num}], [${den}]);`].join('\n')
}

function coefficientText(coefficients: RealCoefficients): string {
  return [
    'H(s) = num(s) / den(s), coefficients in rad/s, highest power first',
    `num = [${descending(coefficients.num).map(exact).join(', ')}]`,
    `den = [${descending(coefficients.den).map(exact).join(', ')}]`,
  ].join('\n')
}

export function formatTransferFunction(
  fit: FitResult,
  band: MeasuredBand | null,
  format: TransferFunctionFormat,
): string {
  const coefficients = denormalise(fit.model)
  switch (format) {
    case 'Latex':
      return latex(fit, band)
    case 'SciPy':
      return scipy(coefficients)
    case 'Matlab':
      return matlab(coefficients)
    case 'Coefficients':
      return coefficientText(coefficients)
    case 'Text':
    default:
      return plainText(fit, band)
  }
}
