import { readFileSync } from 'node:fs'
import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// Если VITE_API_URL относительный (/api/v1), dev-сервер проксирует /api и SignalR-хабы (/hubs, WebSocket)
// на бэкенд. Во всех прокси xfwd: true: Vite добавляет X-Forwarded-For (адрес браузера) и X-Forwarded-Proto, а бэкенд в Development доверяет
// прокси на loopback и показывает в сеансах настоящий адрес клиента, а не ::1 (docs/BACKEND-DEV.md, «Адрес клиента за прокси»). Адрес бэкенда: VITE_PROXY_TARGET в .env (по умолчанию http://localhost:5023).
//
// HTTPS для проверки с другого устройства в сети (только разработка). Камеру, микрофон и Secure-cookie браузер
// разрешает лишь на https или localhost. Если в .env заданы DEV_HTTPS_KEY и DEV_HTTPS_CERT (пути к PEM-файлам
// самоподписанного сертификата вне репозитория), dev-сервер работает по https. Без них всё как раньше, по http.
// Тогда через этот же адрес идут и сигнальный канал LiveKit (/livekit → localhost:7880), и файлы хранилища
// (/<бакет> → localhost:3900): со страницы https браузер не пустит ws:// и http:// (смешанное содержимое), а относительная ссылка от IP не зависит.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_PROXY_TARGET || 'http://localhost:5023'
  const https =
    env.DEV_HTTPS_KEY && env.DEV_HTTPS_CERT
      ? { key: readFileSync(env.DEV_HTTPS_KEY), cert: readFileSync(env.DEV_HTTPS_CERT) }
      : undefined
  const livekitTarget = env.DEV_LIVEKIT_PROXY_TARGET || 'http://localhost:7880'
  const storageTarget = env.DEV_STORAGE_PROXY_TARGET || 'http://localhost:3900'
  const storageBucket = env.DEV_STORAGE_BUCKET || 'skillswap'
  return {
    plugins: [react()],
    server: {
      port: 5173,
      // Слушать все адреса (localhost и адрес Mac в сети), чтобы сайт открывался с телефона по Wi-Fi без флага --host. Только dev-сервер; сборку это не затрагивает.
      host: true,
      https,
      proxy: {
        '/api': { target, changeOrigin: true, xfwd: true },
        '/hubs': { target, changeOrigin: true, ws: true, xfwd: true },
        '/dev': { target, changeOrigin: true, xfwd: true }, // только Development-бэкенд: POST /dev/seed-lesson
        // LiveKit: в разработке фронт подключается к wss://<адрес страницы>/livekit (utils/livekitUrl.ts), клиент сам добавляет /rtc…
        '/livekit': { target: livekitTarget, changeOrigin: true, ws: true, xfwd: true, rewrite: (p) => p.replace(/^\/livekit/, '') },
        // Хранилище: бэкенд отдаёт ссылки на файлы относительными (Storage:RelativeUrls=true): /<бакет>/<ключ>?X-Amz-….
        // Подпись посчитана для Storage:Endpoint (localhost:3900), поэтому Host подменяется на адрес цели (changeOrigin: true):
        // ссылки открываются и на https://localhost:5173, и по адресу в сети, а смена IP их не ломает.
        [`/${storageBucket}/`]: { target: storageTarget, changeOrigin: true, xfwd: true },
      },
    },
  }
})
