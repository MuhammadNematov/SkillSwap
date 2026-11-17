// Пока бэкенд не готов, VITE_USE_MOCKS=true (по умолчанию) — запросы идут на заглушки.
export const USE_MOCKS = import.meta.env.VITE_USE_MOCKS !== 'false'

export const API_URL: string = import.meta.env.VITE_API_URL ?? '/api/v1'
