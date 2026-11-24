import { ApiError } from '../errors'
import type { AuthSession, CurrentUser, UiLanguage } from '../types'
import type { ConfirmEmailCodeRequest, LoginRequest, LoginResult, RegisterRequest, ResetPasswordRequest, TwoFactorRequest } from '../auth'

// ───────── Заглушки для входа. Удалим, когда бэкенд будет готов. ─────────
//
// Тестовые аккаунты (пароль у всех Test1234):
//   user@test.com  — обычный пользователь
//   2fa@test.com   — пользователь с 2FA (код 123456, резервный код RECOVERY1)
//   mod@test.com   — модератор (по ТЗ у служебных ролей 2FA обязательна)
//   admin@test.com — админ
//   super@test.com — суперадмин
//   newmod@test.com — модератор без 2FA: при входе нужно её настроить (код 123456)

const PASSWORD = 'Test1234'
const TWO_FA_CODE = '123456'
const RECOVERY_CODE = 'RECOVERY1'

interface MockAccount {
  user: CurrentUser
  password: string
  requires2fa: boolean
  requiresSetup?: boolean // служебный аккаунт без 2FA: при входе обязательна настройка
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
    user: { id: `mock-${email}`, email, name, role, uiLanguage: 'ru', skillsComplete: true },
  }
}

const accounts = new Map<string, MockAccount>(
  [
    account('user@test.com', 'Абубакр', 'User'),
    account('2fa@test.com', 'Диёрбек', 'User', true),
    account('mod@test.com', 'Модератор', 'Moderator', true),
    account('admin@test.com', 'Админ', 'Admin', true),
    account('super@test.com', 'Суперадмин', 'SuperAdmin', true),
    { ...account('newmod@test.com', 'Новый модератор', 'Moderator'), requiresSetup: true },
  ].map((a) => [a.user.email, a]),
)

// Почта ещё не подтверждена (на заглушках письмо не уходит — см. ссылку на странице регистрации)
const unconfirmed = new Set<string>()

const failedAttempts = new Map<string, number>()
const lockedUntil = new Map<string, number>()
const pending2fa = new Map<string, string>() // twoFactorToken → email

const sleep = (ms = 450) => new Promise((r) => setTimeout(r, ms))
const sessionOf = (a: MockAccount): AuthSession => ({ accessToken: 'mock-access-token', user: a.user })

export async function mockLogin({ email, password }: LoginRequest): Promise<LoginResult> {
  await sleep()
  const key = email.trim().toLowerCase()

  // Блокировка на 15 минут после 5 неудачных попыток (FR-5)
  if ((lockedUntil.get(key) ?? 0) > Date.now()) throw new ApiError('account_locked_out', 423)

  const acc = accounts.get(key)
  if (!acc || acc.password !== password) {
    const n = (failedAttempts.get(key) ?? 0) + 1
    failedAttempts.set(key, n)
    if (n >= 5) {
      lockedUntil.set(key, Date.now() + 15 * 60 * 1000)
      failedAttempts.delete(key)
      throw new ApiError('account_locked_out', 423)
    }
    throw new ApiError('invalid_credentials', 401)
  }

  failedAttempts.delete(key)
  if (unconfirmed.has(key)) throw new ApiError('email_not_confirmed', 403)
  if (acc.requiresSetup) {
    const twoFactorToken = crypto.randomUUID()
    pending2fa.set(twoFactorToken, key)
    return { requires2fa: false, requires2faSetup: true, twoFactorToken }
  }
  if (acc.requires2fa) {
    const twoFactorToken = crypto.randomUUID()
    pending2fa.set(twoFactorToken, key)
    return { requires2fa: true, twoFactorToken }
  }
  return { requires2fa: false, requires2faSetup: false, session: sessionOf(acc) }
}

export async function mockLoginTwoFactor({ code, twoFactorToken }: TwoFactorRequest): Promise<AuthSession> {
  await sleep()
  const email = twoFactorToken ? pending2fa.get(twoFactorToken) : undefined
  const acc = email ? accounts.get(email) : undefined
  if (!acc) throw new ApiError('invalid_credentials', 401)
  if (code !== TWO_FA_CODE && code !== RECOVERY_CODE) throw new ApiError('invalid_two_factor_code', 400)
  pending2fa.delete(twoFactorToken!)
  return sessionOf(acc)
}

export async function mockRegister(req: RegisterRequest): Promise<void> {
  await sleep()
  const key = req.email.trim().toLowerCase()
  if (accounts.has(key)) throw new ApiError('email_already_registered', 409)
  unconfirmed.add(key)
  accounts.set(key, {
    password: req.password,
    requires2fa: false,
    user: {
      id: `mock-${key}`,
      email: key,
      name: req.name,
      role: 'User',
      uiLanguage: req.uiLanguage as UiLanguage,
      skillsComplete: false,
    },
  })
}

// На заглушках userId имеет вид mock-<email>
const emailOfMockUser = (userId: string) => userId.replace(/^mock-/, '').toLowerCase()

// На заглушках подходит любой код из 6 цифр, кроме 000000 (им проверяется ошибка)
export async function mockConfirmEmailCode({ email, code }: ConfirmEmailCodeRequest): Promise<void> {
  await sleep()
  const digits = code.replace(/[\s-]/g, '')
  if (!/^\d{6}$/.test(digits) || digits === '000000') throw new ApiError('invalid_confirmation_code', 400)
  unconfirmed.delete(email.trim().toLowerCase())
}

export async function mockResendConfirmation(_email: string): Promise<void> {
  await sleep()
}

// Всегда успех: сервер не должен раскрывать, есть ли такой email
export async function mockForgotPassword(_email: string): Promise<void> {
  await sleep()
}

export async function mockResetPassword({ userId, token, newPassword }: ResetPasswordRequest): Promise<void> {
  await sleep()
  if (!token || token === 'invalid') throw new ApiError('invalid_confirmation_token', 400)
  const acc = accounts.get(emailOfMockUser(userId))
  if (acc) acc.password = newPassword
}

// Завершение обязательной настройки 2FA при входе: сервер сразу выдаёт сеанс
export function mockSetupSession(twoFactorToken: string): AuthSession | null {
  const email = pending2fa.get(twoFactorToken)
  const acc = email ? accounts.get(email) : undefined
  if (!acc) return null
  acc.requiresSetup = false
  acc.requires2fa = true
  pending2fa.delete(twoFactorToken)
  return sessionOf(acc)
}
