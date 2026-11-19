import { ApiError } from '../errors'
import type {
  ChatMessage,
  Conversation,
  Exchange,
  ExchangeRequest,
  LessonSession,
  MessageType,
  SessionStatus,
  UserSummary,
} from '../types'
import { PEOPLE } from './people'

// ───────── Тестовые данные обменов, заявок, уроков и чата. Удалим, когда бэкенд будет готов. ─────────
// Данные хранятся в памяти и меняются действиями (принять заявку, отменить урок…). После перезагрузки страницы сбрасываются.

const HOUR = 3_600_000
const DAY = 24 * HOUR
const iso = (ms: number) => new Date(ms).toISOString()
const hourFromNow = (h: number) => {
  const d = new Date(Date.now() + h * HOUR)
  d.setMinutes(0, 0, 0)
  return d.getTime()
}
const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))
const clone = <T>(x: T): T => structuredClone(x)

let seq = 100
const nextId = (prefix: string) => `${prefix}${++seq}`

function summary(id: string): UserSummary {
  const p = PEOPLE.find((x) => x.id === id)!
  return {
    id: p.id,
    name: p.name,
    avatarUrl: p.avatarUrl,
    ratingAvg: p.ratingAvg,
    ratingCount: p.ratingCount,
    languages: p.languages,
    timeZone: p.timeZone,
  }
}

function find<T extends { id: string }>(list: T[], id: string): T {
  const item = list.find((i) => i.id === id)
  if (!item) throw new ApiError('NOT_FOUND', 404)
  return item
}

const exchanges: Exchange[] = [
  {
    id: 'e1',
    partner: summary('u1'),
    mySkill: 'C#',
    partnerSkill: 'English',
    startDate: iso(Date.now() - 12 * DAY),
    endDate: iso(Date.now() + 18 * DAY),
    status: 'Active',
    isExtended: false,
    myLessons: 3,
    partnerLessons: 2,
    completedByMe: false,
    completedByPartner: false,
  },
  {
    id: 'e2',
    partner: summary('u2'),
    mySkill: 'Python',
    partnerSkill: 'Figma',
    startDate: iso(Date.now() - 5 * DAY),
    endDate: iso(Date.now() + 55 * DAY),
    status: 'Active',
    isExtended: false,
    myLessons: 1,
    partnerLessons: 1,
    completedByMe: false,
    completedByPartner: false,
  },
  {
    id: 'e3',
    partner: summary('u3'),
    mySkill: 'SQL',
    partnerSkill: 'English',
    startDate: iso(Date.now() - 45 * DAY),
    endDate: iso(Date.now() - 15 * DAY),
    status: 'Completed',
    isExtended: false,
    myLessons: 4,
    partnerLessons: 3,
    completedByMe: true,
    completedByPartner: true,
    review: { canWrite: true, submitted: false, deadlineAt: iso(Date.now() + 10 * DAY) },
  },
]

const requests: ExchangeRequest[] = [
  {
    id: 'r1',
    direction: 'in',
    partner: summary('u7'),
    mySkill: 'Python',
    partnerSkill: 'Figma',
    durationDays: 30,
    message: 'Привет! Хочу научиться Python, могу научить Figma и основам дизайна.',
    status: 'Pending',
    createdAt: iso(Date.now() - 1 * DAY),
    expiresAt: iso(Date.now() + 6 * DAY),
  },
  {
    id: 'r2',
    direction: 'out',
    partner: summary('u4'),
    partnerSkill: 'English',
    durationDays: 30,
    message: 'Хочу практиковать английский. Свой навык предложу в чате.',
    status: 'Pending',
    createdAt: iso(Date.now() - 2 * DAY),
    expiresAt: iso(Date.now() + 5 * DAY),
  },
  {
    id: 'r3',
    direction: 'out',
    partner: summary('u6'),
    mySkill: 'C#',
    partnerSkill: 'English',
    durationDays: 60,
    message: 'Давай обменяемся знаниями на два месяца.',
    status: 'Declined',
    createdAt: iso(Date.now() - 9 * DAY),
    expiresAt: iso(Date.now() - 2 * DAY),
  },
]

