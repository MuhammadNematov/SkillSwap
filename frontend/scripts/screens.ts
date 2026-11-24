// Скриншоты основных экранов для проверки интерфейса глазами: пользователь, модератор, админ, суперадмин;
// светлая и тёмная тема; ширина 1440 и 360 px. Запуск: `npm run screens` (нужны запущенные бэкенд и фронт).
// Вход идёт через API (как в браузере: cookie с refresh-токеном), код 2FA сотрудников берётся из /dev/staff-code (только Development).
// Результат: frontend/screenshots/<роль>/<экран>-<тема>-<ширина>.png (папка в .gitignore) и отчёт о горизонтальной прокрутке и ошибках консоли.
//
// Переменные: SCREENS_BASE (по умолчанию https://localhost:5173), SCREENS_ONLY=user,admin (роли), SCREENS_FILTER=admin/cases (подстрока в имени экрана).
import { mkdirSync, rmSync } from 'node:fs'
import { chromium, type APIRequestContext, type Browser, type BrowserContext, type Page } from '@playwright/test'

const BASE = process.env.SCREENS_BASE ?? 'https://localhost:5173'
const OUT = new URL('../screenshots/', import.meta.url).pathname
const WIDTHS = [1440, 360] as const
const THEMES = ['light', 'dark'] as const
const ONLY = process.env.SCREENS_ONLY?.split(',')
const FILTER = process.env.SCREENS_FILTER

interface Account {
  email: string
  password: string
}
interface Seed {
  superAdmin: Account
  admin: Account
  moderator: Account
  users: (Account & { label: string })[]
}

interface Screen {
  name: string
  path: string
}
const screen = (path: string, name = path.replace(/^\//, '').replace(/[/?=&]/g, '_') || 'home'): Screen => ({ name, path })

const ROLES: Record<string, { screens: Screen[]; menu?: boolean }> = {
  public: { screens: ['/', '/login', '/register', '/forgot-password', '/reset-password?token=x&email=a@b.c', '/confirm-email'].map((p) => screen(p)) },
  user: {
    menu: true,
    screens: ['/app', '/app/matches', '/app/exchanges', '/app/messages', '/app/calendar', '/app/notifications', '/app/reviews', '/app/cases', '/app/profile', '/app/settings'].map((p) => screen(p)),
  },
  moderator: {
    menu: true,
    screens: ['/moderation', '/moderation/queue', '/moderation/mine', '/moderation/appeals', '/moderation/notifications'].map((p) => screen(p)),
  },
  admin: {
    menu: true,
    screens: ['/admin', '/admin/cases', '/admin/cases?status=Open', '/admin/bans', '/admin/users', '/admin/suggestions', '/admin/skills', '/admin/categories', '/admin/staff', '/admin/notifications'].map((p) => screen(p)),
  },
  superadmin: { menu: true, screens: ['/admin', '/admin/staff'].map((p) => screen(p)) },
}

// Лимитер входа по IP (429): ждём указанное в ответе время и повторяем
async function wait429(r: { status(): number; headers(): Record<string, string>; text(): Promise<string> }): Promise<boolean> {
  if (r.status() !== 429) return false
  const sec = Number(r.headers()['retry-after'] ?? (await r.text().catch(() => '')).match(/через (\d+)/)?.[1] ?? 20)
  console.log(`  лимит запросов, жду ${sec + 1} с`)
  await new Promise((done) => setTimeout(done, (sec + 1) * 1000))
  return true
}

async function json<T>(req: APIRequestContext, method: 'get' | 'post', path: string, data?: unknown): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    const r = await req[method](BASE + path, data === undefined ? undefined : { data })
    if (!r.ok()) {
      if (attempt < 4 && (await wait429(r))) continue
      throw new Error(`${method.toUpperCase()} ${path}: ${r.status()} ${(await r.text()).slice(0, 200)}`)
    }
    return (await r.json()) as T
  }
}

// Вход через API: cookie с refresh-токеном остаётся в контексте, приложение само получает сессию при открытии страницы
async function signIn(ctx: BrowserContext, acc: Account, staff: boolean) {
  const res = await json<{ requires2fa?: boolean; twoFactorToken?: string }>(ctx.request, 'post', '/api/v1/auth/login', { email: acc.email, password: acc.password })
  if (!res.requires2fa) return
  if (!staff) throw new Error(`${acc.email}: неожиданно запрошена 2FA`)
  // Использованный код сгорает: при повторном входе в том же 30-секундном окне берём следующий, а если и он занят, ждём новое окно
  for (let attempt = 0; attempt < 3; attempt++) {
    const { codes } = await json<{ codes: string[] }>(ctx.request, 'post', '/dev/staff-code', { email: acc.email })
    for (const code of codes) {
      const r = await ctx.request.post(BASE + '/api/v1/auth/login/2fa', { data: { code, isRecoveryCode: false, twoFactorToken: res.twoFactorToken } })
      if (r.ok()) return
      if (await wait429(r)) break
    }
    await new Promise((done) => setTimeout(done, 31_000))
  }
  throw new Error(`${acc.email}: не удалось войти с кодом 2FA`)
}

const problems: string[] = []

