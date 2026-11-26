import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'

// Уведомления в Telegram и переключатели «что присылать» (docs/BACKEND-DEV.md, «Уведомления в Telegram»). Только обычные аккаунты.
export interface TelegramStatus {
  enabled: boolean // настроен ли бот на сервере; false — блок не показываем
  connected: boolean
  botUsername: string | null
  linkedAt: string | null
}

export interface TelegramLink {
  url: string // https://t.me/<бот>?start=<одноразовый код>, действует 10 минут
  expiresAt: string
}

export type NotificationCategory = 'LessonReminders' | 'LessonChanges' | 'ExchangeRequests' | 'Messages' | 'Cases'

export interface NotificationSetting {
  category: NotificationCategory
  email: boolean | null // null — писем этой группы нет
  telegram: boolean
}

export interface NotificationSettings {
  telegramEnabled: boolean
  telegramConnected: boolean
  items: NotificationSetting[]
}

export interface NotificationSettingUpdate {
  category: NotificationCategory
  email?: boolean
  telegram?: boolean
}

async function http<T>(call: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await call()).data
  } catch (e) {
    throw toApiError(e)
  }
}

const mockSettings: NotificationSettings = {
  telegramEnabled: false,
  telegramConnected: false,
  items: (['LessonReminders', 'LessonChanges', 'ExchangeRequests', 'Messages', 'Cases'] as const).map((category) => ({
    category,
    email: category === 'LessonChanges' ? null : true,
    telegram: true,
  })),
}

export async function getTelegramStatus(): Promise<TelegramStatus> {
  if (USE_MOCKS) return { enabled: false, connected: false, botUsername: null, linkedAt: null }
  return http(() => api.get<TelegramStatus>('/me/telegram'))
}

export async function createTelegramLink(): Promise<TelegramLink> {
  return http(() => api.post<TelegramLink>('/me/telegram/link'))
}

export async function disconnectTelegram(): Promise<TelegramStatus> {
  return http(() => api.delete<TelegramStatus>('/me/telegram'))
}

export async function getNotificationSettings(): Promise<NotificationSettings> {
  if (USE_MOCKS) return structuredClone(mockSettings)
  return http(() => api.get<NotificationSettings>('/me/notification-settings'))
}

export async function updateNotificationSettings(items: NotificationSettingUpdate[]): Promise<NotificationSettings> {
  if (USE_MOCKS) {
    for (const u of items) {
      const row = mockSettings.items.find((x) => x.category === u.category)
      if (row && u.email !== undefined && row.email !== null) row.email = u.email
      if (row && u.telegram !== undefined) row.telegram = u.telegram
    }
    return structuredClone(mockSettings)
  }
  return http(() => api.put<NotificationSettings>('/me/notification-settings', { items }))
}
