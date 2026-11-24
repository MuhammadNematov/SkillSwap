import axios, { AxiosError, InternalAxiosRequestConfig } from 'axios'
import i18n from '../i18n'
import { API_URL } from './config'
import { createRefreshCoordinator, RefreshFatalError, type ChannelLike, type LockManagerLike } from './refreshCoordinator'

// Access-токен хранится только в памяти (ТЗ, раздел 10.1), не в localStorage.
let accessToken: string | null = null
let onAuthFailure: (() => void) | null = null

export const getAccessToken = () => accessToken

export function setAccessToken(token: string | null) {
  accessToken = token
}

// AuthContext регистрирует функцию, которая «разлогинивает» пользователя в интерфейсе.
export function setAuthFailureHandler(handler: (() => void) | null) {
  onAuthFailure = handler
}

export const api = axios.create({
  baseURL: API_URL,
  withCredentials: true, // refresh-токен лежит в HttpOnly-cookie
})

api.interceptors.request.use((config) => {
  if (accessToken) {
    config.headers.Authorization = `Bearer ${accessToken}`
  }
  config.headers['Accept-Language'] = i18n.language
  return config
})

// Эти запросы сами не должны запускать обновление токена (иначе будет цикл).
const NO_REFRESH_URLS = ['/auth/login', '/auth/refresh', '/auth/register']

// Обновление токена: один запрос на все вкладки, 429 и сбои пережидаем, выходим только при 401/403 (refreshCoordinator.ts).
export interface RefreshedSession {
  accessToken: string
  user: { id: string; email: string; name: string; role: string; uiLanguage: string }
}

const refreshCoordinator = createRefreshCoordinator<RefreshedSession>({
  request: async () => (await api.post<RefreshedSession>('/auth/refresh')).data,
  classify: (error) => {
    const response = (error as AxiosError).response
    const retryAfter = Number(response?.headers?.['retry-after'])
    return { status: response?.status, retryAfterMs: Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : undefined }
  },
  locks: typeof navigator !== 'undefined' && 'locks' in navigator ? (navigator.locks as unknown as LockManagerLike) : null,
  channel: typeof BroadcastChannel !== 'undefined' ? (new BroadcastChannel('skillswap-auth') as unknown as ChannelLike) : null,
})

export const refreshAuth = () => refreshCoordinator.refresh()
export { RefreshFatalError }

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    const skip = original?.url && NO_REFRESH_URLS.some((u) => original.url!.includes(u))

    if (error.response?.status === 401 && original && !original._retry && !skip) {
      original._retry = true
      try {
        const { accessToken: token } = await refreshAuth()
        setAccessToken(token)
        original.headers.Authorization = `Bearer ${token}`
        return api(original) // повторяем исходный запрос один раз
      } catch (refreshError) {
        // Разлогиниваем, только если сервер сказал «сеанс недействителен» (401/403). Лимит (429), сеть и 5xx не повод выходить:
        // запрос просто получает исходную ошибку, а сеанс остаётся.
        if (refreshError instanceof RefreshFatalError) {
          setAccessToken(null)
          onAuthFailure?.()
        }
      }
    }
    // Служебный аккаунт снят или заблокирован: токен ещё жив, но сервер отвечает 403 staff_not_active. Сразу разлогиниваем.
    const body = error.response?.data as { code?: string } | undefined
    if (error.response?.status === 403 && body?.code === 'staff_not_active') {
      setAccessToken(null)
      onAuthFailure?.()
    }
    return Promise.reject(error)
  },
)
