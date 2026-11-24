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

// Ответ без поля `code` (его не отдал наш бэкенд): определяем причину по статусу.
// Если вместо JSON пришла страница (например, HTML от dev-сервера Vite без прокси, 404/502), API не отвечает.
function codeWithoutBody(status: number, data: unknown, contentType: string): string {
  if (status === 429) return 'rate_limited'
  const isJson = contentType.includes('json') && typeof data === 'object' && data !== null
  if (!isJson && (status === 404 || status === 502 || status === 503 || status === 504)) return 'api_unreachable'
  if (isJson && 'errors' in (data as object) && status === 400) return 'validation_failed' // ошибка разбора запроса от ASP.NET
  if (status === 502 || status === 503 || status === 504) return 'service_unavailable'
  if (status >= 500) return 'internal_error'
  const byStatus: Record<number, string> = { 400: 'bad_request', 401: 'unauthorized', 403: 'forbidden', 404: 'NOT_FOUND', 409: 'conflict', 413: 'payload_too_large' }
  return byStatus[status] ?? 'UNKNOWN'
}

export function toApiError(e: unknown): ApiError {
  if (e instanceof ApiError) return e
  if (axios.isAxiosError(e)) {
    const data = (e.response?.data ?? {}) as Record<string, unknown>
    const status = e.response?.status
    const contentType = String(e.response?.headers?.['content-type'] ?? '')
    const code =
      (typeof data.code === 'string' && data.code) ||
      (e.response ? codeWithoutBody(status ?? 0, e.response.data, contentType) : 'NETWORK_ERROR')
    return new ApiError(code, status ?? 0, typeof e.response?.data === 'object' && e.response?.data !== null ? data : {})
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
