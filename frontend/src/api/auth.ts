import { api, refreshAuth, setAccessToken } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import {
  mockConfirmEmailCode,
  mockForgotPassword,
  mockLogin,
  mockLoginTwoFactor,
  mockRegister,
  mockResendConfirmation,
  mockResetPassword,
} from './mocks/auth'
import { getMe, toCurrentUser } from './me'
import type { AuthSession, UiLanguage } from './types'

export interface LoginRequest {
  email: string
  password: string
  captchaToken?: string
}

// /auth/login: обычный вход, второй шаг 2FA (requires2fa) или обязательная настройка 2FA для служебных аккаунтов (requires2faSetup).
// В обоих «промежуточных» случаях приходит twoFactorToken.
export type LoginResult =
  | { requires2fa: false; requires2faSetup: false; session: AuthSession }
  | { requires2fa: true; twoFactorToken: string }
  | { requires2fa: false; requires2faSetup: true; twoFactorToken: string }

export interface TwoFactorRequest {
  code: string
  isRecoveryCode: boolean
  twoFactorToken: string
}

export interface RegisterRequest {
  name: string
  email: string
  password: string
  dateOfBirth: string // YYYY-MM-DD
  uiLanguage: UiLanguage
  acceptTerms: boolean
  consentModeratorAccess: boolean // уходит на сервер как acceptChatModeration
  captchaToken?: string
}

// Ответ сервера с токеном (в user нет skillsComplete и аватара: их берём из GET /me)
export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt?: string
  user: { id: string; email: string; name: string; role: string; uiLanguage: string }
}

interface LoginResponse {
  requires2fa: boolean
  requires2faSetup: boolean
  twoFactorToken: string | null
  accessToken: string | null
  accessTokenExpiresAt: string | null
  user: AuthResponse['user'] | null
}

// Сразу сохраняем токен, чтобы запросить полный профиль (GET /me)
export async function toSession(auth: AuthResponse): Promise<AuthSession> {
  setAccessToken(auth.accessToken)
  const me = await getMe()
  return { accessToken: auth.accessToken, user: toCurrentUser(me) }
}

export async function login(req: LoginRequest): Promise<LoginResult> {
  if (USE_MOCKS) return mockLogin(req)
  try {
    const { data } = await api.post<LoginResponse>('/auth/login', req)
    if (data.requires2fa) return { requires2fa: true, twoFactorToken: data.twoFactorToken! }
    if (data.requires2faSetup) return { requires2fa: false, requires2faSetup: true, twoFactorToken: data.twoFactorToken! }
    const session = await toSession({ accessToken: data.accessToken!, user: data.user! })
    return { requires2fa: false, requires2faSetup: false, session }
  } catch (e) {
    throw toApiError(e)
  }
}

export async function loginTwoFactor(req: TwoFactorRequest): Promise<AuthSession> {
  if (USE_MOCKS) return mockLoginTwoFactor(req)
  try {
    const { data } = await api.post<LoginResponse>('/auth/login/2fa', req)
    return await toSession({ accessToken: data.accessToken!, user: data.user! })
  } catch (e) {
    throw toApiError(e)
  }
}

export async function register(req: RegisterRequest): Promise<void> {
  if (USE_MOCKS) return mockRegister(req)
  try {
    const { consentModeratorAccess, ...rest } = req
    await api.post('/auth/register', { ...rest, acceptChatModeration: consentModeratorAccess })
  } catch (e) {
    throw toApiError(e)
  }
}

// Восстановление сеанса при открытии сайта: refresh-токен в cookie → новый access-токен.
// На заглушках сеанса после перезагрузки нет (как будто cookie не было).
export async function refreshSession(): Promise<AuthSession | null> {
  if (USE_MOCKS) return null
  // Общий обновитель (single-flight, пауза при 429, блокировка между вкладками): api/refreshCoordinator.ts
  return toSession(await refreshAuth())
}

export async function logoutRequest(): Promise<void> {
  if (USE_MOCKS) return
  await api.post('/auth/logout')
}

// ───────── Подтверждение email и сброс пароля (ТЗ FR-1, FR-4) ─────────
// Email подтверждается шестизначным кодом из письма (ссылки в письме нет). Код живёт 15 минут, 5 попыток;
// «отправить ещё раз» выдаёт новый код. Любой отказ — 400 invalid_confirmation_code.

export interface ConfirmEmailCodeRequest {
  email: string
  code: string // 6 цифр, пробелы и дефисы сервер допускает
}

export async function confirmEmailCode(req: ConfirmEmailCodeRequest): Promise<void> {
  if (USE_MOCKS) return mockConfirmEmailCode(req)
  try {
    await api.post('/auth/confirm-email/code', req)
  } catch (e) {
    throw toApiError(e)
  }
}

export async function resendConfirmation(email: string): Promise<void> {
  if (USE_MOCKS) return mockResendConfirmation(email)
  try {
    await api.post('/auth/resend-confirmation', { email })
  } catch (e) {
    throw toApiError(e)
  }
}

export async function forgotPassword(req: { email: string; captchaToken?: string }): Promise<void> {
  if (USE_MOCKS) return mockForgotPassword(req.email)
  try {
    await api.post('/auth/forgot-password', req)
  } catch (e) {
    throw toApiError(e)
  }
}

export interface ResetPasswordRequest {
  userId: string
  token: string // Base64Url, передаём как есть
  newPassword: string
}

export async function resetPassword(req: ResetPasswordRequest): Promise<void> {
  if (USE_MOCKS) return mockResetPassword(req)
  try {
    await api.post('/auth/reset-password', req)
  } catch (e) {
    throw toApiError(e)
  }
}
