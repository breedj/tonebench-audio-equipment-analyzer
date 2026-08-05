import { defineConfig } from 'vitest/config'

// Separate from vite.config.ts deliberately: the tests here are plain TS (metrics.ts has no Vue
// dependency), so this stays minimal and avoids pulling @vitejs/plugin-vue's Plugin type into
// vitest/config's defineConfig, which — under this project's Vite 8 — resolves to an
// incompatible rolldown-vite Plugin type and fails typecheck (see vue-tsc error if you try to
// merge this into vite.config.ts's own defineConfig instead).
export default defineConfig({
  test: {
    environment: 'node',
  },
})