function mkSession(
  id: string,
  exchangeId: string,
  teacherIsMe: boolean,
  startMs: number,
  status: SessionStatus,
  proposedByMe: boolean,
  topic?: string,
): LessonSession {
  const ex = exchanges.find((e) => e.id === exchangeId)!
  return {
    id,
    exchangeId,
    partnerName: ex.partner.name,
    partnerTimeZone: ex.partner.timeZone,
    teacherIsMe,
    startAt: iso(startMs),
    durationMin: 45,
    topic,
    status,
    proposedByMe,
  }
}

const sessions: LessonSession[] = [
  mkSession('s1', 'e1', false, hourFromNow(3), 'Scheduled', true, 'English для начинающих'),
  mkSession('s2', 'e1', true, hourFromNow(27), 'Scheduled', false, 'ASP.NET Core: контроллеры'),
  mkSession('s3', 'e1', true, hourFromNow(-72), 'Completed', true, 'Основы C#'),
  mkSession('s4', 'e1', false, hourFromNow(50), 'Proposed', false, 'Разговорная практика'),
  mkSession('s5', 'e2', false, hourFromNow(75), 'Scheduled', false, 'Figma: компоненты'),
  mkSession('s6', 'e1', false, hourFromNow(-144), 'Completed', false, 'Алфавит и произношение'),
]

function mkMessage(
  exchangeId: string,
  senderIsMe: boolean,
  type: MessageType,
  atMs: number,
  extra: Partial<ChatMessage> = {},
): ChatMessage {
  return { id: nextId('m'), exchangeId, senderIsMe, type, createdAt: iso(atMs), ...extra }
}

const messages: ChatMessage[] = [
  mkMessage('e1', false, 'System', Date.now() - 12 * DAY, { text: 'Обмен начался' }),
  mkMessage('e1', false, 'Text', Date.now() - 12 * DAY + HOUR, { text: 'Привет! Рада знакомству. Давай договоримся о первом уроке?' }),
  mkMessage('e1', true, 'Text', Date.now() - 12 * DAY + 2 * HOUR, { text: 'Привет! Давай, мне удобно по вечерам.' }),
  mkMessage('e1', true, 'SessionCard', Date.now() - 2 * DAY, { sessionId: 's2' }),
  mkMessage('e1', false, 'Text', Date.now() - 2 * DAY + HOUR, { text: 'Отлично, подтверждаю.' }),
  mkMessage('e1', false, 'SessionCard', Date.now() - 3 * HOUR, { sessionId: 's4' }),
  mkMessage('e2', false, 'System', Date.now() - 5 * DAY, { text: 'Обмен начался' }),
  mkMessage('e2', false, 'Text', Date.now() - 5 * DAY + HOUR, { text: 'Привет! Начнём с основ Figma, а потом перейдём к Python?' }),
  mkMessage('e2', true, 'Text', Date.now() - 5 * DAY + 2 * HOUR, { text: 'Договорились.' }),
  mkMessage('e3', false, 'System', Date.now() - 15 * DAY, { text: 'Обмен завершён' }),
]

// Для панели «Главная»
export const db = { exchanges, requests, sessions, messages }

function addSystem(exchangeId: string, text: string) {
  messages.push(mkMessage(exchangeId, false, 'System', Date.now(), { text }))
}

// ───────── Обмены ─────────

export async function mockGetExchanges(): Promise<Exchange[]> {
  await sleep()
  const order: Record<string, number> = { Active: 0, Frozen: 1, Completed: 2, Aborted: 3 }
  return clone([...exchanges].sort((a, b) => order[a.status] - order[b.status]))
}

export async function mockGetExchange(id: string): Promise<Exchange> {
  await sleep()
  return clone(find(exchanges, id))
}

export async function mockCompleteExchange(id: string): Promise<void> {
  await sleep()
  const ex = find(exchanges, id)
  if (ex.status !== 'Active') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  ex.completedByMe = true
  if (ex.completedByPartner) ex.status = 'Completed'
}

