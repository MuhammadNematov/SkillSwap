import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import * as m from './mocks/exchanges'
import i18n from '../i18n'
import { getMyId } from './identity'
import { getChatHub, startHub } from './realtime'
import { getUserProfile } from './users'
import { pickName } from '../utils/localized'
import type { ChatMessage, Conversation, Exchange, ExchangeRequest, ExchangeStatus, LessonSession, RequestStatus, SessionStatus } from './types'

// ───────── DTO бэкенда и адаптеры ─────────
interface SkillDto {
  id: string
  nameTg: string
  nameRu: string
  nameEn: string
}
interface PersonDto {
  id: string
  name: string
  avatarUrl: string | null
}
interface RequestDto {
  id: string
  direction: 'in' | 'out'
  status: RequestStatus
  fromUser: PersonDto
  toUser: PersonDto
  fromTeachesSkill: SkillDto | null
  toTeachesSkill: SkillDto
  durationDays: number
  message: string | null
  expiresAt: string
  exchangeId: string | null
  createdAt: string
}
interface ExchangeDto {
  id: string
  status: ExchangeStatus
  partner: PersonDto
  iSkillTeach: SkillDto
  iSkillLearn: SkillDto
  startDate: string
  endDate: string
  isExtended: boolean
  completedByMe: boolean
  completedByPartner: boolean
  myExtensionAccepted: boolean
  partnerExtensionAccepted: boolean
  frozenAt: string | null
  abortedBy: 'me' | 'partner' | null
  abortReason: string | null
  closedAt: string | null
  balance: { myLessons: number; partnerLessons: number; showReminder: boolean }
  chat: { mode: 'ReadWrite' | 'ReadOnly' | 'Archived'; readOnlyReason: string | null; readableUntil: string | null }
  unreadMessages: number
}
interface Page<T> {
  items: T[]
  totalCount: number
}

const skillName = (s: SkillDto) => pickName({ tg: s.nameTg, ru: s.nameRu, en: s.nameEn }, i18n.resolvedLanguage ?? 'ru')
// В заявках и обменах бэкенд отдаёт только id, имя и аватар. Остальное у партнёра подтянет профиль.
const person = (p: PersonDto) => ({ id: p.id, name: p.name, avatarUrl: p.avatarUrl, ratingAvg: 0, ratingCount: 0, languages: [], timeZone: '' })

function toRequest(r: RequestDto): ExchangeRequest {
  const out = r.direction === 'out'
  const mine = out ? r.fromTeachesSkill : r.toTeachesSkill // чему учу я
  const theirs = out ? r.toTeachesSkill : r.fromTeachesSkill // чему учит партнёр
  return {
    id: r.id,
    direction: r.direction,
    partner: person(out ? r.toUser : r.fromUser),
    mySkill: mine ? skillName(mine) : undefined,
    partnerSkill: theirs ? skillName(theirs) : '',
    needsPartnerSkill: !theirs,
    durationDays: r.durationDays as 30 | 60,
    message: r.message ?? '',
    status: r.status,
    createdAt: r.createdAt,
    expiresAt: r.expiresAt,
    exchangeId: r.exchangeId,
  }
}

function toExchange(e: ExchangeDto): Exchange {
  return {
    id: e.id,
    partner: person(e.partner),
    mySkill: skillName(e.iSkillTeach),
    partnerSkill: skillName(e.iSkillLearn),
    startDate: e.startDate,
    endDate: e.endDate,
    status: e.status,
    isExtended: e.isExtended,
    myLessons: e.balance.myLessons,
    partnerLessons: e.balance.partnerLessons,
    showBalanceReminder: e.balance.showReminder,
    completedByMe: e.completedByMe,
    completedByPartner: e.completedByPartner,
    myExtensionAccepted: e.myExtensionAccepted,
    partnerExtensionAccepted: e.partnerExtensionAccepted,
    abortReason: e.abortReason ?? undefined,
    abortedBy: e.abortedBy,
    closedAt: e.closedAt,
    frozenAt: e.frozenAt,
    unreadMessages: e.unreadMessages,
    chat: e.chat,
  }
}

