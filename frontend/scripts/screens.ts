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
  after?: (page: Page) => Promise<void> // действие после открытия (например, открыть первый чат)
}
const screen = (path: string, name = path.replace(/^\//, '').replace(/[/?=&]/g, '_') || 'home'): Screen => ({ name, path })

// Открыть первую ссылку из списка (чат, обмен, обращение), чтобы снять и страницу подробностей
const openFirst = (selector: string) => async (page: Page) => {
  const link = page.locator(selector).first()
  if (await link.isVisible().catch(() => false)) {
    const before = page.url()
    await link.click()
    await page.waitForURL((u) => u.toString() !== before, { timeout: 5000 }).catch(() => undefined)
    await page.waitForTimeout(300)
    await page.waitForSelector('[aria-busy="true"]', { state: 'detached', timeout: 10000 }).catch(() => undefined)
    await page.waitForTimeout(800)
  }
}

const ROLES: Record<string, { screens: Screen[]; menu?: boolean }> = {
  public: { screens: ['/', '/login', '/register', '/forgot-password', '/reset-password?token=x&userId=u1', '/confirm-email', '/dev/staff-qr'].map((p) => screen(p)) },
  user: {
    menu: true,
    screens: [
      ...['/app', '/app/matches', '/app/exchanges', '/app/messages', '/app/calendar', '/app/notifications', '/app/reviews', '/app/cases', '/app/profile', '/app/settings'].map((p) => screen(p)),
      { name: 'app_messages_chat', path: '/app/messages', after: openFirst('a[href^="/app/messages/"]') },
      { name: 'app_exchange_detail', path: '/app/exchanges', after: openFirst('a[href^="/app/exchanges/"]') },
      { name: 'app_case_detail', path: '/app/cases', after: openFirst('a[href^="/app/cases/"]') },
    ],
  },
  moderator: {
    menu: true,
    screens: ['/moderation', '/moderation/queue', '/moderation/mine', '/moderation/appeals', '/moderation/notifications'].map((p) => screen(p)),
  },
  admin: {
    menu: true,
    screens: ['/admin', '/admin/cases', '/admin/cases?status=Open', '/admin/cases?q=%D0%BF%D0%BE%D0%B2%D0%B5%D0%B4%D0%B5%D0%BD%D0%B8%D0%B5-%D0%BD%D0%B5%D1%82%D1%82%D0%B0%D0%BA%D0%BE%D0%B3%D0%BE', '/admin/bans?q=dev', '/admin/bans', '/admin/users', '/admin/suggestions', '/admin/skills', '/admin/categories', '/admin/staff', '/admin/notifications'].map((p) => screen(p)),
  },
  superadmin: {
    menu: true,
    screens: [
      ...['/admin', '/admin/staff'].map((p) => screen(p)),
      // Блок «Админы» (только суперадмин) лежит далеко внизу страницы: прокручиваем к нему и снимаем окно экрана
      { name: 'admin_staff_admins', path: '/admin/staff', after: async (page) => void (await page.getByRole('heading', { name: 'Админы' }).first().scrollIntoViewIfNeeded().catch(() => undefined)) },
    ],
  },
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
const authLog: string[] = [] // последние ответы /api/v1/auth/*: помогает понять, почему слетел сеанс
async function openPage(page: Page, path: string, retry = true) {
  await page.goto(BASE + path, { waitUntil: 'load' }).catch(() => undefined)
  // Лимиты запросов (по IP и по пользователю) могут держать приложение в загрузке: приложение ждёт Retry-After, ждём и мы
  await page.waitForSelector('[aria-busy="true"]', { state: 'detached', timeout: 70_000 }).catch(() => undefined)
  // Без переходов и анимации: снимок после смены темы не должен поймать цвета на полпути
  await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; animation: none !important; }' }).catch(() => undefined)
  await page.waitForTimeout(600)
  if (retry && relogin && new URL(page.url()).pathname.startsWith('/login') && !path.startsWith('/login')) {
    problems.push(`сессия слетела при открытии ${path}: вход выполнен заново; последние ответы auth: ${authLog.slice(-8).join(', ')}`)
    await relogin()
    await openPage(page, path, false)
  }
}

async function shoot(page: Page, role: string, s: Screen) {
  const dir = `${OUT}${role}/`
  mkdirSync(dir, { recursive: true })
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: width === 360 ? 780 : 900 })
    // Страница открывается один раз на ширину, а тема переключается на месте (каждая загрузка = обновление токена, лимит 30 в минуту)
    await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' })
    await openPage(page, s.path)
    await s.after?.(page)
    for (const theme of THEMES) {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await page.waitForFunction((d) => document.documentElement.classList.contains("dark") === d, theme === "dark", { timeout: 4000 }).catch(async () => {
        // Событие смены системной темы иногда не доходит до страницы: ставим класс сами, чтобы снимок был в нужной теме (на пользователя это не влияет: тема переключается кнопкой)
        await page.evaluate((d) => document.documentElement.classList.toggle('dark', d), theme === 'dark')
      })
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
      await page.screenshot({ path: `${dir}${s.name}-${theme}-${width}.png`, fullPage: !process.env.SCREENS_VIEWPORT })
    }
  }
}

