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
    outDir: path.resolve(rootDir, '../src/Qa403.App/wwwroot'),
    emptyOutDir: true,
    // Vite's 500 kB default warns about bytes crossing the internet on a cold cache. This bundle
    // never does: Kestrel serves it from wwwroot on 127.0.0.1, so the browser reads it off local
    // disk. Raised rather than silenced, and only to 1 MB, so that it still catches a dependency
    // that doubles the bundle — which for an offline app is a licensing or supply-chain question
    // even when it is not a latency one.
    chunkSizeWarningLimit: 1024,
  },
})
