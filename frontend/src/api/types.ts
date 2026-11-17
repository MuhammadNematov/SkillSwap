// Типы пишем вручную. Когда бэкенд выложит Swagger — сверим и поправим.

export type Role = 'User' | 'Moderator' | 'Admin' | 'Superadmin'

export type UiLanguage = 'tg' | 'ru' | 'en'

export interface CurrentUser {
  id: string
  email: string
  name: string
  role: Role
  uiLanguage: UiLanguage
  avatarUrl?: string | null
  // false — анкета (онбординг) ещё не заполнена
  hasProfile: boolean
}

export interface AuthSession {
  accessToken: string
  user: CurrentUser
}