// Все эндпоинты — из раздела 12 ТЗ. Формы ответов угаданы, сверим по Swagger.
async function http<T>(promise: Promise<{ data: T }>): Promise<T> {
  try {
    return (await promise).data
  } catch (e) {
    throw toApiError(e)
  }
}

// ───────── Обмены ─────────
// Бэкенд отдаёт страницами до 50. Первой страницы хватает для списка; если обменов больше, добавим подгрузку.
export const getExchanges = async (): Promise<Exchange[]> => {
  if (USE_MOCKS) return m.mockGetExchanges()
  const data = await http(api.get<Page<ExchangeDto>>('/exchanges', { params: { page: 1, pageSize: 50 } }))
  return data.items.map(toExchange)
}

export const getExchange = async (id: string): Promise<Exchange> => {
  if (USE_MOCKS) return m.mockGetExchange(id)
  return toExchange(await http(api.get<ExchangeDto>(`/exchanges/${id}`)))
}

export const completeExchange = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockCompleteExchange(id)
  await http(api.post(`/exchanges/${id}/complete`))
}

export const abortExchange = async ({ id, reason }: { id: string; reason: string }): Promise<void> => {
  if (USE_MOCKS) return m.mockAbortExchange(id, reason)
  await http(api.post(`/exchanges/${id}/abort`, { reason }))
}

export const extendExchange = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockExtendExchange(id)
  await http(api.post(`/exchanges/${id}/extend`))
}

// ───────── Заявки ─────────
export const getExchangeRequests = async (box: 'in' | 'out'): Promise<ExchangeRequest[]> => {
  if (USE_MOCKS) return m.mockGetRequests(box)
  const data = await http(api.get<Page<RequestDto>>('/exchange-requests', { params: { box, page: 1, pageSize: 50 } }))
  return data.items.map(toRequest)
}

// Возвращает id созданного обмена. fromTeachesSkillId обязателен, если отправитель не указал свой навык.
export const acceptRequest = async ({ id, fromTeachesSkillId }: { id: string; fromTeachesSkillId?: string }): Promise<string> => {
  if (USE_MOCKS) return m.mockAcceptRequest(id)
  const data = await http(api.post<RequestDto>(`/exchange-requests/${id}/accept`, { fromTeachesSkillId: fromTeachesSkillId ?? null }))
  return data.exchangeId ?? ''
}

export const declineRequest = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockResolveRequest(id, 'Declined')
  await http(api.post(`/exchange-requests/${id}/decline`))
}

export const withdrawRequest = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockResolveRequest(id, 'Withdrawn')
  await http(api.post(`/exchange-requests/${id}/withdraw`))
}

// ───────── Уроки ─────────
interface SessionDto {
  id: string
  exchangeId: string
  teacherId: string
  startAt: string
  endAt: string
  durationMinutes: number
  topic: string | null
  status: SessionStatus
  proposedById: string
  previousStartAt: string | null
  confirmedBy: string[]
  confirmDeadlineAt: string | null
}
interface Partner {
  name: string
  timeZone: string
}

function toSession(d: SessionDto, partner: Partner): LessonSession {
  const me = getMyId()
  return {
    id: d.id,
    exchangeId: d.exchangeId,
    partnerName: partner.name,
    partnerTimeZone: partner.timeZone,
    teacherIsMe: d.teacherId === me,
    startAt: d.startAt,
    endAt: d.endAt,
    durationMin: d.durationMinutes,
    topic: d.topic ?? undefined,
    status: d.status,
    proposedByMe: d.proposedById === me,
    confirmedByMe: d.confirmedBy.includes(me),
    confirmDeadlineAt: d.confirmDeadlineAt,
    previousStartAt: d.previousStartAt,
  }
}