// Шаг «Подтверждение входа» (2FA): через интерфейс входа служебным аккаунтом, затем переключение на резервный код
async function shootTwoFactor(page: Page, acc: Account) {
  const dir = `${OUT}flows/`
  mkdirSync(dir, { recursive: true })
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: width === 360 ? 780 : 900 })
    for (const theme of THEMES) {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await openPage(page, '/login')
      await page.getByPlaceholder('name@example.com').fill(acc.email)
      await page.locator('input[type="password"]').first().fill(acc.password)
      // тестовая капча Cloudflare проходит сама, но не мгновенно
      await page.waitForTimeout(2500)
      await page.getByRole('button', { name: /^(Войти|Sign in|Log in|Ворид шудан)$/ }).click()
      const cell = page.locator('input[autocomplete="one-time-code"]').first()
      await cell.waitFor({ timeout: 6000 }).catch(() => undefined)
      if (!(await cell.isVisible().catch(() => false))) {
        // Тестовая капча иногда не успевает: ждём и нажимаем ещё раз
        await page.waitForTimeout(4000)
        await page.getByRole('button', { name: /^(Войти|Sign in|Log in|Ворид шудан)$/ }).click().catch(() => undefined)
        await cell.waitFor({ timeout: 15000 }).catch(() => undefined)
      }
      if (!(await cell.isVisible().catch(() => false))) {
        problems.push(`flows/2fa ${theme} ${width}: экран кода не открылся`)
        await page.screenshot({ path: `${dir}2fa-missing-${theme}-${width}.png` })
        continue
      }
      await page.waitForTimeout(400)
      await page.screenshot({ path: `${dir}2fa-${theme}-${width}.png`, fullPage: true })
      await page.getByRole('button', { name: /резервн|recovery|захира/i }).first().click().catch(() => undefined)
      await page.waitForTimeout(300)
      await page.screenshot({ path: `${dir}2fa-recovery-${theme}-${width}.png`, fullPage: true })
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


// ───────── Автоматические проверки поведения (кроме снимков): результат идёт в общий список замечаний ─────────

const AUTH_PAGES = ['/login', '/register', '/forgot-password', '/reset-password?token=x&userId=u1', '/confirm-email']

// Фон страниц входа: метки под содержимым, без кликов, приглушены, вне центральной полосы формы и вне шапки и подвала; на телефоне скрыты; при reduced motion стоят
async function checkFloaters(page: Page) {
  for (const path of AUTH_PAGES) {
    for (const width of [1440, 768, 360]) {
      await page.setViewportSize({ width, height: 900 })
      await page.emulateMedia({ reducedMotion: 'no-preference' })
      await openPage(page, path)
      const tag = `фон ${path.split('?')[0]} ${width}`
      const info = await page.evaluate(() => {
        const box = [...document.querySelectorAll<HTMLElement>('section div[aria-hidden="true"]')].find((d) => d.querySelector('span.will-change-transform'))
        if (!box) return null
        const cs = getComputedStyle(box)
        const formSection = box.closest('section')!
        const section = formSection.getBoundingClientRect()
        const cx = section.left + section.width / 2
        const spans = [...box.querySelectorAll<HTMLElement>('span.will-change-transform')]
        const content = [...formSection.querySelectorAll<HTMLElement>('h1, label, input, button, p, a')].filter((e) => !box.contains(e) && e.getBoundingClientRect().width > 0)
        const header = formSection.querySelector('a[href="/"]')?.getBoundingClientRect()
        const footer = [...formSection.querySelectorAll<HTMLElement>('p')].pop()?.getBoundingClientRect()
        const contentRects = content.map((e) => e.getBoundingClientRect())
        const box0 = box.getBoundingClientRect()
        // Содержимое формы: всё, что внутри колонки формы (кроме шапки и подвала), должно лежать в полосе ±212 px, где метки полностью скрыты маской
        const formRects = contentRects.filter((r) => header && footer && r.top > header.bottom && r.bottom < footer.top)
        return {
          display: cs.display,
          pointerEvents: cs.pointerEvents,
          zIndex: cs.zIndex,
          mask: cs.maskImage || cs.webkitMaskImage,
          opacity: Math.max(0, ...spans.map((e) => Number(getComputedStyle(e).opacity))),
          count: spans.length,
          boxTop: box0.top,
          boxBottom: box0.bottom,
          headerBottom: header?.bottom ?? 0,
          footerTop: footer?.top ?? 9999,
          formInsideBand: formRects.every((r) => r.left >= cx - 212 && r.right <= cx + 212),
          contentZ: [...formSection.querySelectorAll<HTMLElement>('.z-10')].map((e) => getComputedStyle(e).zIndex),
          t0: spans.map((e) => e.style.transform).join('|'),
        }
      })
      if (width < 768) {
        if (info && info.display !== 'none') problems.push(`${tag}: на телефоне метки должны быть скрыты (display=${info.display})`)
        continue
      }
      if (!info) {
        problems.push(`${tag}: контейнер с метками не найден`)
        continue
      }
      if (info.pointerEvents !== 'none') problems.push(`${tag}: у фона нет pointer-events: none`)
      if (!(Number(info.zIndex) <= 0 || info.zIndex === '0') || !info.contentZ.every((z) => Number(z) > Number(info.zIndex))) problems.push(`${tag}: метки не под содержимым (z фона ${info.zIndex}, содержимого ${info.contentZ.join(',')})`)
      if (!info.mask || info.mask === 'none') problems.push(`${tag}: нет маски безопасной зоны`)
      if (info.opacity > 0.5) problems.push(`${tag}: метки слишком яркие (opacity ${info.opacity})`)
      if (info.count > 12) problems.push(`${tag}: слишком много меток (${info.count})`)
      if (info.boxTop < info.headerBottom || info.boxBottom > info.footerTop + 1) problems.push(`${tag}: фон заходит на шапку или подвал`)
      if (!info.formInsideBand) problems.push(`${tag}: содержимое формы шире безопасной полосы (метки могли бы налезть)`)
      // Движение есть без reduced motion и нет с ним
      await page.waitForTimeout(1500)
      const moved = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('span.will-change-transform')].map((e) => e.style.transform).join('|'))
      if (moved === info.t0) problems.push(`${tag}: метки не двигаются`)
      await page.emulateMedia({ reducedMotion: 'reduce' })
      await openPage(page, path)
      const still0 = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('span.will-change-transform')].map((e) => e.style.transform).join('|'))
      await page.waitForTimeout(1500)
      const still1 = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('span.will-change-transform')].map((e) => e.style.transform).join('|'))
      if (still0 !== still1) problems.push(`${tag}: при prefers-reduced-motion метки двигаются`)
    }
  }
}

