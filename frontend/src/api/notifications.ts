import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import type { AppNotification } from './types'

const sleep = (ms = 200) => new Promise((r) => setTimeout(r, ms))
const ago = (min: number) => new Date(Date.now() - min * 60_000).toISOString()

// Тестовые уведомления (в памяти). Удалим, когда бэкенд будет готов.
let mock: AppNotification[] = [
  { id: 'n1', type: 'RequestReceived', payload: { partnerName: 'Фаррух' }, isRead: false, createdAt: ago(12) },
  { id: 'n2', type: 'SessionProposed', payload: { partnerName: 'Сабрина', exchangeId: 'e1', startAt: ago(-60 * 26) }, isRead: false, createdAt: ago(95) },
  { id: 'n3', type: 'ReviewAvailable', payload: { partnerName: 'Мадина', exchangeId: 'e3' }, isRead: true, createdAt: ago(60 * 30) },
]

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /notifications?unreadOnly=
export async function getNotifications(unreadOnly = false): Promise<AppNotification[]> {
  if (USE_MOCKS) {
    await sleep()
    return mock.filter((n) => !unreadOnly || !n.isRead)
  }
  return http(() => api.get<AppNotification[]>('/notifications', { params: { unreadOnly } }))
}

// POST /notifications/{id}/read
export async function markRead(id: string): Promise<void> {
  if (USE_MOCKS) {
    mock = mock.map((n) => (n.id === id ? { ...n, isRead: true } : n))
    return
  }
  await http(() => api.post(`/notifications/${id}/read`))
}

// POST /notifications/read-all
export async function markAllRead(): Promise<void> {
  if (USE_MOCKS) {
    mock = mock.map((n) => ({ ...n, isRead: true }))
    return
  }
  await http(() => api.post('/notifications/read-all'))
}
