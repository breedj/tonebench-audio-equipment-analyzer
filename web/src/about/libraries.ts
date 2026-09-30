// THE OPEN-SOURCE LIBRARIES PAGE, as data rather than as markup in AboutDialog.vue — nothing in a
// .vue is testable under the node environment vitest runs in, and this is a list that rots
// silently: a dependency's version moves on every upgrade, and a licences page that quotes the
// wrong version is worse than none at all.
//
// So every entry that comes from a package manager carries a `package` id, and libraries.test.ts
// reads web/package.json and every src/**/*.csproj and fails when a version here disagrees with
// the manifest — or when a runtime dependency exists in a manifest and is missing from this list.
// The entries without a `package` (the .NET runtime, SQLite itself, the vendored libusb binary,
// the bundled typeface, Inno Setup) are things no manifest of ours names; they are listed because
// they are genuinely redistributed or genuinely used, and they are the ones to re-check by hand.
//
// Licences below were read from the installed packages, not from memory: `.nuspec` <license> for
// the NuGet ones, `license` in node_modules/*/package.json for the npm ones (2026-09-30).

export interface Library {
  name: string
  /** The version in use, when a manifest pins one. Checked against that manifest by the test. */
  version?: string
  /** Where the project lives — its own site or repository, never a package-registry mirror. */
  url: string
  /** SPDX identifier where there is one; the licence's own name where there is not. */
  licence: string
  /** What it does *here*. A licences page nobody can read is a compliance box, not a credit. */
  role: string
  /** Which manifest pins it, so the test can check the version and the coverage. */
  package?: { manifest: 'npm' | 'nuget'; id: string }
}

export interface LibraryGroup {
  title: string
  blurb: string
  items: Library[]
}

/**
 * What is redistributed inside the installed application. This is the group that carries licence
 * obligations — note the two LGPL entries, which is why USB access is a separately-linked library
 * and not vendored source.
 */