export async function mockAbortExchange(id: string, reason: string): Promise<void> {
  await sleep()
  const ex = find(exchanges, id)
  if (ex.status !== 'Active' && ex.status !== 'Frozen') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  ex.status = 'Aborted'
  ex.abortReason = reason
  for (const s of sessions) {
    if (s.exchangeId === id && (s.status === 'Scheduled' || s.status === 'Proposed')) s.status = 'Cancelled'
  }
  addSystem(id, 'Обмен прерван')
}

export async function mockExtendExchange(id: string): Promise<void> {
  await sleep()
  const ex = find(exchanges, id)
  if (ex.status !== 'Active') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  if (ex.isExtended) throw new ApiError('ALREADY_EXTENDED', 409)
  ex.endDate = iso(new Date(ex.endDate).getTime() + 30 * DAY)
  ex.isExtended = true
  addSystem(id, 'Обмен продлён на 30 дней')
}

// ───────── Заявки ─────────

export async function mockGetRequests(box: 'in' | 'out'): Promise<ExchangeRequest[]> {
  await sleep()
  return clone(requests.filter((r) => r.direction === box).reverse())
}

export async function mockAcceptRequest(id: string): Promise<string> {
  await sleep()
  const r = find(requests, id)
  if (r.status !== 'Pending' || r.direction !== 'in') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  r.status = 'Accepted'
  const ex: Exchange = {
    id: nextId('e'),
    partner: r.partner,
    mySkill: r.mySkill ?? '—',
    partnerSkill: r.partnerSkill,
    startDate: iso(Date.now()),
    endDate: iso(Date.now() + r.durationDays * DAY),
    status: 'Active',
    isExtended: false,
    myLessons: 0,
    partnerLessons: 0,
    completedByMe: false,
    completedByPartner: false,
  }
  exchanges.push(ex)
  addSystem(ex.id, 'Обмен начался')
  return ex.id
}

export async function mockResolveRequest(id: string, status: 'Declined' | 'Withdrawn'): Promise<void> {
  await sleep()
  const r = find(requests, id)
  if (r.status !== 'Pending') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  r.status = status
}

// ───────── Уроки ─────────

export async function mockGetSessions(exchangeId: string): Promise<LessonSession[]> {
  await sleep(150)
  return clone(sessions.filter((s) => s.exchangeId === exchangeId).sort((a, b) => a.startAt.localeCompare(b.startAt)))
}

export async function mockGetMySessions(): Promise<LessonSession[]> {
  await sleep()
  return clone([...sessions].sort((a, b) => a.startAt.localeCompare(b.startAt)))
}

export async function mockProposeSession(
  exchangeId: string,
  input: { startAt: string; teacherIsMe: boolean; topic?: string },
): Promise<LessonSession> {
  await sleep()
  const ex = find(exchanges, exchangeId)
  if (ex.status !== 'Active') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  const start = new Date(input.startAt).getTime()
  if (start < Date.now() + HOUR) throw new ApiError('SESSION_TOO_SOON', 400)
  if (start > new Date(ex.endDate).getTime()) throw new ApiError('SESSION_AFTER_END', 400)
  const s = mkSession(nextId('s'), exchangeId, input.teacherIsMe, start, 'Proposed', true, input.topic || undefined)
  sessions.push(s)
  messages.push(mkMessage(exchangeId, true, 'SessionCard', Date.now(), { sessionId: s.id }))
  return clone(s)
}

export async function mockConfirmSession(id: string): Promise<void> {
  await sleep()
  const s = find(sessions, id)
  if (s.status !== 'Proposed' || s.proposedByMe) throw new ApiError('INVALID_STATE_TRANSITION', 409)
  const start = new Date(s.startAt).getTime()
  // Пересечение с другим уроком (в том числе в другом обмене) — как проверка с блокировкой на сервере
  const clash = sessions.some(
    (o) => o.id !== id && o.status === 'Scheduled' && Math.abs(new Date(o.startAt).getTime() - start) < 45 * 60_000,
  )
  if (clash) throw new ApiError('SESSION_CONFLICT', 409)
  s.status = 'Scheduled'
}

