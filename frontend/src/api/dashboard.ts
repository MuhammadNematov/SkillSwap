// Главная: GET /me/dashboard (счётчики, ближайший урок, что ждёт ответа) + GET /matches для рекомендаций.
// В моках считаем по тем же данным, что страницы обменов и календаря.

import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import { getMatches } from './matches'
import { db } from './mocks/exchanges'
import { ME, PEOPLE } from './mocks/people'

export interface DashboardLesson {
  id: string
  title: string
  startsAt: string // UTC, ISO 8601
  partnerName: string
}

export interface RecommendedPartner {
  id: string
  name: string
  skills: string[]
  rating: number
  online: boolean
}

export type ActionType = 'AnswerRequest' | 'AnswerSession' | 'ConfirmSession' | 'FinishExchange' | 'AnswerExtension' | 'WriteReview'
export interface DashboardAction {
  type: ActionType
  exchangeId: string | null
  requestId: string | null
  dueAt: string | null
}

export interface DashboardData {
  activeExchanges: number
  upcomingLessons: number
  rating: number | null // null у «Новичка»
  unreadMessages: number
  actions: DashboardAction[]
  nextLesson: DashboardLesson | null
  recommended: RecommendedPartner[]
}

const sleep = (ms = 350) => new Promise((r) => setTimeout(r, ms))

interface DashboardDto {
  nextSession: {
    id: string
    exchangeId: string
    startAt: string
    topic: string | null
    partner: { name: string }
  } | null
  counters: { activeExchanges: number; unreadMessages: number }
  rating: { average: number | null }
  actionsNeeded: { type: ActionType; exchangeId: string | null; requestId: string | null; dueAt: string | null }[]
}

export async function getDashboard(): Promise<DashboardData> {
  if (!USE_MOCKS) {
    let dto: DashboardDto
    try {
      dto = (await api.get<DashboardDto>('/me/dashboard')).data
    } catch (e) {
      throw toApiError(e)
    }
    // Рекомендации — первые из подбора; если анкета не заполнена (403), просто пустой список
    const matches = await getMatches({}).catch(() => [])
    const sessions = await api
      .get<{ items?: unknown[] } | unknown[]>('/me/sessions', { params: { from: new Date().toISOString() } })
      .then((r) => (Array.isArray(r.data) ? r.data.length : (r.data.items?.length ?? 0)))
      .catch(() => 0)
    const n = dto.nextSession
    return {
      activeExchanges: dto.counters.activeExchanges,
      upcomingLessons: sessions,
      rating: dto.rating.average,
      unreadMessages: dto.counters.unreadMessages,
      actions: dto.actionsNeeded,
      nextLesson: n ? { id: n.id, title: n.topic ?? n.partner.name, startsAt: n.startAt, partnerName: n.partner.name } : null,
      recommended: matches.slice(0, 3).map((m) => ({
        id: m.user.id,
        name: m.user.name,
        skills: m.teaches.map((x) => x.name),
        rating: m.user.ratingAvg ?? 0,
        online: false,
      })),
    }
  }
  await sleep()
  // MOCK: считаем по тем же данным, что и страницы обменов и календаря
  const upcoming = db.sessions
    .filter((s) => s.status === 'Scheduled' && new Date(s.startAt).getTime() > Date.now())
    .sort((a, b) => a.startAt.localeCompare(b.startAt))
  const next = upcoming[0]

  return {
    activeExchanges: db.exchanges.filter((e) => e.status === 'Active').length,
    upcomingLessons: upcoming.length,
    rating: ME.ratingAvg,
    unreadMessages: 3,
    actions: [
      { type: 'AnswerRequest', exchangeId: null, requestId: 'r1', dueAt: new Date(Date.now() + 36e5 * 20).toISOString() },
      { type: 'ConfirmSession', exchangeId: 'e1', requestId: null, dueAt: new Date(Date.now() + 36e5 * 5).toISOString() },
    ],
    nextLesson: next
      ? { id: next.id, title: next.topic ?? next.partnerName, startsAt: next.startAt, partnerName: next.partnerName }
      : null,
    // id совпадают с mocks/people.ts, чтобы открывался профиль
    recommended: ['u2', 'u1', 'u7'].map((id) => {
      const p = PEOPLE.find((x) => x.id === id)!
      return { id: p.id, name: p.name, skills: p.teach.map((s) => s.name), rating: p.ratingAvg, online: id === 'u2' }
    }),
  }
}