// «Обязательное поле» не показывается, пока не было попытки отправки (заход в поле и выход из него не считаются)
async function checkRequired(page: Page) {
  const word = 'Обязательное поле'
  for (const path of ['/login', '/register', '/forgot-password']) {
    await page.setViewportSize({ width: 1440, height: 900 })
    await openPage(page, path)
    const inputs = page.locator('section input:not([type="checkbox"]):not([type="hidden"])')
    const n = Math.min(await inputs.count(), 3)
    for (let i = 0; i < n; i++) {
      await inputs.nth(i).focus()
      await inputs.nth(i).blur()
    }
    await page.keyboard.press('Tab')
    if ((await page.locator(`text=${word}`).count()) > 0) problems.push(`${path}: «${word}» показано до попытки отправки`)
    await page.locator('section form button[type="submit"], section form button:not([type])').first().click()
    await page.waitForTimeout(400)
    if ((await page.locator(`text=${word}`).count()) === 0) problems.push(`${path}: после попытки отправки «${word}» не появилось`)
  }
}

// Серверный поиск в «Обращениях» админа: один запрос через ~300 мс после последнего символа, значение в адресе, сброс очищает и поле, и адрес
async function checkSearch(page: Page) {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openPage(page, '/admin/cases')
  const requests: string[] = []
  page.on('request', (r) => {
    if (r.url().includes('/api/v1/admin/cases?')) requests.push(r.url())
  })
  const box = page.getByPlaceholder('Поиск по тексту и именам')
  await box.pressSequentially('dev-user', { delay: 60 })
  const startedAt = Date.now()
  await page.waitForResponse((r) => r.url().includes('/admin/cases?') && r.url().includes('q=dev-user'), { timeout: 5000 }).catch(() => undefined)
  const waited = Date.now() - startedAt
  const withQ = requests.filter((u) => u.includes('q='))
  if (withQ.length !== 1) problems.push(`поиск: ожидался один запрос с q после набора, а их ${withQ.length}: ${withQ.map((u) => new URL(u).searchParams.get('q')).join(', ')}`)
  if (waited < 150) problems.push(`поиск: запрос ушёл слишком рано (${waited} мс после последнего символа)`)
  if (!new URL(page.url()).searchParams.get('q')?.includes('dev-user')) problems.push('поиск: значение не попало в адрес')
  await page.reload({ waitUntil: 'load' })
  await page.waitForSelector('[aria-busy="true"]', { state: 'detached', timeout: 30_000 }).catch(() => undefined)
  if ((await page.getByPlaceholder('Поиск по тексту и именам').inputValue()) !== 'dev-user') problems.push('поиск: после обновления страницы поле не восстановилось из адреса')
  await page.getByRole('button', { name: 'Сбросить' }).first().click()
  await page.waitForTimeout(600)
  if ((await page.getByPlaceholder('Поиск по тексту и именам').inputValue()) !== '' || new URL(page.url()).searchParams.has('q')) problems.push('поиск: «Сбросить» не очистил поле и адрес')
}