export async function mockDeclineSession(id: string): Promise<void> {
  await sleep()
  const s = find(sessions, id)
  if (s.status !== 'Proposed' || s.proposedByMe) throw new ApiError('INVALID_STATE_TRANSITION', 409)
  s.status = 'Declined'
}

export async function mockCancelSession(id: string): Promise<void> {
  await sleep()
  const s = find(sessions, id)
  if (s.status === 'Proposed' && s.proposedByMe) {
    s.status = 'Cancelled'
    return
  }
  if (s.status !== 'Scheduled') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  // Меньше 2 часов до начала — поздняя отмена (ТЗ 6.2)
  s.status = new Date(s.startAt).getTime() - Date.now() < 2 * HOUR ? 'LateCancelled' : 'Cancelled'
}

export async function mockConfirmManual(id: string): Promise<void> {
  await sleep()
  const s = find(sessions, id)
  if (s.status !== 'Unconfirmed') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  s.confirmedByMe = true
  s.status = 'Completed'
}

// ───────── Чат ─────────

export async function mockGetMessages(exchangeId: string): Promise<ChatMessage[]> {
  await sleep(150)
  return clone(messages.filter((m) => m.exchangeId === exchangeId).sort((a, b) => a.createdAt.localeCompare(b.createdAt)))
}

export async function mockSendMessage(exchangeId: string, text: string): Promise<void> {
  await sleep(100)
  if (find(exchanges, exchangeId).status !== 'Active') throw new ApiError('CHAT_READ_ONLY', 409)
  messages.push(mkMessage(exchangeId, true, 'Text', Date.now(), { text }))
}

const ALLOWED_EXT = ['pdf', 'docx', 'xlsx', 'pptx', 'zip', 'txt', 'png', 'jpg', 'webp']

// Белый список типов и лимит 20 МБ (ТЗ 7.1). Настоящую проверку сигнатур делает сервер.
export async function mockSendFile(exchangeId: string, file: { name: string; size: number }): Promise<void> {
  await sleep(300)
  if (find(exchanges, exchangeId).status !== 'Active') throw new ApiError('CHAT_READ_ONLY', 409)
  const ext = file.name.split('.').pop()?.toLowerCase() ?? ''
  if (!ALLOWED_EXT.includes(ext)) throw new ApiError('FILE_TYPE_NOT_ALLOWED', 400)
  if (file.size > 20 * 1024 * 1024) throw new ApiError('FILE_TOO_LARGE', 400)
  messages.push(mkMessage(exchangeId, true, 'File', Date.now(), { fileName: file.name }))
}

export async function mockSendVoice(exchangeId: string, durationSec: number): Promise<void> {
  await sleep(300)
  if (find(exchanges, exchangeId).status !== 'Active') throw new ApiError('CHAT_READ_ONLY', 409)
  messages.push(mkMessage(exchangeId, true, 'Voice', Date.now(), { durationSec: Math.round(durationSec), attachmentId: nextId('a') }))
}

export async function mockGetConversations(): Promise<Conversation[]> {
  await sleep()
  return clone(
    exchanges
      .map((exchange) => {
        const mine = messages.filter((m) => m.exchangeId === exchange.id)
        return { exchange, last: mine.sort((a, b) => a.createdAt.localeCompare(b.createdAt)).at(-1) }
      })
      .sort((a, b) => (b.last?.createdAt ?? '').localeCompare(a.last?.createdAt ?? '')),
  )
}

export async function mockRescheduleSession(id: string, startAt: string): Promise<void> {
  await sleep()
  const s = find(sessions, id)
  if (s.status !== 'Scheduled') throw new ApiError('INVALID_STATE_TRANSITION', 409)
  if (new Date(s.startAt).getTime() - Date.now() < 2 * HOUR) throw new ApiError('RESCHEDULE_TOO_LATE', 409)
  const start = new Date(startAt).getTime()
  if (start < Date.now() + HOUR) throw new ApiError('SESSION_TOO_SOON', 400)
  s.startAt = new Date(start).toISOString()
  s.status = 'Proposed'
  s.proposedByMe = true
}
