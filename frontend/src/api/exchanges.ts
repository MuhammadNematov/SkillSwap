import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import * as m from './mocks/exchanges'
import type { ChatMessage, Conversation, Exchange, ExchangeRequest, LessonSession } from './types'

// Все эндпоинты — из раздела 12 ТЗ. Формы ответов угаданы, сверим по Swagger.
async function http<T>(promise: Promise<{ data: T }>): Promise<T> {
  try {
    return (await promise).data
  } catch (e) {
    throw toApiError(e)
  }
}

// ───────── Обмены ─────────
export const getExchanges = (): Promise<Exchange[]> =>
  USE_MOCKS ? m.mockGetExchanges() : http(api.get<Exchange[]>('/exchanges'))

export const getExchange = (id: string): Promise<Exchange> =>
  USE_MOCKS ? m.mockGetExchange(id) : http(api.get<Exchange>(`/exchanges/${id}`))

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
export const getExchangeRequests = (box: 'in' | 'out'): Promise<ExchangeRequest[]> =>
  USE_MOCKS ? m.mockGetRequests(box) : http(api.get<ExchangeRequest[]>('/exchange-requests', { params: { box } }))

// Возвращает id созданного обмена
export const acceptRequest = async (id: string): Promise<string> => {
  if (USE_MOCKS) return m.mockAcceptRequest(id)
  const data = await http(api.post<{ exchangeId: string }>(`/exchange-requests/${id}/accept`))
  return data.exchangeId
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
export const getSessions = (exchangeId: string): Promise<LessonSession[]> =>
  USE_MOCKS ? m.mockGetSessions(exchangeId) : http(api.get<LessonSession[]>(`/exchanges/${exchangeId}/sessions`))

// Календарь: GET /me/sessions?from=&to=
export const getMySessions = (): Promise<LessonSession[]> =>
  USE_MOCKS ? m.mockGetMySessions() : http(api.get<LessonSession[]>('/me/sessions'))

export interface ProposeSessionInput {
  exchangeId: string
  startAt: string // UTC
  teacherIsMe: boolean
  topic?: string
}

export const proposeSession = async ({ exchangeId, ...body }: ProposeSessionInput): Promise<void> => {
  if (USE_MOCKS) {
    await m.mockProposeSession(exchangeId, body)
    return
  }
  await http(api.post(`/exchanges/${exchangeId}/sessions`, body))
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

// ───────── Чат ─────────
// История — обычным запросом (курсор before). Отправка по ТЗ идёт через SignalR-хаб /hubs/chat:
// когда будет бэкенд, добавим хук useChat, а здесь пока только заглушки.
export const getMessages = (exchangeId: string): Promise<ChatMessage[]> =>
  USE_MOCKS ? m.mockGetMessages(exchangeId) : http(api.get<ChatMessage[]>(`/exchanges/${exchangeId}/messages`))

export const sendMessage = async ({ exchangeId, text }: { exchangeId: string; text: string }): Promise<void> => {
  if (USE_MOCKS) return m.mockSendMessage(exchangeId, text)
  throw new ApiError('NOT_IMPLEMENTED', 501)
}

export const sendFile = async ({ exchangeId, file }: { exchangeId: string; file: File }): Promise<void> => {
  if (USE_MOCKS) return m.mockSendFile(exchangeId, file)
  const form = new FormData()
  form.append('file', file)
  await http(api.post(`/exchanges/${exchangeId}/attachments`, form))
}

// Список чатов: отдельного эндпоинта в ТЗ нет, собираем из /exchanges
export const getConversations = async (): Promise<Conversation[]> => {
  if (USE_MOCKS) return m.mockGetConversations()
  const exchanges = await getExchanges()
  return exchanges.map((exchange) => ({ exchange }))
}