// networkidle не подходит: SignalR держит соединение. Ждём загрузку, затем пока исчезнут скелетоны (до 8 с) и ещё чуть-чуть на отрисовку.
// Сессия может слететь (ротация refresh-токена при частых перезагрузках): тогда приложение уводит на /login, и мы входим заново и повторяем.
let relogin: (() => Promise<void>) | null = null
async function openPage(page: Page, path: string, retry = true) {
  await page.goto(BASE + path, { waitUntil: 'load' }).catch(() => undefined)
  await page.waitForSelector('[aria-busy="true"]', { state: 'detached', timeout: 8000 }).catch(() => undefined)
  await page.waitForTimeout(600)
  if (retry && relogin && new URL(page.url()).pathname.startsWith('/login') && !path.startsWith('/login')) {
    problems.push(`сессия слетела при открытии ${path}: вход выполнен заново`)
    await relogin()
    await openPage(page, path, false)
  }
}

async function shoot(page: Page, role: string, s: Screen) {
  const dir = `${OUT}${role}/`
  mkdirSync(dir, { recursive: true })
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: width === 360 ? 780 : 900 })
    for (const theme of THEMES) {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await openPage(page, s.path)
      const tag = `${role}/${s.name} ${theme} ${width}`
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
      if (overflow > 1) {
        // Кто выходит за правый край: самые вложенные элементы с правой границей правее окна (подсказка, что чинить)
        const culprits = await page.evaluate(() => {
          const w = window.innerWidth
          return [...document.querySelectorAll('body *')]
            .filter((el) => el.getBoundingClientRect().right > w + 1 && ![...el.children].some((c) => c.getBoundingClientRect().right > w + 1))
            .slice(0, 3)
            .map((el) => `<${el.tagName.toLowerCase()} class="${String(el.getAttribute('class') ?? '').slice(0, 70)}"> «${(el.textContent ?? '').trim().slice(0, 30)}»`)
        })
        problems.push(`${tag}: горизонтальная прокрутка +${overflow}px; выходят: ${culprits.join(' | ')}`)
      }
      const dark = await page.evaluate(() => document.documentElement.classList.contains('dark'))
      if (dark !== (theme === 'dark')) problems.push(`${tag}: тема не применилась`)
      await page.screenshot({ path: `${dir}${s.name}-${theme}-${width}.png`, fullPage: true })
    }
  }
}

// Мобильное меню кабинета: открыть кнопкой «Меню» и снять в обеих темах
async function shootMenu(page: Page, role: string, path: string) {
  const dir = `${OUT}${role}/`
  await page.setViewportSize({ width: 360, height: 780 })
  for (const theme of THEMES) {
    await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
    await openPage(page, path)
    const button = page.getByRole('button', { name: /^(Меню|Menu)$/ }).first()
    await button.waitFor({ timeout: 8000 }).catch(() => undefined)
    if (!(await button.isVisible().catch(() => false))) {
      problems.push(`${role}/menu ${theme} 360: кнопка меню не найдена (снимок menu-missing-${theme}-360.png)`)
      await page.screenshot({ path: `${dir}menu-missing-${theme}-360.png` })
      continue
    }
    await button.click()
    await page.waitForTimeout(500)
    await page.screenshot({ path: `${dir}menu-${theme}-360.png` })
  }
}

async function main() {
  const seed = await (async () => {
    const browser0 = await chromium.launch({ channel: 'chromium' })
    const c = await browser0.newContext({ ignoreHTTPSErrors: true })
    const s = await json<Seed>(c.request, 'post', '/dev/seed-admin')
    await browser0.close()
    return s
  })()
  const accounts: Record<string, { acc: Account; staff: boolean }> = {
    user: { acc: seed.users.find((u) => u.label === 'userB') ?? seed.users[0], staff: false },
    moderator: { acc: seed.moderator, staff: true },
    admin: { acc: seed.admin, staff: true },
    superadmin: { acc: seed.superAdmin, staff: true },
  }

  const browser: Browser = await chromium.launch({ channel: 'chromium' })
  for (const [role, def] of Object.entries(ROLES)) {
    if (ONLY && !ONLY.includes(role)) continue
    if (!FILTER) rmSync(`${OUT}${role}/`, { recursive: true, force: true }) // старые снимки этой роли заменяются, остальные роли не трогаем
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ru-RU' })
    const page = await ctx.newPage()
    page.on('response', (r) => {
      if (role !== 'public' && r.url().includes('/auth/refresh') && !r.ok()) problems.push(`${role}: /auth/refresh ответил ${r.status()}`)
    })
    page.on('pageerror', (e) => problems.push(`${role}: ошибка страницы: ${e.message.slice(0, 160)}`))
    page.on('console', (m) => {
      // Обрывы SignalR при переходах между страницами (и лимит запросов на частые перезагрузки) для проверки не важны
      if (m.type() === 'error' && !/favicon|Failed to load resource|negotiation|WebSocket|SignalR|hubs\//.test(m.text())) problems.push(`${role}: консоль: ${m.text().slice(0, 160)}`)
    })
    if (role !== 'public') {
      const { acc, staff } = accounts[role]
      await signIn(ctx, acc, staff)
      relogin = () => signIn(ctx, acc, staff)
    } else relogin = null
    for (const s of def.screens) {
      if (FILTER && !`${role}/${s.name}`.includes(FILTER)) continue
      console.log(`→ ${role}/${s.name}`)
      await shoot(page, role, s)
    }
    if (def.menu && !FILTER) await shootMenu(page, role, def.screens[0].path)
    await ctx.close()
  }
  await browser.close()
  console.log(problems.length ? `\nЗамечания (${problems.length}):\n${[...new Set(problems)].join('\n')}` : '\nАвтоматических замечаний нет')
  console.log(`Скриншоты: ${OUT}`)
}

main().catch((e) => {
  console.error(e)
  process.exit(1)
})