// Имя и часовой пояс партнёра берём из профиля: в уроках бэкенд их не отдаёт
async function partnerInfo(userId: string, name?: string): Promise<Partner> {
  try {
    const p = await getUserProfile(userId)
    return { name: name ?? p.name, timeZone: p.timeZone }
  } catch {
    return { name: name ?? '', timeZone: 'UTC' }
  }
}

export const getSessions = async (exchangeId: string): Promise<LessonSession[]> => {
  if (USE_MOCKS) return m.mockGetSessions(exchangeId)
  const [list, exchange] = await Promise.all([
    http(api.get<SessionDto[]>(`/exchanges/${exchangeId}/sessions`)),
    http(api.get<ExchangeDto>(`/exchanges/${exchangeId}`)),
  ])
  const partner = await partnerInfo(exchange.partner.id, exchange.partner.name)
  return list.map((d) => toSession(d, partner))
}

// Календарь: GET /me/sessions?from=&to=&includeClosed= (окно не больше 100 дней)
export const getMySessions = async (): Promise<LessonSession[]> => {
  if (USE_MOCKS) return m.mockGetMySessions()
  const from = new Date(Date.now() - 30 * 86_400_000).toISOString()
  const to = new Date(Date.now() + 60 * 86_400_000).toISOString()
  const list = await http(
    api.get<{ session: SessionDto; partner: PersonDto }[]>('/me/sessions', { params: { from, to, includeClosed: true } }),
  )
  const ids = [...new Set(list.map((x) => x.partner.id))]
  const infos = new Map(await Promise.all(ids.map(async (id) => [id, await partnerInfo(id, list.find((x) => x.partner.id === id)!.partner.name)] as const)))
  return list.map((x) => toSession(x.session, infos.get(x.partner.id)!))
}

export interface ProposeSessionInput {
  exchangeId: string
  partnerId: string
  startAt: string // UTC
  teacherIsMe: boolean
  topic?: string
}

export const proposeSession = async ({ exchangeId, partnerId, ...body }: ProposeSessionInput): Promise<void> => {
  if (USE_MOCKS) {
    await m.mockProposeSession(exchangeId, body)
    return
  }
  await http(api.post(`/exchanges/${exchangeId}/sessions`, { startAt: body.startAt, teacherId: body.teacherIsMe ? getMyId() : partnerId, topic: body.topic ?? null }))
}

export const confirmSession = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockConfirmSession(id)
  await http(api.post(`/sessions/${id}/confirm`))
}

export const declineSession = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockDeclineSession(id)
  await http(api.post(`/sessions/${id}/decline`))
}

export const cancelSession = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockCancelSession(id)
  await http(api.post(`/sessions/${id}/cancel`))
}

// Перенос: новое время уходит партнёру как предложение (ограничения проверяет сервер)
export const rescheduleSession = async ({ id, startAt }: { id: string; startAt: string }): Promise<void> => {
  if (USE_MOCKS) return m.mockRescheduleSession(id, startAt)
  await http(api.post(`/sessions/${id}/reschedule`, { startAt }))
}

// «Урок состоялся»: после планового окончания, пока нет видеозвонков
export const confirmSessionManual = async (id: string): Promise<void> => {
  if (USE_MOCKS) return m.mockConfirmManual(id)
  await http(api.post(`/sessions/${id}/confirm-manual`))
}

// ───────── Чат ─────────
interface MessageDto {
  id: string
  exchangeId: string
  senderId: string | null
  type: ChatMessage['type']
  text: string | null
  paymentWarning: boolean
  attachmentId: string | null
  sessionId: string | null
  createdAt: string
  deletedAt: string | null
  readAt: string | null
  attachment?: { fileName: string; durationSeconds: number | null } | null
}

