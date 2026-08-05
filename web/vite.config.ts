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
  },
})
