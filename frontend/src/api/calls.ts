import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'

// POST /sessions/{id}/join-token: данные для входа в видеокомнату LiveKit.
// Окно входа: открывается за 10 минут до начала и закрывается через 30 минут после планового окончания.
// Ошибки (окно не открыто, окно закрыто, обмен заморожен) приходят кодами, переводятся через errors.codes.*
export interface JoinToken {
  token: string
  url: string // wss-адрес LiveKit
  roomName: string
  startAt: string // UTC
  plannedEndAt: string // UTC
  roomClosesAt: string // UTC
}

export async function getJoinToken(sessionId: string): Promise<JoinToken> {
  if (USE_MOCKS) {
    await new Promise((r) => setTimeout(r, 500))
    const now = Date.now()
    return {
      token: 'mock-token',
      url: '',
      roomName: `mock-${sessionId}`,
      startAt: new Date(now - 60_000).toISOString(),
      plannedEndAt: new Date(now + 45 * 60_000).toISOString(),
      roomClosesAt: new Date(now + 75 * 60_000).toISOString(),
    }
  }
  try {
    return (await api.post<JoinToken>(`/sessions/${sessionId}/join-token`)).data
  } catch (e) {
    throw toApiError(e)
  }
}
