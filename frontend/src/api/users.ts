import i18n from '../i18n'
import { pickName } from '../utils/localized'
import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { getMe } from './me'
import { ME, findPerson } from './mocks/people'
import { getMySkills } from './onboarding'
import type { LocalizedName, SkillLevel, UserProfile, UserSkill } from './types'

const sleep = (ms = 300) => new Promise((r) => setTimeout(r, ms))

interface PublicSkillDto {
  skillId: string
  nameTg: string
  nameRu: string
  nameEn: string
  level: SkillLevel
  description: string | null
}
interface PublicProfileDto {
  id: string
  name: string
  bio: string | null
  avatarUrl: string | null
  timeZone: string
  languages: string[]
  teach: PublicSkillDto[]
  learn: PublicSkillDto[]
  ratingAvg: number | null
  ratingCount: number
}

const lang = () => i18n.resolvedLanguage ?? 'ru'
const toSkill = (s: PublicSkillDto): UserSkill => ({
  skillId: s.skillId,
  name: pickName({ tg: s.nameTg, ru: s.nameRu, en: s.nameEn }, lang()),
  level: s.level,
  description: s.description ?? undefined,
})

// 'me' → GET /me + GET /me/skills, иначе GET /users/{id}.
// 404 user_not_found приходит и когда пользователя нет, и когда он вам недоступен (скрыт, заблокирован…).
// Отзывы и счётчик обменов появятся вместе с обменами (этап 4), пока пусто.
export async function getUserProfile(id: string): Promise<UserProfile> {
  if (USE_MOCKS) {
    await sleep()
    if (id === 'me') return ME
    const p = findPerson(id)
    if (!p) throw new ApiError('user_not_found', 404)
    return p
  }
  try {
    if (id === 'me') {
      const [me, skills] = await Promise.all([getMe(), getMySkills()])
      const toMine = (e: { skillId: string; names?: LocalizedName; level: SkillLevel; description?: string }): UserSkill => ({
        skillId: e.skillId,
        name: e.names ? pickName(e.names, lang()) : e.skillId,
        level: e.level,
        description: e.description || undefined,
      })
      return {
        id: me.id,
        name: me.name,
        avatarUrl: me.avatarUrl,
        ratingAvg: me.ratingAvg ?? 0,
        ratingCount: me.ratingCount,
        languages: me.languages,
        timeZone: me.timeZone,
        bio: me.bio,
        completedExchanges: 0,
        teach: skills.teach.filter((s) => s.isActive !== false).map(toMine),
        learn: skills.learn.filter((s) => s.isActive !== false).map(toMine),
        reviews: [],
        isMe: true,
      }
    }
    const { data } = await api.get<PublicProfileDto>(`/users/${id}`)
    return {
      id: data.id,
      name: data.name,
      avatarUrl: data.avatarUrl,
      ratingAvg: data.ratingAvg ?? 0,
      ratingCount: data.ratingCount,
      languages: data.languages,
      timeZone: data.timeZone,
      bio: data.bio,
      completedExchanges: 0,
      teach: data.teach.map(toSkill),
      learn: data.learn.map(toSkill),
      reviews: [],
      isMe: false,
    }
  } catch (e) {
    throw toApiError(e)
  }
}
