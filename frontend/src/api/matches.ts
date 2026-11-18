import i18n from '../i18n'
import { pickName } from '../utils/localized'
import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import { PEOPLE } from './mocks/people'
import type { ExchangeRequestInput, MatchFilters, MatchItem, SkillLevel } from './types'

const sleep = (ms = 350) => new Promise((r) => setTimeout(r, ms))

interface MatchedSkillDto {
  skillId: string
  nameTg: string
  nameRu: string
  nameEn: string
  theirLevel: SkillLevel
}
interface MatchDto {
  userId: string
  name: string
  avatarUrl: string | null
  timeZone: string
  languages: string[]
  ratingAvg: number | null
  ratingCount: number
  matchType: 'Full' | 'Partial'
  theyTeach: MatchedSkillDto[]
}

// GET /matches?skill=&lang=&level=&minRating=&page=&pageSize=
// skill: id навыка из моего «Хочу изучать»; level: уровень партнёра. Сервер сам ставит полные совпадения выше частичных.
// Поиска по имени на сервере нет, поэтому q применяем здесь, по уже полученной странице.
export async function getMatches(filters: MatchFilters): Promise<MatchItem[]> {
  if (USE_MOCKS) {
    await sleep()
    const q = filters.q?.trim().toLowerCase()
    return PEOPLE.filter((p) => {
      if (q && !p.name.toLowerCase().includes(q) && !p.teach.some((s) => s.name.toLowerCase().includes(q))) return false
      if (filters.skill && !p.teach.some((s) => s.skillId === filters.skill)) return false
      if (filters.lang && !p.languages.includes(filters.lang)) return false
      if (filters.level && !p.teach.some((s) => s.level === filters.level)) return false
      if (filters.minRating && p.ratingAvg < filters.minRating) return false
      return true
    })
      .map((p) => ({
        matchType: p.matchType,
        teaches: p.teach,
        user: {
          id: p.id,
          name: p.name,
          avatarUrl: p.avatarUrl,
          ratingAvg: p.ratingAvg,
          ratingCount: p.ratingCount,
          languages: p.languages,
          timeZone: p.timeZone,
        },
      }))
      .sort((a, b) => Number(b.matchType === 'full') - Number(a.matchType === 'full'))
  }
  try {
    const { data } = await api.get<{ items: MatchDto[] }>('/matches', {
      params: {
        skill: filters.skill,
        lang: filters.lang,
        level: filters.level,
        minRating: filters.minRating,
        page: 1,
        pageSize: 50,
      },
    })
    const lang = i18n.resolvedLanguage ?? 'ru'
    const q = filters.q?.trim().toLowerCase()
    const items: MatchItem[] = data.items.map((m) => ({
      matchType: m.matchType === 'Full' ? 'full' : 'partial',
      teaches: m.theyTeach.map((s) => ({
        skillId: s.skillId,
        name: pickName({ tg: s.nameTg, ru: s.nameRu, en: s.nameEn }, lang),
        level: s.theirLevel,
      })),
      user: {
        id: m.userId,
        name: m.name,
        avatarUrl: m.avatarUrl,
        ratingAvg: m.ratingAvg ?? 0,
        ratingCount: m.ratingCount,
        languages: m.languages,
        timeZone: m.timeZone,
      },
    }))
    return q ? items.filter((m) => m.user.name.toLowerCase().includes(q) || m.teaches.some((s) => s.name.toLowerCase().includes(q))) : items
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /exchange-requests
// Лимиты (5 исходящих, 10 в сутки, кулдаун 30 дней) проверяет сервер и вернёт код ошибки.
export async function createExchangeRequest(input: ExchangeRequestInput): Promise<void> {
  if (USE_MOCKS) {
    await sleep(500)
    return
  }
  try {
    await api.post('/exchange-requests', input)
  } catch (e) {
    throw toApiError(e)
  }
}
