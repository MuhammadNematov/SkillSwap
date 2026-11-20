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
