import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { ALL_LIBRARIES, LIBRARY_GROUPS, type Library } from './libraries'

// A licences page is the one screen in the app that nobody looks at until it matters, and it rots
// on every dependency bump with nothing to say so. These tests read the manifests themselves, so
// the list cannot quote a version that is no longer installed, and a new runtime dependency cannot
// reach a release without being credited.
//
// Scope is deliberate: RUNTIME dependencies must be listed (npm `dependencies`, and every
// PackageReference in a project that is not a test or benchmark project), because those are the
// ones redistributed and the ones carrying obligations. Test-only and dev-only packages may be
// listed — several are, under "Build and test tooling" — but are not required to be, or the page
// would fill up with @types/node.

const webRoot = resolve(import.meta.dirname, '..', '..')
const srcRoot = resolve(webRoot, '..', 'src')

interface Manifest {
  runtime: Map<string, string>
  all: Map<string, string>
}

function npmManifest(): Manifest {
  const pkg = JSON.parse(readFileSync(join(webRoot, 'package.json'), 'utf8')) as {
    dependencies?: Record<string, string>
    devDependencies?: Record<string, string>
  }
  // `^3.5.40` / `~6.0.2` -> `3.5.40` / `6.0.2`. The range operator is a policy for `npm install`,
  // not part of the version anyone would print on a licences page.
  const exact = (range: string) => range.replace(/^[\^~]/, '')
  const runtime = new Map(Object.entries(pkg.dependencies ?? {}).map(([k, v]) => [k, exact(v)]))
  const all = new Map([...runtime, ...Object.entries(pkg.devDependencies ?? {}).map(([k, v]) => [k, exact(v)] as const)])
  return { runtime, all }
}

function nugetManifest(): Manifest {
  const runtime = new Map<string, string>()
  const all = new Map<string, string>()
  for (const project of readdirSync(srcRoot, { withFileTypes: true })) {
    if (!project.isDirectory()) continue
    const csproj = join(srcRoot, project.name, `${project.name}.csproj`)
    let text: string
    try {
      text = readFileSync(csproj, 'utf8')
    } catch {
      continue
    }
    // A test or benchmark project's references are never redistributed.
    const shipped = !project.name.endsWith('.Tests') && !project.name.endsWith('.Benchmarks')
    for (const match of text.matchAll(/<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g)) {
      const [, id, version] = match
      all.set(id, version)
      if (shipped) runtime.set(id, version)
    }
  }
  return { runtime, all }
}

const npm = npmManifest()
const nuget = nugetManifest()

function manifestFor(library: Library): Manifest {
  return library.package!.manifest === 'npm' ? npm : nuget
}

describe('the library list', () => {
  it('names something for every entry', () => {
    for (const library of ALL_LIBRARIES) {
      expect(library.name.length, library.name).toBeGreaterThan(0)
      expect(library.licence.length, library.name).toBeGreaterThan(0)
      expect(library.role.length, library.name).toBeGreaterThan(0)
    }
  })

  it('links every entry to the project itself over https', () => {
    for (const library of ALL_LIBRARIES) {
      expect(library.url, library.name).toMatch(/^https:\/\//)
      // A registry mirror answers "where do I get the package", which is not the question a
      // credits page asks. Every link here goes to the project's own site or repository.
      expect(library.url, library.name).not.toMatch(/nuget\.org|npmjs\.com/)
    }
  })

  it('lists each library once', () => {
    const names = ALL_LIBRARIES.map((l) => l.name)
    expect(new Set(names).size).toBe(names.length)
  })
})

describe('versions', () => {
  it('match the manifest that pins them', () => {
    for (const library of ALL_LIBRARIES.filter((l) => l.package)) {
      const installed = manifestFor(library).all.get(library.package!.id)
      expect(installed, `${library.name} is not in its ${library.package!.manifest} manifest`).toBeDefined()
      expect(library.version, `${library.name} version`).toBe(installed)
    }
  })

  it('are given wherever a manifest pins one, and only there', () => {
    for (const library of ALL_LIBRARIES) {
      // No manifest of ours names the .NET runtime, SQLite itself, the vendored libusb binary, the
      // typeface QuestPDF bundles or Inno Setup — so those carry no version rather than a stale one.
      if (library.package) expect(library.version, library.name).toBeDefined()
      else expect(library.version, library.name).toBeUndefined()
    }
  })
})

describe('coverage', () => {
  const listed = new Set(ALL_LIBRARIES.filter((l) => l.package).map((l) => `${l.package!.manifest}:${l.package!.id}`))

  it('credits every npm runtime dependency', () => {
    for (const id of npm.runtime.keys()) {
      expect(listed.has(`npm:${id}`), `${id} ships but is not on the libraries page`).toBe(true)
    }
  })

  it('credits every NuGet package a shipped project references', () => {
    for (const id of nuget.runtime.keys()) {
      expect(listed.has(`nuget:${id}`), `${id} ships but is not on the libraries page`).toBe(true)
    }
  })

  it('puts the shipped libraries and the tooling in separate groups', () => {
    expect(LIBRARY_GROUPS.map((g) => g.title)).toEqual(['In the application', 'Build and test tooling'])
    const shipped = new Set(LIBRARY_GROUPS[0].items.map((l) => l.name))
    for (const tool of LIBRARY_GROUPS[1].items) expect(shipped.has(tool.name)).toBe(false)
  })
})
