import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import path from 'node:path'

// Vite ≥6 / Node ≥20: `import.meta.dirname` replaces the CJS-only `__dirname`.
const rootDir = import.meta.dirname

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:8399',
        changeOrigin: true,
      },
      '/ws': {
        target: 'ws://127.0.0.1:8399',
        ws: true,
      },
    },
  },
  build: {
    outDir: path.resolve(rootDir, '../src/Tonebench.App/wwwroot'),
    emptyOutDir: true,
    // Vite's 500 kB default warns about bytes crossing the internet on a cold cache. This bundle
    // never does: Kestrel serves it from wwwroot on 127.0.0.1, so the browser reads it off local
    // disk. Raised rather than silenced, so that it still catches a dependency that doubles the
    // bundle — which for an offline app is a licensing or supply-chain question even when it is
    // not a latency one.
    //
    // The figure is the main chunk's own size plus roughly half again, not a round number: at
    // 1024 it had been overtaken by ordinary growth (1051 kB on 2026-10-01) and printed five
    // lines of code-splitting advice on every single build, which is noise rather than a finding
    // — and a warning that is always on is one nobody reads the day it means something. Raise it
    // the same way when it next fires on growth alone; shrink the bundle instead if it ever
    // fires on one commit.
    chunkSizeWarningLimit: 1600,
  },
})