// Фильтр «Язык общения» (поиск партнёров): открытый список, поиск, клавиатура, высота, шторка на телефоне; снимки открытого состояния
async function checkLanguageSelect(page: Page) {
  const dir = `${OUT}user/`
  mkdirSync(dir, { recursive: true })
  const names = ['Любой', 'Таджикский', 'Русский', 'Английский'] // названия на языке интерфейса (как в выборе языков занятий), рядом коды TG, RU, EN
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: width === 360 ? 780 : 900 })
    await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' })
    await openPage(page, '/app/matches')
    const tag = `язык-фильтр ${width}`
    const trigger = page.getByRole('combobox', { name: 'Язык общения' })
    await trigger.scrollIntoViewIfNeeded()
    await trigger.click()
    const search = page.getByRole('searchbox')
    await search.waitFor({ timeout: 3000 }).catch(() => undefined)
    if (!(await search.evaluate((el) => el === document.activeElement).catch(() => false))) problems.push(`${tag}: поле поиска не в фокусе после открытия`)
    const opts = page.getByRole('option')
    const texts = (await opts.allInnerTexts()).map((x) => x.replace(/\s+/g, ' ').trim())
    if (!names.every((n, i) => texts[i]?.startsWith(n))) problems.push(`${tag}: сверху должны быть ${names.join(', ')}, а есть: ${texts.slice(0, 5).join(' | ')}`)
    if (texts.length !== 11) problems.push(`${tag}: ожидалось 11 вариантов без поиска (Любой, 3 основных, 7 популярных), есть ${texts.length}`)
    if (!(await page.getByText('Частые').first().isVisible().catch(() => false)) && !(await page.getByText('Популярные').first().isVisible().catch(() => false))) problems.push(`${tag}: нет разделителя «популярные»`)
    const geo = await page.evaluate(() => {
      const list = document.querySelector('[role="listbox"]')!
      const panel = list.parentElement!.getBoundingClientRect()
      return { top: panel.top, bottom: panel.bottom, height: panel.height, vh: window.innerHeight, fixed: getComputedStyle(list.parentElement!).position, scrollW: document.documentElement.scrollWidth - window.innerWidth }
    })
    if (geo.bottom > geo.vh + 1 || geo.top < -1) problems.push(`${tag}: список вылезает за экран (top ${Math.round(geo.top)}, bottom ${Math.round(geo.bottom)}, окно ${geo.vh})`)
    if (width >= 640 && geo.height > 330) problems.push(`${tag}: список выше 320 px (${Math.round(geo.height)})`)
    if (width < 640 && (geo.fixed !== 'fixed' || geo.height > geo.vh * 0.8 + 1)) problems.push(`${tag}: на телефоне нужна нижняя шторка не выше 80% экрана (position ${geo.fixed}, ${Math.round(geo.height)} из ${geo.vh})`)
    if (geo.scrollW > 1) problems.push(`${tag}: открытый список даёт горизонтальную прокрутку`)
    for (const theme of THEMES) {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' })
      await page.evaluate((d) => document.documentElement.classList.toggle('dark', d), theme === 'dark')
      await page.waitForTimeout(200)
      await page.screenshot({ path: `${dir}matches_language_open-${theme}-${width}.png` })
    }
    await page.evaluate(() => document.documentElement.classList.remove('dark'))

    // Поиск: по названию на любом языке и по коду, без регистра, не больше 8
    await search.fill('УЗБ')
    let found = (await opts.allInnerTexts()).join(' ')
    if (!/Узбек|Uzbek|Ӯзбек/i.test(found)) problems.push(`${tag}: поиск «УЗБ» не нашёл узбекский: ${found.slice(0, 80)}`)
    await search.fill('TURK')
    found = (await opts.allInnerTexts()).join(' ')
    if (!/Турецк|Turk|Туркӣ/i.test(found)) problems.push(`${tag}: поиск «TURK» не нашёл турецкий: ${found.slice(0, 80)}`)
    await search.fill('fa')
    if ((await opts.count()) > 8) problems.push(`${tag}: в результатах больше 8 записей`)
    await opts.first().waitFor({ timeout: 4000 }).catch(() => undefined)
    if (!/FA/.test(((await opts.first().innerText({ timeout: 2000 }).catch(() => '')) || '').toUpperCase())) problems.push(`${tag}: по коду «fa» первым должен идти персидский`)
    await search.fill('zzzzqq')
    if ((await opts.count()) !== 0 || !(await page.getByText('Ничего не найдено').isVisible().catch(() => false))) problems.push(`${tag}: нет сообщения «Ничего не найдено»`)
    await search.fill('')
    await search.fill('нем')
    await page.screenshot({ path: `${dir}matches_language_search-light-${width}.png` })

    // Клавиатура: ↓ ↓ Enter выбирает вторую найденную запись, галочка у выбранного, Esc закрывает
    await search.fill('')
    await page.keyboard.press('ArrowDown')
    await page.keyboard.press('ArrowDown')
    await page.keyboard.press('Enter')
    await page.waitForTimeout(300)
    const label = (await trigger.innerText()).trim()
    if (label === 'Любой' || (await page.getByRole('listbox').count()) !== 0) problems.push(`${tag}: ↓↓ Enter не выбрали язык или список не закрылся (кнопка: «${label}»)`)
    await trigger.click()
    const selected = page.locator('[role="option"][aria-selected="true"]').first()
    if (!(await selected.locator('svg').count())) problems.push(`${tag}: у выбранного языка нет галочки`)
    await page.keyboard.press('Escape')
    if ((await page.getByRole('listbox').count()) !== 0) problems.push(`${tag}: Esc не закрыл список`)
    // Сброс на «Любой» и закрытие кликом вне списка
    await trigger.click()
    await page.getByRole('option', { name: /^Любой/ }).click()
    if ((await trigger.innerText()).trim() !== 'Любой') problems.push(`${tag}: сброс на «Любой» не сработал`)
    await trigger.click()
    await page.mouse.click(width >= 640 ? 900 : 180, 8)
    await page.waitForTimeout(200)
    if ((await page.getByRole('listbox').count()) !== 0) problems.push(`${tag}: клик вне списка не закрыл его`)
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
  if (!ONLY || ONLY.includes('checks')) {
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ru-RU' })
    const page = await ctx.newPage()
    page.on('response', () => undefined)
    await checkFloaters(page)
    await checkRequired(page)
    await ctx.close()
  }
  if (!ONLY || ONLY.includes('flows')) {
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ru-RU' })
    const page = await ctx.newPage()
    await shootTwoFactor(page, seed.moderator)
    await ctx.close()
  }
  for (const [role, def] of Object.entries(ROLES)) {
    if (ONLY && !ONLY.includes(role)) continue
    if (!FILTER) rmSync(`${OUT}${role}/`, { recursive: true, force: true }) // старые снимки этой роли заменяются, остальные роли не трогаем
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'ru-RU' })
    const page = await ctx.newPage()
    page.on('response', (r) => {
      if (r.url().includes('/api/v1/') && (r.url().includes('/auth/') || !r.ok())) authLog.push(`${r.url().split('/api/v1/')[1].slice(0, 30)}=${r.status()}${r.headers()['retry-after'] ? '(ra ' + r.headers()['retry-after'] + ')' : ''}`)
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
      await page.waitForTimeout(4000) // не упираться в лимит обновления токена (30 в минуту с одного IP)
    }
    if (def.menu && !FILTER) await shootMenu(page, role, def.screens[0].path)
    if (role === 'admin' && (!FILTER || FILTER === 'search')) await checkSearch(page)
    if (role === 'user' && (!FILTER || FILTER === 'language')) await checkLanguageSelect(page).catch((e) => problems.push(`язык-фильтр: проверка прервалась: ${String(e.message).split('\n')[0]}`))
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