export function toMessage(d: MessageDto): ChatMessage {
  return {
    id: d.id,
    exchangeId: d.exchangeId,
    senderIsMe: d.senderId === getMyId(),
    type: d.type,
    text: d.text ?? undefined,
    createdAt: d.createdAt,
    sessionId: d.sessionId ?? undefined,
    attachmentId: d.attachmentId ?? undefined,
    fileName: d.attachment?.fileName,
    durationSec: d.attachment?.durationSeconds ?? undefined,
    paymentWarning: d.paymentWarning,
    readAt: d.readAt,
    deleted: !!d.deletedAt,
  }
}

// История — обычным запросом: последние 100 сообщений по возрастанию времени.
// Отправка и события в реальном времени идут через SignalR-хаб /hubs/chat (нужна библиотека @microsoft/signalr).
export const getMessages = async (exchangeId: string): Promise<ChatMessage[]> => {
  if (USE_MOCKS) return m.mockGetMessages(exchangeId)
  const data = await http(api.get<{ items: MessageDto[] }>(`/exchanges/${exchangeId}/messages`, { params: { limit: 100 } }))
  return data.items.map(toMessage)
}

// Код ошибки хаба лежит в тексте исключения: "...chat_read_only..."
function hubError(e: unknown): ApiError {
  const msg = e instanceof Error ? e.message : ''
  const m = /(chat_read_only|chat_archived|chat_rate_limited|message_empty|message_too_long|exchange_not_found|message_not_found|not_message_author|edit_window_expired|message_deleted)/.exec(msg)
  return new ApiError(m ? m[1] : 'NETWORK_ERROR', m ? 400 : 0)
}

async function hubInvoke<T>(method: string, ...args: unknown[]): Promise<T> {
  const hub = getChatHub()
  if (!(await startHub(hub))) throw new ApiError('NETWORK_ERROR', 0)
  try {
    return await hub.invoke<T>(method, ...args)
  } catch (e) {
    throw hubError(e)
  }
}

export const sendMessage = async ({ exchangeId, text }: { exchangeId: string; text: string }): Promise<ChatMessage> => {
  if (USE_MOCKS) {
    await m.mockSendMessage(exchangeId, text)
    return (await m.mockGetMessages(exchangeId)).at(-1) as ChatMessage
  }
  return toMessage(await hubInvoke<MessageDto>('Send', exchangeId, text))
}

export const sendTyping = (exchangeId: string): void => {
  if (USE_MOCKS) return
  void hubInvoke('Typing', exchangeId).catch(() => undefined)
}

// POST /exchanges/{id}/messages/read — «прочитано» собеседнику
export const markMessagesRead = async (exchangeId: string): Promise<void> => {
  if (USE_MOCKS) return
  await http(api.post(`/exchanges/${exchangeId}/messages/read`))
}

export const sendFile = async ({ exchangeId, file }: { exchangeId: string; file: File }): Promise<void> => {
  if (USE_MOCKS) return m.mockSendFile(exchangeId, file)
  const form = new FormData()
  form.append('file', file)
  await http(api.post(`/exchanges/${exchangeId}/attachments`, form))
}

// Голосовое: multipart, поля file (.webm, Opus, до 4 МБ) и durationSeconds (1–120). Ответ 201 — готовое сообщение.
export const sendVoice = async ({ exchangeId, blob, durationSec }: { exchangeId: string; blob: Blob; durationSec: number }): Promise<void> => {
  if (USE_MOCKS) return m.mockSendVoice(exchangeId, durationSec, blob)
  const form = new FormData()
  form.append('file', blob, 'voice.webm')
  form.append('durationSeconds', String(Math.min(120, Math.max(1, Math.round(durationSec)))))
  await http(api.post(`/exchanges/${exchangeId}/attachments`, form))
}

// Список чатов: отдельного эндпоинта в ТЗ нет, собираем из /exchanges
export const getConversations = async (): Promise<Conversation[]> => {
  if (USE_MOCKS) return m.mockGetConversations()
  const exchanges = await getExchanges()
  return exchanges.map((exchange) => ({ exchange }))
}
