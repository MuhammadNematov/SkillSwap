import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { MOCK_CATEGORIES, MOCK_LANGUAGES, MOCK_SKILLS } from './mocks/catalog'
import { setMockSkillsComplete } from './me'
import type {
  CatalogPage,
  CatalogQuery,
  LanguageOption,
  LearnEntry,
  LocalizedName,
  MySkills,
  SkillCategory,
  SkillLevel,
  TeachEntry,
} from './types'

const sleep = (ms = 200) => new Promise((r) => setTimeout(r, ms))

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// Сервер отдаёт nameTg / nameRu / nameEn, интерфейсу удобнее один объект
interface Named {
  nameTg: string
  nameRu: string
  nameEn: string
}
const names = (n: Named): LocalizedName => ({ tg: n.nameTg, ru: n.nameRu, en: n.nameEn })

// GET /skill-categories
export async function getSkillCategories(): Promise<SkillCategory[]> {
  if (USE_MOCKS) {
    await sleep()
    return MOCK_CATEGORIES
  }
  const data = await http(() => api.get<(Named & { id: string })[]>('/skill-categories'))
  return data.map((c) => ({ id: c.id, names: names(c) }))
}

// GET /skills?category=&q=&lang=&page=&pageSize= (pageSize не больше 50; lang влияет только на сортировку)
export async function searchCatalog(query: CatalogQuery): Promise<CatalogPage> {
  if (USE_MOCKS) {
    await sleep()
    const q = query.q?.trim().toLowerCase()
    const all = MOCK_SKILLS.filter(
      (s) =>
        (!query.category || s.categoryId === query.category) &&
        (!q || Object.values(s.names).some((n) => n.toLowerCase().includes(q))),
    )
    const from = (query.page - 1) * query.pageSize
    return { items: all.slice(from, from + query.pageSize), total: all.length }
  }
  const data = await http(() =>
    api.get<{ items: (Named & { id: string; categoryId: string })[]; totalCount: number }>('/skills', {
      params: {
        category: query.category || undefined,
        q: query.q?.trim() || undefined,
        lang: query.lang,
        page: query.page,
        pageSize: query.pageSize,
      },
    }),
  )
  return { items: data.items.map((s) => ({ id: s.id, categoryId: s.categoryId, names: names(s) })), total: data.totalCount }
}

// GET /languages — справочник языков занятий
export async function getLanguages(): Promise<LanguageOption[]> {
  if (USE_MOCKS) {
    await sleep()
    return MOCK_LANGUAGES
  }
  const data = await http(() => api.get<(Named & { code: string })[]>('/languages'))
  return data.map((l) => ({ code: l.code, names: names(l) }))
}

let mockMine: MySkills = { teach: [], learn: [] }

interface SkillDto extends Named {
  skillId: string
  level: SkillLevel
  description: string | null
  isActive: boolean
}
interface MySkillsDto {
  teach: SkillDto[]
  learn: SkillDto[]
  isComplete: boolean
  isSearchable: boolean
}

// GET /me/skills
export async function getMySkills(): Promise<MySkills> {
  if (USE_MOCKS) {
    await sleep()
    return mockMine
  }
  const d = await http(() => api.get<MySkillsDto>('/me/skills'))
  const teach: TeachEntry[] = d.teach.map((s) => ({
    skillId: s.skillId,
    level: s.level,
    description: s.description ?? '',
    names: names(s),
    isActive: s.isActive,
  }))
  const learn: LearnEntry[] = d.learn.map((s) => ({ skillId: s.skillId, level: s.level, names: names(s), isActive: s.isActive }))
  return { teach, learn, isComplete: d.isComplete, isSearchable: d.isSearchable }
}

// PUT /me/skills — заменяет оба списка целиком. Когда анкета полная, сервер сам включает показ в подборе.
export async function saveMySkills(body: { teach: TeachEntry[]; learn: LearnEntry[] }): Promise<void> {
  if (USE_MOCKS) {
    await sleep(400)
    mockMine = { teach: body.teach, learn: body.learn, isComplete: body.teach.length > 0 && body.learn.length > 0 }
    setMockSkillsComplete(!!mockMine.isComplete)
    return
  }
  await http(() =>
    api.put('/me/skills', {
      teach: body.teach.map((e) => ({ skillId: e.skillId, level: e.level, description: e.description.trim() || null })),
      learn: body.learn.map((e) => ({ skillId: e.skillId, level: e.level, description: null })),
    }),
  )
}

// POST /skills/suggestions — предложить новый навык (201 Pending).
// 409 skill_already_exists: в ошибке есть existingSkillId — такой навык уже есть в каталоге.
export async function suggestSkill(body: { name: string; categoryId: string }): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    const exists = MOCK_SKILLS.find((s) => Object.values(s.names).some((n) => n.toLowerCase() === body.name.trim().toLowerCase()))
    if (exists) throw new ApiError('skill_already_exists', 409, { existingSkillId: exists.id })
    return
  }
  await http(() => api.post('/skills/suggestions', body))
}
