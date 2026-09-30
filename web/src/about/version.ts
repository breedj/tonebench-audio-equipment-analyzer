/**
 * Splits the assembly's informational version into the part a person reads and the part a bug
 * report needs.
 *
 * `/api/app` returns `AssemblyInformationalVersionAttribute`, and the SDK appends the source
 * revision to it automatically — so an ordinary build answers something like
 * `0.2.0+96350a9f134a426ebd5d1f87d26353c92b536174`, and the about dialog's first version of this
 * printed all forty-one characters of it as the version number. The release number is what belongs
 * on the Version row; the commit is worth keeping, because it is the only thing that identifies a
 * build made between two releases, but seven characters of it are enough to find the commit and
 * the rest is noise.
 *
 * Pure and here rather than in AboutDialog.vue because nothing in a .vue is testable under the node
 * environment vitest runs in.
 */
export interface AppVersion {
  /** The semantic version, including any pre-release tag: `0.2.0`, `1.0.0-rc.2`. */
  release: string
  /** The abbreviated source revision, or null when the build carries no build metadata. */
  build: string | null
}

/** How much of a commit hash is shown. Git's own default abbreviation. */
const BUILD_ABBREVIATION = 7

export function splitVersion(informational: string): AppVersion {
  const trimmed = informational.trim()
  // SemVer build metadata is everything after the FIRST '+', and a pre-release tag (`-rc.2`) sits
  // before it — so splitting on '+' keeps the tag on the release where it belongs.
  const plus = trimmed.indexOf('+')
  if (plus < 0) return { release: trimmed, build: null }

  const release = trimmed.slice(0, plus)
  const build = trimmed.slice(plus + 1)
  return {
    release,
    // An empty `1.0.0+` is malformed rather than a build with an empty name; treat it as no build.
    build: build.length > 0 ? build.slice(0, BUILD_ABBREVIATION) : null,
  }
}
