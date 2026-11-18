import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { MOCK_CATEGORIES, MOCK_SKILLS } from './mocks/catalog'

// ───────── Каталог навыков для админа (/admin/skills, /admin/skill-categories) ─────────

export interface AdminCategory {
  id: string
  nameTg: string
  nameRu: string
  nameEn: string
  sortOrder: number
  isActive: boolean
  skillCount: number
}
export interface AdminSkill {
  id: string
  categoryId: string
  nameTg: string
  nameRu: string
  nameEn: string
  synonyms: string[]
  isActive: boolean
}
export interface CategoryInput {
  nameTg: string
  nameRu: string
  nameEn: string
  sortOrder: number
  isActive?: boolean
}
export interface SkillInput {
  categoryId: string
  nameTg: string
  nameRu: string
  nameEn: string
  synonyms: string[]
  isActive?: boolean
}
export interface AdminSkillFilters {
  category?: string
  q?: string
  isActive?: boolean
  page: number
  pageSize: number
}

const sleep = (ms = 200) => new Promise((r) => setTimeout(r, ms))
let seq = 0
const uid = (p: string) => `${p}-${Date.now()}-${++seq}`

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// Данные заглушек (в памяти)
let mockCategories: AdminCategory[] = MOCK_CATEGORIES.map((c, i) => ({
  id: c.id,
  nameTg: c.names.tg,
  nameRu: c.names.ru,
  nameEn: c.names.en,
  sortOrder: i + 1,
  isActive: true,
  skillCount: 0,
}))
let mockSkills: AdminSkill[] = MOCK_SKILLS.map((s) => ({
  id: s.id,
  categoryId: s.categoryId,
  nameTg: s.names.tg,
  nameRu: s.names.ru,
  nameEn: s.names.en,
  synonyms: [],
  isActive: true,
}))
const withCounts = () => mockCategories.map((c) => ({ ...c, skillCount: mockSkills.filter((s) => s.categoryId === c.id).length }))

// GET /admin/skill-categories
export async function getAdminCategories(): Promise<AdminCategory[]> {
  if (USE_MOCKS) {
    await sleep()
    return withCounts()
  }
  return http(() => api.get<AdminCategory[]>('/admin/skill-categories'))
}

// POST /admin/skill-categories (409 category_name_taken)
export async function createCategory(body: CategoryInput): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    if (mockCategories.some((c) => c.nameRu.toLowerCase() === body.nameRu.trim().toLowerCase())) throw new ApiError('category_name_taken', 409)
    mockCategories = [...mockCategories, { id: uid('cat'), isActive: true, skillCount: 0, ...body }]
    return
  }
  await http(() => api.post('/admin/skill-categories', body))
}

// PUT /admin/skill-categories/{id}
export async function updateCategory({ id, ...body }: CategoryInput & { id: string }): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    mockCategories = mockCategories.map((c) => (c.id === id ? { ...c, ...body, isActive: body.isActive ?? c.isActive } : c))
    return
  }
  await http(() => api.put(`/admin/skill-categories/${id}`, body))
}

// DELETE /admin/skill-categories/{id} (409 category_not_empty)
export async function deleteCategory(id: string): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    if (mockSkills.some((s) => s.categoryId === id)) throw new ApiError('category_not_empty', 409)
    mockCategories = mockCategories.filter((c) => c.id !== id)
    return
  }
  await http(() => api.delete(`/admin/skill-categories/${id}`))
}

// GET /admin/skills?category=&q=&isActive=&page=&pageSize=
export async function getAdminSkills(f: AdminSkillFilters): Promise<{ items: AdminSkill[]; total: number }> {
  if (USE_MOCKS) {
    await sleep()
    const q = f.q?.trim().toLowerCase()
    const all = mockSkills.filter(
      (s) =>
        (!f.category || s.categoryId === f.category) &&
        (f.isActive === undefined || s.isActive === f.isActive) &&
        (!q || [s.nameTg, s.nameRu, s.nameEn, ...s.synonyms].some((n) => n.toLowerCase().includes(q))),
    )
    const from = (f.page - 1) * f.pageSize
    return { items: all.slice(from, from + f.pageSize), total: all.length }
  }
  const data = await http(() =>
    api.get<{ items: AdminSkill[]; totalCount: number }>('/admin/skills', {
      params: { category: f.category || undefined, q: f.q?.trim() || undefined, isActive: f.isActive, page: f.page, pageSize: f.pageSize },
    }),
  )
  return { items: data.items, total: data.totalCount }
}

// POST /admin/skills (409 skill_name_taken, 404 category_not_found)
export async function createSkill(body: SkillInput): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    if (mockSkills.some((s) => s.categoryId === body.categoryId && s.nameRu.toLowerCase() === body.nameRu.trim().toLowerCase())) throw new ApiError('skill_name_taken', 409)
    mockSkills = [...mockSkills, { id: uid('skill'), isActive: true, ...body }]
    return
  }
  await http(() => api.post('/admin/skills', body))
}

// PUT /admin/skills/{id}
export async function updateSkill({ id, ...body }: SkillInput & { id: string }): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    mockSkills = mockSkills.map((s) => (s.id === id ? { ...s, ...body, isActive: body.isActive ?? s.isActive } : s))
    return
  }
  await http(() => api.put(`/admin/skills/${id}`, body))
}

// DELETE /admin/skills/{id} (409 skill_in_use: вместо удаления можно выключить)
export async function deleteSkill(id: string): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    mockSkills = mockSkills.filter((s) => s.id !== id)
    return
  }
  await http(() => api.delete(`/admin/skills/${id}`))
}
