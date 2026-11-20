import axios from 'axios'
import { toApiError } from './errors'

// Только Development-бэкенд: POST /dev/seed-lesson (вне /api/v1, без входа) создаёт двух пользователей,
// обмен и подтверждённый урок, который начнётся через 2 минуты. В продакшене маршрута нет.
export interface SeedUser {
  role: 'teacher' | 'student'
  userId: string
  email: string
  password: string
  name: string
}

export interface SeedLesson {
  exchangeId: string
  sessionId: string
  startAt: string
  joinOpensAt: string
  roomClosesAt: string
  users: SeedUser[]
}

export async function seedLesson(): Promise<SeedLesson> {
  try {
    return (await axios.post<SeedLesson>('/dev/seed-lesson')).data
  } catch (e) {
    throw toApiError(e)
  }
}
