// Панель «Режим разработчика» (вход без пароля) не должна попасть в продакшен-сборку: она подключена под import.meta.env.DEV,
// и сборщик выбрасывает её вместе с вызовами /dev/*. Тест собирает фронтенд в режиме production во временную папку и ищет следы панели
// (в сборке с NODE_ENV=development они есть: проверено, так что тест действительно их ловит).
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'

const root = new URL('../../', import.meta.url).pathname
const MARKERS = ['/dev/seed-users', '/dev/login-user', '/dev/reset-lesson', '/dev/seed-staff', '/dev/staff-code', '/dev/seed-case', '/dev/seed-lesson', 'data-testid="dev-panel"', 'dev-panel', 'dev-user-1@example.com']

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? files(join(dir, e.name)) : [join(dir, e.name)]))
}

test('production build contains no developer panel and no /dev routes', { timeout: 120_000 }, () => {
  const out = mkdtempSync(join(tmpdir(), 'skillswap-prod-'))
  try {
    execFileSync(join(root, 'node_modules/.bin/vite'), ['build', '--mode', 'production', '--outDir', out, '--emptyOutDir', '--logLevel', 'error'], { cwd: root, stdio: 'pipe', env: { ...process.env, NODE_ENV: 'production' } })
    const code = files(out).filter((f) => /\.(js|html)$/.test(f)).map((f) => readFileSync(f, 'utf8')).join('\n')
    assert.ok(code.length > 10_000, 'сборка пустая')
    const found = MARKERS.filter((m) => code.includes(m))
    assert.deepEqual(found, [], `в продакшен-сборке остались следы панели разработчика: ${found.join(', ')}`)
  } finally {
    rmSync(out, { recursive: true, force: true })
  }
})
