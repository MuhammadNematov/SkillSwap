// В ТЗ (раздел 12) нет отдельного эндпоинта «главная». Когда бэкенд будет готов, соберём данные из
// GET /exchanges, GET /me/sessions?from=&to=, GET /matches и GET /me — или попросим у бэкендера
// один агрегирующий эндпоинт. Пока всегда возвращаем заглушку.

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

export interface DashboardData {
  activeExchanges: number
  upcomingLessons: number
  rating: number
  nextLesson: DashboardLesson | null
  recommended: RecommendedPartner[]
}

const sleep = (ms = 350) => new Promise((r) => setTimeout(r, ms))

export async function getDashboard(): Promise<DashboardData> {
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
