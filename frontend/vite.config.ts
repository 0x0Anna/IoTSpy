import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Overridable for dockerized dev (e.g. VITE_BACKEND_HOST=host.docker.internal),
// since the frontend container's own `localhost` isn't the host machine's.
const backendTarget = `http://${process.env.VITE_BACKEND_HOST ?? 'localhost'}:5000`

export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      onwarn(warning, warn) {
        // Suppress pure annotation warnings from SignalR library
        if (
          warning.loc?.file?.includes('@microsoft/signalr') &&
          warning.message?.includes('/*#__PURE__*/')
        ) {
          return
        }
        warn(warning)
      },
    },
  },
  server: {
    port: 3000,
    host: true,
    proxy: {
      '/api': {
        target: backendTarget,
        changeOrigin: true,
        configure: (proxy) => {
          proxy.removeAllListeners('error')
          proxy.on('error', (err) => {
            // ECONNREFUSED: backend not running yet; ECONNRESET: backend restarted.
            // Both are expected during dev — the frontend handles reconnection.
            const code = (err as NodeJS.ErrnoException).code
            if (code !== 'ECONNRESET' && code !== 'ECONNREFUSED') {
              console.error('[vite] api proxy error:', err)
            }
          })
        },
      },
      '/hubs': {
        target: backendTarget,
        changeOrigin: true,
        ws: true,
        configure: (proxy) => {
          proxy.removeAllListeners('error')
          proxy.on('error', (err) => {
            const code = (err as NodeJS.ErrnoException).code
            if (code !== 'ECONNRESET' && code !== 'ECONNREFUSED') {
              console.error('[vite] ws proxy error:', err)
            }
          })
        },
      },
    },
  },
})
