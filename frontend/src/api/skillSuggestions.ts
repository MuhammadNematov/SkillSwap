import { api } from './client'
import { toApiError } from './errors'

// ───────── Предложения навыков для админа (/admin/skill-suggestions): docs/FRONTEND-ADMIN.md, раздел 6 ─────────
// Только настоящий бэкенд: заглушек нет.

export type SuggestionStatus = 'Pending' | 'Approved' | 'Merged' | 'Rejected'

export interface SimilarSkill {
  id: string
  nameTg: string
  nameRu: string
  nameEn: string
  isActive: boolean
}

export interface Suggestion {
  id: string
  userId: string
  userName: string
  categoryId: string
  categoryNameEn: string
  name: string // как написал автор
  status: SuggestionStatus
  createdAt: string
  resolvedAt: string | null
  resolvedSkillId: string | null
  resolvedById: string | null
  sameNameCount: number // неразобранных с таким же названием в категории, включая это (у разобранных 0)
  similar: SimilarSkill[] // до 5 навыков категории; только у неразобранных
}

export interface SuggestionList {
  items: Suggestion[]
  page: number
  pageSize: number
  totalCount: number
}

export interface ApproveSuggestionInput {
  nameTg: string
  nameRu: string
  nameEn: string
  synonyms?: string[]
  categoryId?: string // перенести навык в другую категорию
}

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /admin/skill-suggestions?status=&categoryId=&page=&pageSize=. Без status только неразобранные (старые сверху).
export function getSuggestions(f: { status?: SuggestionStatus; categoryId?: string; page: number; pageSize: number }): Promise<SuggestionList> {
  return http(() => api.get<SuggestionList>('/admin/skill-suggestions', { params: { status: f.status, categoryId: f.categoryId || undefined, page: f.page, pageSize: f.pageSize } }))
}

// POST /admin/skill-suggestions/{id}/approve: все три названия обязательны (409 skill_name_taken: в категории уже есть такой навык)
export function approveSuggestion(id: string, body: ApproveSuggestionInput): Promise<Suggestion> {
  return http(() => api.post<Suggestion>(`/admin/skill-suggestions/${encodeURIComponent(id)}/approve`, body))
}

// POST /admin/skill-suggestions/{id}/merge: предложенное название по умолчанию становится синонимом навыка
export function mergeSuggestion(id: string, body: { skillId: string; addAsSynonym: boolean }): Promise<Suggestion> {
  return http(() => api.post<Suggestion>(`/admin/skill-suggestions/${encodeURIComponent(id)}/merge`, body))
}

// POST /admin/skill-suggestions/{id}/reject: причина необязательна (до 500 символов), её увидит автор
export function rejectSuggestion(id: string, reason: string): Promise<Suggestion> {
  return http(() => api.post<Suggestion>(`/admin/skill-suggestions/${encodeURIComponent(id)}/reject`, reason.trim() ? { reason: reason.trim() } : {}))
}
