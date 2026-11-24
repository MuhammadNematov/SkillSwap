// Раньше этого года дату рождения не принимаем: <input type="date"> позволяет набрать год 0007, и тогда «возраст» выходит около 2000 лет.
export const MIN_BIRTH_YEAR = 1900

const isLeapYear = (y: number) => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0
const daysInMonth = (y: number, m: number) => [31, isLeapYear(y) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][m - 1]

// Проверка даты рождения (строка YYYY-MM-DD из <input type="date">). Возвращает ключ перевода ошибки или null, если всё хорошо.
// Дату проверяем по цифрам, без `new Date`: он превращает год 0–99 в 1900–1999. Окончательную проверку делает сервер.
export function birthDateError(iso: string, minAge: number, today = new Date()): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso) // ровно четыре цифры года: 20000 или 0007 за дату рождения не считаем
  if (!match) return 'validation.birthDateInvalid'
  const [y, m, d] = [Number(match[1]), Number(match[2]), Number(match[3])]
  if (y < MIN_BIRTH_YEAR || m < 1 || m > 12 || d < 1 || d > daysInMonth(y, m)) return 'validation.birthDateInvalid'
  const [ny, nm, nd] = [today.getFullYear(), today.getMonth() + 1, today.getDate()]
  if (y > ny || (y === ny && (m > nm || (m === nm && d > nd)))) return 'validation.futureDate'
  return ageFromBirthDate(iso, today) >= minAge ? null : 'validation.underage'
}

// Возраст по дате рождения (строка YYYY-MM-DD). Окончательную проверку делает сервер.
export function ageFromBirthDate(iso: string, today = new Date()): number {
  const [y, m, d] = iso.split('-').map(Number)
  let age = today.getFullYear() - y
  const hadBirthday =
    today.getMonth() + 1 > m || (today.getMonth() + 1 === m && today.getDate() >= d)
  if (!hadBirthday) age -= 1
  return age
}

// «UTC+5» для часового пояса IANA (Asia/Dushanbe → UTC+5)
export function utcOffsetLabel(timeZone: string): string {
  try {
    const options = { timeZone, timeZoneName: 'shortOffset' } as unknown as Intl.DateTimeFormatOptions
    const part = new Intl.DateTimeFormat('en', options)
      .formatToParts(new Date())
      .find((p) => p.type === 'timeZoneName')?.value
    return (part ?? '').replace('GMT', 'UTC') || 'UTC'
  } catch {
    return timeZone
  }
}

// Время в часовом поясе партнёра («17:00»)
export function formatInZone(iso: string, timeZone: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', timeZone }).format(new Date(iso))
}

// «вторник, 18 ноября»
export function formatDay(iso: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, { weekday: 'long', day: 'numeric', month: 'long' }).format(new Date(iso))
}

export function dayKey(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`
}

export function isSameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

// Время в часовом поясе пользователя (ISO из UTC → локальное «17:00»)
export function formatTime(iso: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit' }).format(new Date(iso))
}

export function formatDateTime(iso: string, lang: string): string {
  return new Intl.DateTimeFormat(lang, {
    day: 'numeric',
    month: 'long',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(iso))
}

// Вход в комнату урока открыт с −10 минут до старта и до старта + 75 минут (ТЗ, 7.2: окно 85 минут).
// Это только подсказка для кнопки: настоящую проверку делает сервер при выдаче join-token.
export function isJoinWindowOpen(startIso: string, now = new Date()): boolean {
  const start = new Date(startIso).getTime()
  return now.getTime() >= start - 10 * 60_000 && now.getTime() <= start + 75 * 60_000
}

// Когда откроется вход в комнату (за 10 минут до старта)
export function joinOpensAtIso(startIso: string): string {
  return new Date(new Date(startIso).getTime() - 10 * 60_000).toISOString()
}

// Сегодняшняя дата в формате для <input type="date" max="...">
export function todayIso(): string {
  const now = new Date()
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}
