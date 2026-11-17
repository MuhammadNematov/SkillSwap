import { ApiError } from '../errors'
import type { AuthSession, CurrentUser, UiLanguage } from '../types'
import type { LoginRequest, LoginResult, RegisterRequest, TwoFactorRequest } from '../auth'

// ───────── Заглушки для входа. Удалим, когда бэкенд будет готов. ─────────
//
// Тестовые аккаунты (пароль у всех Test1234):
//   user@test.com  — обычный пользователь
//   2fa@test.com   — пользователь с 2FA (код 123456, резервный код RECOVERY1)
//   mod@test.com   — модератор (по ТЗ у служебных ролей 2FA обязательна)
//   admin@test.com — админ
//   super@test.com — суперадмин

const PASSWORD = 'Test1234'
const TWO_FA_CODE = '123456'
const RECOVERY_CODE = 'RECOVERY1'

interface MockAccount {
  user: CurrentUser
  password: string
  requires2fa: boolean
}

function account(
  email: string,
  name: string,
  role: CurrentUser['role'],
  requires2fa = false,
): MockAccount {
  return {
    password: PASSWORD,
    requires2fa,
    user: { id: `mock-${email}`, email, name, role, uiLanguage: 'ru', hasProfile: true },
  }
}

const accounts = new Map<string, MockAccount>(
  [
    account('user@test.com', 'Абубакр', 'User'),
    account('2fa@test.com', 'Диёрбек', 'User', true),
    account('mod@test.com', 'Модератор', 'Moderator', true),
    account('admin@test.com', 'Админ', 'Admin', true),
    account('super@test.com', 'Суперадмин', 'Superadmin', true),
  ].map((a) => [a.user.email, a]),
)

const failedAttempts = new Map<string, number>()
const lockedUntil = new Map<string, number>()
const pending2fa = new Map<string, string>() // challengeToken → email

const sleep = (ms = 450) => new Promise((r) => setTimeout(r, ms))
const sessionOf = (a: MockAccount): AuthSession => ({ accessToken: 'mock-access-token', user: a.user })

export async function mockLogin({ email, password }: LoginRequest): Promise<LoginResult> {
  await sleep()
  const key = email.trim().toLowerCase()

  // Блокировка на 15 минут после 5 неудачных попыток (FR-5)
  if ((lockedUntil.get(key) ?? 0) > Date.now()) throw new ApiError('ACCOUNT_LOCKED', 423)

  const acc = accounts.get(key)
  if (!acc || acc.password !== password) {
    const n = (failedAttempts.get(key) ?? 0) + 1
    failedAttempts.set(key, n)
    if (n >= 5) {
      lockedUntil.set(key, Date.now() + 15 * 60 * 1000)
      failedAttempts.delete(key)
      throw new ApiError('ACCOUNT_LOCKED', 423)
    }
    throw new ApiError('INVALID_CREDENTIALS', 401)
  }

  failedAttempts.delete(key)
  if (acc.requires2fa) {
    const challengeToken = crypto.randomUUID()
    pending2fa.set(challengeToken, key)
    return { requires2fa: true, challengeToken }
  }
  return { requires2fa: false, session: sessionOf(acc) }
}

export async function mockLoginTwoFactor({ code, challengeToken }: TwoFactorRequest): Promise<AuthSession> {
  await sleep()
  const email = challengeToken ? pending2fa.get(challengeToken) : undefined
  const acc = email ? accounts.get(email) : undefined
  if (!acc) throw new ApiError('INVALID_CREDENTIALS', 401)
  if (code !== TWO_FA_CODE && code !== RECOVERY_CODE) throw new ApiError('INVALID_2FA_CODE', 400)
  pending2fa.delete(challengeToken!)
  return sessionOf(acc)
}

export async function mockRegister(req: RegisterRequest): Promise<void> {
  await sleep()
  const key = req.email.trim().toLowerCase()
  if (accounts.has(key)) throw new ApiError('EMAIL_TAKEN', 409)
  // На заглушках сразу считаем email подтверждённым, чтобы можно было войти.
  accounts.set(key, {
    password: req.password,
    requires2fa: false,
    user: {
      id: `mock-${key}`,
      email: key,
      name: req.name,
      role: 'User',
      uiLanguage: req.uiLanguage as UiLanguage,
      hasProfile: false,
    },
  })
}
