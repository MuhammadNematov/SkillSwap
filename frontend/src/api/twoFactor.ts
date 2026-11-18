import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { toSession, type AuthResponse } from './auth'
import { mockSetupSession } from './mocks/auth'
import { setMockTwoFactor } from './me'
import type { AuthSession, AuthSessionInfo, TwoFactorSetup } from './types'

const sleep = (ms = 300) => new Promise((r) => setTimeout(r, ms))

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /auth/2fa/setup
// Из настроек (пользователь уже вошёл): без тела. Служебный аккаунт при входе передаёт twoFactorToken из ответа /auth/login.
export async function setupTwoFactor(twoFactorToken?: string): Promise<TwoFactorSetup> {
  if (USE_MOCKS) {
    await sleep()
    return { sharedKey: 'JBSWY3DPEHPK3PXP', authenticatorUri: 'otpauth://totp/SkillSwap:user?secret=JBSWY3DPEHPK3PXP&issuer=SkillSwap' }
  }
  return http(() => api.post<TwoFactorSetup>('/auth/2fa/setup', twoFactorToken ? { twoFactorToken } : null))
}

export interface VerifyResult {
  recoveryCodes: string[]
  session: AuthSession | null // при настройке во время входа сервер сразу выдаёт сеанс
}

// POST /auth/2fa/verify — включает 2FA и один раз отдаёт резервные коды
export async function verifyTwoFactor(req: { code: string; twoFactorToken?: string }): Promise<VerifyResult> {
  if (USE_MOCKS) {
    await sleep()
    if (req.code !== '123456') throw new ApiError('invalid_two_factor_code', 400)
    setMockTwoFactor(true)
    return { recoveryCodes: ['A1B2-C3D4', 'E5F6-G7H8', 'J9K1-L2M3', 'N4P5-Q6R7', 'S8T9-U1V2', 'W3X4-Y5Z6', 'B7C8-D9E1', 'F2G3-H4J5'], session: req.twoFactorToken ? mockSetupSession(req.twoFactorToken) : null }
  }
  const data = await http(() =>
    api.post<{ recoveryCodes: string[]; auth: AuthResponse | null }>('/auth/2fa/verify', {
      code: req.code,
      twoFactorToken: req.twoFactorToken ?? null,
    }),
  )
  return { recoveryCodes: data.recoveryCodes, session: data.auth ? await toSession(data.auth) : null }
}

// POST /auth/2fa/disable (204)
export async function disableTwoFactor(req: { password: string; code: string }): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    if (req.code !== '123456') throw new ApiError('invalid_two_factor_code', 400)
    setMockTwoFactor(false)
    return
  }
  await http(() => api.post('/auth/2fa/disable', req))
}

// ───────── Сеансы входа ─────────

let mockSessions: AuthSessionInfo[] = [
  { id: 's-current', deviceInfo: 'Chrome, Windows', ipAddress: '127.0.0.1', startedAt: new Date(Date.now() - 3_600_000).toISOString(), lastActiveAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 29 * 86_400_000).toISOString(), isCurrent: true },
  { id: 's-phone', deviceInfo: 'Safari, iPhone', ipAddress: '192.168.0.12', startedAt: new Date(Date.now() - 5 * 86_400_000).toISOString(), lastActiveAt: new Date(Date.now() - 86_400_000).toISOString(), expiresAt: new Date(Date.now() + 25 * 86_400_000).toISOString(), isCurrent: false },
]

// GET /auth/sessions
export async function getAuthSessions(): Promise<AuthSessionInfo[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockSessions
  }
  return http(() => api.get<AuthSessionInfo[]>('/auth/sessions'))
}

// DELETE /auth/sessions/{id}
export async function revokeAuthSession(id: string): Promise<void> {
  if (USE_MOCKS) {
    mockSessions = mockSessions.filter((s) => s.id !== id)
    return
  }
  await http(() => api.delete(`/auth/sessions/${id}`))
}

// DELETE /auth/sessions — завершить остальные сеансы
export async function revokeAllAuthSessions(): Promise<void> {
  if (USE_MOCKS) {
    mockSessions = mockSessions.filter((s) => s.isCurrent)
    return
  }
  await http(() => api.delete('/auth/sessions'))
}
