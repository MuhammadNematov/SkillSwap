import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// Если VITE_API_URL относительный (/api/v1), dev-сервер проксирует /api и SignalR-хабы (/hubs, WebSocket)
// на бэкенд. Адрес бэкенда: VITE_PROXY_TARGET в .env (по умолчанию http://localhost:5023).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_PROXY_TARGET || 'http://localhost:5023'
  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': { target, changeOrigin: true },
        '/hubs': { target, changeOrigin: true, ws: true },
        '/dev': { target, changeOrigin: true }, // только Development-бэкенд: POST /dev/seed-lesson
      },
    },
  }
})
