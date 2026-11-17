import axios from 'axios'
import type { TFunction } from 'i18next'

// Бэкенд отдаёт ошибки как ProblemDetails с кодом (ТЗ, раздел 12).
// Название поля с кодом уточним по Swagger — пока читаем `code` или `errorCode`.
export class ApiError extends Error {
  code: string
  status: number

  constructor(code: string, status = 400) {
    super(code)
    this.code = code
    this.status = status
  }
}

export function toApiError(e: unknown): ApiError {
  if (e instanceof ApiError) return e
  if (axios.isAxiosError(e)) {
    const data = e.response?.data as { code?: string; errorCode?: string } | undefined
    const code = data?.code ?? data?.errorCode ?? (e.response ? 'UNKNOWN' : 'NETWORK_ERROR')
    return new ApiError(code, e.response?.status ?? 0)
  }
  return new ApiError('UNKNOWN', 0)
}

// Переводим код ошибки в понятный текст на выбранном языке
export function errorMessage(t: TFunction, e: unknown): string {
  const { code } = toApiError(e)
  return t(`errors.codes.${code}`, { defaultValue: t('errors.codes.UNKNOWN') })
}