const SHIPPED: Library[] = [
  {
    name: '.NET 10 and ASP.NET Core',
    url: 'https://github.com/dotnet/runtime',
    licence: 'MIT',
    role: 'The runtime the application is built on, and the Kestrel web server it talks to your browser through.',
  },
  {
    name: 'LibUsbDotNet',
    version: '3.0.224',
    url: 'https://github.com/LibUsbDotNet/LibUsbDotNet',
    licence: 'LGPL-3.0-or-later',
    role: 'Managed USB transfers — every sample from the QA403 arrives through it.',
    package: { manifest: 'nuget', id: 'LibUsbDotNet' },
  },
  {
    name: 'libusb',
    url: 'https://libusb.info/',
    licence: 'LGPL-2.1-or-later',
    role: 'The native USB library underneath LibUsbDotNet. Shipped as a DLL on Windows; on Linux the distribution’s own libusb-1.0-0 is used instead.',
  },
  {
    name: 'FftFlat',
    version: '1.0.1',
    url: 'https://github.com/sinshu/fftflat',
    licence: 'MIT',
    role: 'The FFT behind every spectrum, sweep and response measurement. The only one of the three transforms here with a genuine real-input path, and the only one that allocates nothing per record.',
    package: { manifest: 'nuget', id: 'FftFlat' },
  },
  {
    name: 'FftSharp',
    version: '2.2.0',
    url: 'https://github.com/swharden/FftSharp',
    licence: 'MIT',
    role: 'A second transform, kept as a cross-check: the test suite runs all three against each other on a coherent tone, real and imaginary parts alike.',
    package: { manifest: 'nuget', id: 'FftSharp' },
  },
  {
    name: 'Math.NET Numerics',
    version: '5.0.0',
    url: 'https://numerics.mathdotnet.com/',
    licence: 'MIT',
    role: 'The third transform in that cross-check, and the linear algebra behind the pole–zero fit.',
    package: { manifest: 'nuget', id: 'MathNet.Numerics' },
  },
  {
    name: 'SQLite',
    url: 'https://sqlite.org/',
    licence: 'Public domain',
    role: 'The database engine. A project file is one ordinary SQLite database, readable by anything that reads SQLite.',
  },
  {
    name: 'Microsoft.Data.Sqlite',
    version: '10.0.10',
    url: 'https://learn.microsoft.com/dotnet/standard/data/sqlite/',
    licence: 'MIT',
    role: 'The SQLite driver — connections, transactions, and the BLOB streaming every stored trace is written through.',
    package: { manifest: 'nuget', id: 'Microsoft.Data.Sqlite' },
  },
  {
    name: 'SQLitePCLRaw',
    version: '3.0.5',
    url: 'https://github.com/ericsink/SQLitePCL.raw',
    licence: 'Apache-2.0',
    role: 'Carries the native SQLite build the driver binds to.',
    package: { manifest: 'nuget', id: 'SQLitePCLRaw.bundle_e_sqlite3' },
  },
  {
    name: 'Dapper',
    version: '2.1.79',
    url: 'https://github.com/DapperLib/Dapper',
    licence: 'Apache-2.0',
    role: 'Maps query rows onto records — everything in the database except the BLOBs.',
    package: { manifest: 'nuget', id: 'Dapper' },
  },
  {
    name: 'QuestPDF',
    version: '2026.9.0',
    url: 'https://github.com/QuestPDF/QuestPDF',
    licence: 'QuestPDF Community License',
    role: 'Lays out the PDF reports. Dual-licensed; this application is used under the Community terms.',
    package: { manifest: 'nuget', id: 'QuestPDF' },
  },
  {
    name: 'SkiaSharp',
    url: 'https://github.com/mono/SkiaSharp',
    licence: 'MIT',
    role: 'The graphics engine QuestPDF draws each report page with.',
  },
  {
    name: 'Lato',
    url: 'https://www.latofonts.com/',
    licence: 'SIL Open Font License 1.1',
    role: 'The typeface in the PDF reports, bundled with QuestPDF.',
  },
  {
    name: 'Serilog.AspNetCore',
    version: '10.0.0',
    url: 'https://serilog.net/',
    licence: 'Apache-2.0',
    role: 'Writes the diagnostic log — including the device teardown trace that names whatever blocked a shutdown, and, with detailed logging on, a line per API call.',
    package: { manifest: 'nuget', id: 'Serilog.AspNetCore' },
  },
  {
    name: 'Serilog.Sinks.File',
    version: '7.0.0',
    url: 'https://github.com/serilog/serilog-sinks-file',
    licence: 'Apache-2.0',
    role: 'The rolling log file itself.',
    package: { manifest: 'nuget', id: 'Serilog.Sinks.File' },
  },
  {
    name: 'Microsoft.Extensions.FileProviders.Embedded',
    version: '10.0.10',
    url: 'https://github.com/dotnet/aspnetcore',
    licence: 'MIT',
    role: 'Serves the user interface out of the executable, so the installed app is one file.',
    package: { manifest: 'nuget', id: 'Microsoft.Extensions.FileProviders.Embedded' },
  },
  {
    name: 'Microsoft.Extensions.Logging.Abstractions',
    version: '10.0.10',
    url: 'https://github.com/dotnet/runtime',
    licence: 'MIT',
    role: 'Lets the USB layer log without depending on a logging framework of its own.',
    package: { manifest: 'nuget', id: 'Microsoft.Extensions.Logging.Abstractions' },
  },
  {
    name: 'Vue',
    version: '3.5.40',
    url: 'https://github.com/vuejs/core',
    licence: 'MIT',
    role: 'The user interface — every screen, panel and dialog in this window.',
    package: { manifest: 'npm', id: 'vue' },
  },
  {
    name: 'µPlot',
    version: '1.6.32',
    url: 'https://github.com/leeoniya/uPlot',
    licence: 'MIT',
    role: 'Draws the charts. Fast enough to redraw a spectrum twenty times a second without dropping samples.',
    package: { manifest: 'npm', id: 'uplot' },
  },
  {
    name: 'Tailwind CSS',
    version: '4.3.3',
    url: 'https://tailwindcss.com/',
    licence: 'MIT',
    role: 'The styling, including the light and dark themes.',
    package: { manifest: 'npm', id: 'tailwindcss' },
  },
]

