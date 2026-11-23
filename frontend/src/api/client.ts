import axios, { AxiosError, InternalAxiosRequestConfig } from 'axios'
import i18n from '../i18n'
import { API_URL } from './config'

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

// Если несколько запросов одновременно получили 401 — refresh вызываем один раз.
let refreshPromise: Promise<string> | null = null

async function refreshAccessToken(): Promise<string> {
  const { data } = await api.post<{ accessToken: string }>('/auth/refresh')
  return data.accessToken
}

type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

api.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    const skip = original?.url && NO_REFRESH_URLS.some((u) => original.url!.includes(u))

    if (error.response?.status === 401 && original && !original._retry && !skip) {
      original._retry = true
      try {
        refreshPromise ??= refreshAccessToken().finally(() => {
          refreshPromise = null
        })
        const token = await refreshPromise
        setAccessToken(token)
        original.headers.Authorization = `Bearer ${token}`
        return api(original) // повторяем исходный запрос один раз
      } catch {
        setAccessToken(null)
        onAuthFailure?.()
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
