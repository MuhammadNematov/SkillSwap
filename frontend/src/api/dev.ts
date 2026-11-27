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

// POST /dev/seed-staff: модераторы и админ с готовыми access-токенами (вход без пароля и 2FA).
// Точная форма ответа не описана, поэтому берём все объекты, где есть accessToken.
export interface SeedStaffAccount {
  accessToken: string
  label: string
  role: string
}

function collectTokens(node: unknown, out: Record<string, unknown>[]) {
  if (Array.isArray(node)) node.forEach((n) => collectTokens(n, out))
  else if (node && typeof node === 'object') {
    const o = node as Record<string, unknown>
    if (typeof o.accessToken === 'string') out.push(o)
    Object.values(o).forEach((v) => collectTokens(v, out))
  }
}

export async function seedStaff(): Promise<SeedStaffAccount[]> {
  try {
    const { data } = await axios.post<unknown>('/dev/seed-staff')
    const found: Record<string, unknown>[] = []
    collectTokens(data, found)
    return found.map((o) => {
      const label = String(o.email ?? o.name ?? o.role ?? 'staff')
      const role = String(o.role ?? (label.includes('admin') ? 'Admin' : 'Moderator'))
      return { accessToken: String(o.accessToken), label, role }
    })
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /dev/seed-case: обращение, доведённое до нужной стадии настоящими сервисами бэкенда
export type SeedCaseType = 'Conduct' | 'NoShowAbuse' | 'ReviewDispute'
export type SeedCaseStage = 'Open' | 'Taken' | 'Waiting' | 'Resolved' | 'Appealed' | 'AppealReview'

export interface SeedCaseResult {
  caseId: string
  caseUrl?: string
  users: { role?: string; email?: string; password?: string; name?: string }[]
}

export async function seedCase(body: { type: SeedCaseType; stage: SeedCaseStage }): Promise<SeedCaseResult> {
  try {
    const { data } = await axios.post<Record<string, unknown>>('/dev/seed-case', body)
    // пользователи могут лежать в users или в двух полях; берём всё, где есть почта
    const users: SeedCaseResult['users'] = []
    const walk = (n: unknown) => {
      if (Array.isArray(n)) n.forEach(walk)
      else if (n && typeof n === 'object') {
        const o = n as Record<string, unknown>
        if (typeof o.email === 'string' && typeof o.password === 'string') {
          users.push({ role: o.role as string | undefined, email: o.email, password: o.password, name: o.name as string | undefined })
        }
        Object.values(o).forEach(walk)
      }
    }
    walk(data)
    return { caseId: String(data.caseId ?? ''), caseUrl: typeof data.caseUrl === 'string' ? data.caseUrl : undefined, users }
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /dev/staff-code: текущий и следующий код 2FA служебного аккаунта без приложения-аутентификатора
export async function getStaffCode(email: string): Promise<{ codes: string[]; secondsLeft: number }> {
  try {
    const { data } = await axios.post<{ codes?: string[]; secondsLeft?: number }>('/dev/staff-code', { email })
    return { codes: data.codes ?? [], secondsLeft: data.secondsLeft ?? 0 }
  } catch (e) {
    throw toApiError(e)
  }
}

// Постоянные dev-user-1…3 (только Development и только с этого компьютера): POST /dev/seed-users создаёт или приводит их в порядок
// (профиль, навыки, языки, обмен user-1 ↔ user-2 с перепиской и уроком), POST /dev/login-user входит без пароля (refresh приходит в cookie, как при обычном входе),
// POST /dev/reset-lesson назначает урок user-1 и user-2 заново (начнётся примерно через минуту).
export const DEV_USER_EMAILS = ['dev-user-1@example.com', 'dev-user-2@example.com', 'dev-user-3@example.com'] as const

export interface DevUsers {
  users: { key: string; userId: string; email: string; name: string; password: string }[]
  exchange: { id: string; created: boolean; url: string }
  lesson: { sessionId: string; startAt: string; created: boolean } | null
  lessonNote?: string | null
  avatarNote?: string | null
}

export async function seedDevUsers(): Promise<DevUsers> {
  try {
    return (await axios.post<DevUsers>('/dev/seed-users')).data
  } catch (e) {
    throw toApiError(e)
  }
}

export async function loginDevUser(email: string): Promise<{ accessToken: string }> {
  try {
    return (await axios.post<{ accessToken: string }>('/dev/login-user', { email })).data
  } catch (e) {
    throw toApiError(e)
  }
}

export async function resetDevLesson(): Promise<{ lesson: { sessionId: string; startAt: string }; cancelled: number }> {
  try {
    return (await axios.post<{ lesson: { sessionId: string; startAt: string }; cancelled: number }>('/dev/reset-lesson')).data
  } catch (e) {
    throw toApiError(e)
  }
}