/**
 * Used to build and check the application, and not redistributed with it. Listed because "which
 * libraries do you use" is a fair question about the whole project, not only about the bytes that
 * happen to ship.
 */
const TOOLING: Library[] = [
  {
    name: 'Vite',
    version: '8.2.0',
    url: 'https://vite.dev/',
    licence: 'MIT',
    role: 'Bundles the user interface.',
    package: { manifest: 'npm', id: 'vite' },
  },
  {
    name: 'TypeScript',
    version: '6.0.2',
    url: 'https://www.typescriptlang.org/',
    licence: 'Apache-2.0',
    role: 'The language the user interface is written in.',
    package: { manifest: 'npm', id: 'typescript' },
  },
  {
    name: 'vue-tsc',
    version: '3.3.8',
    url: 'https://github.com/vuejs/language-tools',
    licence: 'MIT',
    role: 'Type-checks the Vue components.',
    package: { manifest: 'npm', id: 'vue-tsc' },
  },
  {
    name: 'Vitest',
    version: '3.2.4',
    url: 'https://vitest.dev/',
    licence: 'MIT',
    role: 'Runs the user-interface tests.',
    package: { manifest: 'npm', id: 'vitest' },
  },
  {
    name: 'PostCSS',
    version: '8.5.25',
    url: 'https://postcss.org/',
    licence: 'MIT',
    role: 'The stylesheet pipeline Tailwind runs in.',
    package: { manifest: 'npm', id: 'postcss' },
  },
  {
    name: 'Autoprefixer',
    version: '10.5.4',
    url: 'https://github.com/postcss/autoprefixer',
    licence: 'MIT',
    role: 'Adds vendor prefixes to the stylesheet.',
    package: { manifest: 'npm', id: 'autoprefixer' },
  },
  {
    name: 'sharp',
    version: '0.35.4',
    url: 'https://sharp.pixelplumbing.com/',
    licence: 'Apache-2.0',
    role: 'Renders the application icon from its SVG into every raster the installers need.',
    package: { manifest: 'npm', id: 'sharp' },
  },
  {
    name: 'png-to-ico',
    version: '3.0.2',
    url: 'https://github.com/steambap/png-to-ico',
    licence: 'MIT',
    role: 'Packs those rasters into the Windows .ico.',
    package: { manifest: 'npm', id: 'png-to-ico' },
  },
  {
    name: 'xUnit.net',
    version: '2.9.2',
    url: 'https://xunit.net/',
    licence: 'Apache-2.0',
    role: 'Runs the DSP, device, database and application tests.',
    package: { manifest: 'nuget', id: 'xunit' },
  },
  {
    name: 'BenchmarkDotNet',
    version: '0.14.0',
    url: 'https://benchmarkdotnet.org/',
    licence: 'MIT',
    role: 'Measures the FFT backends against each other — how the transform in use was chosen.',
    package: { manifest: 'nuget', id: 'BenchmarkDotNet' },
  },
  {
    name: 'Inno Setup',
    url: 'https://jrsoftware.org/isinfo.php',
    licence: 'Inno Setup License',
    role: 'Builds the Windows installer.',
  },
]

export const LIBRARY_GROUPS: LibraryGroup[] = [
  {
    title: 'In the application',
    blurb: 'Redistributed inside the installed application.',
    items: SHIPPED,
  },
  {
    title: 'Build and test tooling',
    blurb: 'Used to build and check it; not shipped with it.',
    items: TOOLING,
  },
]

/** Every library in both groups, which is what the tests and any count want. */
export const ALL_LIBRARIES: Library[] = LIBRARY_GROUPS.flatMap((g) => g.items)
