import { api } from './client'
import type { BanRequest, CaseDecision, CaseStatus, CaseType } from './cases'
import { toApiError } from './errors'

// ───────── Пользователи для админа (/admin/users, /admin/warnings): docs/FRONTEND-ADMIN.md, раздел 4 ─────────
// Только настоящий бэкенд: заглушек нет. Подтверждение паролем и кодом 2FA для этих действий не нужно, нужна причина 10–500 символов (в журнал аудита).

export type AdminUserStatus = 'Active' | 'Blocked' | 'Deleted'
export type AdminAgeGroup = 'Minor' | 'Adult' | 'Unknown'

export interface AdminUserListItem {
  id: string
  name: string
  email: string | null // у удалённых null
  status: AdminUserStatus
  ageGroup: AdminAgeGroup
  createdAt: string
  lastActiveAt: string | null
  ratingAvg: number
  ratingCount: number
  activeWarnings: number
  openCases: number
}

export interface AdminUserList {
  items: AdminUserListItem[]
  page: number
  pageSize: number
  totalCount: number
}

export interface AdminWarning {
  id: string
  caseId: string
  issuedById: string
  reason: string
  issuedAt: string
  expiresAt: string
  revokedAt: string | null
  isActive: boolean
}

export interface AdminUserCase {
  id: string
  type: CaseType
  status: CaseStatus
  decision: CaseDecision | null
  role: 'Opener' | 'Against' // подал сам или подано на него
  createdAt: string
}

export interface BlockEvent {
  action: 'UserBlocked' | 'UserUnblocked'
  at: string
  byId: string | null
  reason: string | null
  source: 'manual' | 'ban_request' | null
}

export interface AdminUser {
  user: AdminUserListItem
  uiLanguage: string
  timeZone: string
  emailConfirmed: boolean
  activeExchanges: number
  warnings: AdminWarning[]
  cases: AdminUserCase[]
  banRequests: BanRequest[]
  blockHistory: BlockEvent[]
}

export interface AdminUserFilters {
  search?: string
  status?: AdminUserStatus
  page: number
  pageSize: number
}

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /admin/users?search=&status=&page=&pageSize=
export function getAdminUsers(f: AdminUserFilters): Promise<AdminUserList> {
  return http(() => api.get<AdminUserList>('/admin/users', { params: { search: f.search?.trim() || undefined, status: f.status, page: f.page, pageSize: f.pageSize } }))
}

// GET /admin/users/{id}: карточка (404 user_not_found, в том числе для служебных аккаунтов)
export function getAdminUser(id: string): Promise<AdminUser> {
  return http(() => api.get<AdminUser>(`/admin/users/${encodeURIComponent(id)}`))
}

// POST /admin/users/{id}/block: те же последствия, что у одобренного запроса на бан. Возвращает карточку.
export function blockUser(id: string, reason: string): Promise<AdminUser> {
  return http(() => api.post<AdminUser>(`/admin/users/${encodeURIComponent(id)}/block`, { reason }))
}

// POST /admin/users/{id}/unblock: прерванные обмены не возвращаются. Возвращает карточку.
export function unblockUser(id: string, reason: string): Promise<AdminUser> {
  return http(() => api.post<AdminUser>(`/admin/users/${encodeURIComponent(id)}/unblock`, { reason }))
}

// POST /admin/warnings/{id}/revoke: снять предупреждение вручную. Возвращает карточку пользователя.
export function revokeWarning(warningId: string, reason: string): Promise<AdminUser> {
  return http(() => api.post<AdminUser>(`/admin/warnings/${encodeURIComponent(warningId)}/revoke`, { reason }))
}
