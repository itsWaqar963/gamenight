import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // The compiled SPA lands in server/public — the folder Fastify already serves.
  // One deployable, one origin, zero CORS (SDD §8.3).
  build: { outDir: '../server/public', emptyOutDir: true },
  // Dev mode: Vite serves the UI on :5173 with hot reload and PROXIES api/auth
  // calls to the real server on :8080 — two processes, one apparent origin.
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/auth': 'http://localhost:8080',
      '/healthz': 'http://localhost:8080',
    },
  },
});
