import axios from 'axios'
import type { TFunction } from 'i18next'

// Бэкенд отдаёт ошибки как ProblemDetails (RFC 7807) с полем `code` в нижнем регистре, например `invalid_credentials`.
// Для validation_failed есть ещё `errors`: поле → список кодов. Другие поля (existingSkillId, retryAfterSeconds…) лежат в data.
export class ApiError extends Error {
  code: string
  status: number
  data: Record<string, unknown>

  constructor(code: string, status = 400, data: Record<string, unknown> = {}) {
    super(code)
    this.code = code
    this.status = status
    this.data = data
  }

  // Поле → коды ошибок проверки (validation_failed)
  get fieldErrors(): Record<string, string[]> {
    return (this.data.errors as Record<string, string[]> | undefined) ?? {}
  }
}

export function toApiError(e: unknown): ApiError {
  if (e instanceof ApiError) return e
  if (axios.isAxiosError(e)) {
    const data = (e.response?.data ?? {}) as Record<string, unknown>
    const status = e.response?.status
    const code =
      (typeof data.code === 'string' && data.code) ||
      (status === 429 ? 'rate_limited' : e.response ? 'UNKNOWN' : 'NETWORK_ERROR')
    return new ApiError(code, status ?? 0, data)
  }
  return new ApiError('UNKNOWN', 0)
}

// Регистр кода не важен: INVALID_CREDENTIALS и invalid_credentials дают один перевод
export function errorMessage(t: TFunction, e: unknown): string {
  const err = toApiError(e)
  const { code } = err

  // Ошибки полей: показываем перевод первого кода (too_young, password_too_short…)
  if (code === 'validation_failed') {
    const first = Object.values(err.fieldErrors)[0]?.[0]
    if (first) return t([`errors.validation.${first}`, 'errors.codes.validation_failed'])
  }

  // Уточнение стороны: exchange_limit_reached_you / _partner, slot_overlap_you / _partner
  const side = [err.data.limitReachedFor, err.data.conflictFor].find((v) => typeof v === 'string') as string | undefined
  const base = [code, code.toUpperCase(), code.toLowerCase()]
  const keys = [...(side ? base.map((c) => `${c}_${side}`) : []), ...base].map((c) => `errors.codes.${c}`)
  return t(keys, { defaultValue: t('errors.codes.UNKNOWN') })
}

export function isErrorCode(e: unknown, code: string): boolean {
  return toApiError(e).code.toLowerCase() === code.toLowerCase()
}
