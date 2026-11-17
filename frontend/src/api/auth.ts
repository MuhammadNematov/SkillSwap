import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import { mockLogin, mockLoginTwoFactor, mockRegister } from './mocks/auth'
import type { AuthSession, CurrentUser, UiLanguage } from './types'

export interface LoginRequest {
  email: string
  password: string
}

// По ТЗ (FR-6): /auth/login возвращает requires2fa, затем /auth/login/2fa с кодом.
// challengeToken — временный токен между шагами; нужен ли он, уточним по Swagger.
export type LoginResult =
  | { requires2fa: false; session: AuthSession }
  | { requires2fa: true; challengeToken?: string }

export interface TwoFactorRequest {
  code: string
  isRecoveryCode: boolean
  challengeToken?: string
}

export interface RegisterRequest {
  name: string
  email: string
  password: string
  dateOfBirth: string // YYYY-MM-DD
  uiLanguage: UiLanguage
  acceptTerms: boolean
  consentModeratorAccess: boolean
}

// Ответ настоящего бэкенда на /auth/login (форму уточним по Swagger)
interface LoginResponse {
  requires2fa: boolean
  challengeToken?: string
  accessToken?: string
  user?: CurrentUser
}

export async function login(req: LoginRequest): Promise<LoginResult> {
  if (USE_MOCKS) return mockLogin(req)
  try {
    const { data } = await api.post<LoginResponse>('/auth/login', req)
    if (data.requires2fa) return { requires2fa: true, challengeToken: data.challengeToken }
    return { requires2fa: false, session: { accessToken: data.accessToken!, user: data.user! } }
  } catch (e) {
    throw toApiError(e)
  }
}

export async function loginTwoFactor(req: TwoFactorRequest): Promise<AuthSession> {
  if (USE_MOCKS) return mockLoginTwoFactor(req)
  try {
    const { data } = await api.post<AuthSession>('/auth/login/2fa', req)
    return data
  } catch (e) {
    throw toApiError(e)
  }
}

export async function register(req: RegisterRequest): Promise<void> {
  if (USE_MOCKS) return mockRegister(req)
  try {
    await api.post('/auth/register', req)
  } catch (e) {
    throw toApiError(e)
  }
}

// Восстановление сеанса при открытии сайта: refresh-токен в cookie → новый access-токен.
// На заглушках сеанса после перезагрузки нет (как будто cookie не было).
export async function refreshSession(): Promise<AuthSession | null> {
  if (USE_MOCKS) return null
  const { data } = await api.post<AuthSession>('/auth/refresh')
  return data
}

export async function logoutRequest(): Promise<void> {
  if (USE_MOCKS) return
  await api.post('/auth/logout')
}
