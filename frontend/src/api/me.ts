import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import type { CurrentUser, MyProfile, ProfileUpdate } from './types'

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// Тестовый профиль на заглушках (в памяти). Удалим, когда подключим настоящий бэкенд.
let mockMe: MyProfile = {
  id: 'me-mock',
  email: 'user@test.com',
  role: 'User',
  name: 'Абубакр',
  bio: null,
  avatarUrl: null,
  dateOfBirth: '2000-01-01',
  uiLanguage: 'ru',
  timeZone: '',
  isSearchable: false,
  languages: [],
  skillsComplete: false,
  twoFactorEnabled: false,
  ratingAvg: null,
  ratingCount: 0,
  isNewcomer: true,
}

export function setMockSkillsComplete(value: boolean) {
  mockMe = { ...mockMe, skillsComplete: value, isSearchable: value || mockMe.isSearchable }
}
export function setMockTwoFactor(value: boolean) {
  mockMe = { ...mockMe, twoFactorEnabled: value }
}

// GET /me
export async function getMe(): Promise<MyProfile> {
  if (USE_MOCKS) {
    await sleep()
    return mockMe
  }
  return http(() => api.get<MyProfile>('/me'))
}

// PUT /me — заменяет поля целиком. isSearchable: true при неполной анкете даёт 403 skills_incomplete.
export async function updateMe(body: ProfileUpdate): Promise<MyProfile> {
  if (USE_MOCKS) {
    await sleep(350)
    mockMe = { ...mockMe, ...body }
    return mockMe
  }
  return http(() => api.put<MyProfile>('/me', body))
}

// Профиль → пользователь для интерфейса. Часть полей (skillsComplete, avatarUrl) есть только в /me.
export function toCurrentUser(p: MyProfile): CurrentUser {
  return {
    id: p.id,
    email: p.email,
    name: p.name,
    role: p.role,
    uiLanguage: (['tg', 'ru', 'en'].includes(p.uiLanguage) ? p.uiLanguage : 'ru') as CurrentUser['uiLanguage'],
    avatarUrl: p.avatarUrl,
    skillsComplete: p.skillsComplete,
    twoFactorEnabled: p.twoFactorEnabled,
  }
}

// PUT /me/avatar — multipart, поле File (jpeg/png/webp). Ответ: { avatarUrl } (подписанная ссылка)
export async function uploadAvatar(file: File): Promise<string> {
  if (USE_MOCKS) {
    await sleep(400)
    const url = URL.createObjectURL(file)
    mockMe = { ...mockMe, avatarUrl: url }
    return url
  }
  const form = new FormData()
  form.append('File', file)
  const data = await http(() => api.put<{ avatarUrl: string }>('/me/avatar', form))
  return data.avatarUrl
}

// DELETE /me/avatar
export async function deleteAvatar(): Promise<void> {
  if (USE_MOCKS) {
    await sleep(250)
    mockMe = { ...mockMe, avatarUrl: null }
    return
  }
  await http(() => api.delete('/me/avatar'))
}
