// В ТЗ (раздел 12) нет отдельного эндпоинта «главная». Когда бэкенд будет готов, соберём данные из
// GET /exchanges, GET /me/sessions?from=&to=, GET /matches и GET /me — или попросим у бэкендера
// один агрегирующий эндпоинт. Пока всегда возвращаем заглушку.

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
  // MOCK: урок «через пару часов», чтобы было видно кнопку «Присоединиться» в выключенном состоянии
  const start = new Date()
  start.setHours(start.getHours() + 2, 0, 0, 0)

  return {
    activeExchanges: 2,
    upcomingLessons: 1,
    rating: 4.8,
    nextLesson: {
      id: 'mock-lesson-1',
      title: 'C# для начинающих',
      startsAt: start.toISOString(),
      partnerName: 'Diyorbek',
    },
    recommended: [
      { id: 'p1', name: 'Diyorbek', skills: ['C#', 'Python'], rating: 4.9, online: true },
      { id: 'p2', name: 'Sabrina', skills: ['English', 'UI/UX'], rating: 4.8, online: false },
      { id: 'p3', name: 'Farrukh', skills: ['React', 'TypeScript'], rating: 4.7, online: false },
    ],
  }
}
