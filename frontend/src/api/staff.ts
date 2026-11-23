import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import type { UiLanguage } from './types'

// ───────── Сотрудники и приглашения (docs/FRONTEND-ADMIN.md, разделы 1, 2, 5) ─────────

export type StaffRole = 'Moderator' | 'Admin'
export type InvitationStatus = 'Pending' | 'Accepted' | 'Revoked' | 'Expired'

// Подтверждение действия паролем и кодом 2FA: уходит в теле КАЖДОГО запроса, код одноразовый
export interface Confirmation {
  password: string
  code: string
  isRecoveryCode: boolean
}

export interface StaffInvitation {
  id: string
  email: string
  targetRole: StaffRole
  status: InvitationStatus
  invitedBy: { id: string; name: string }
  createdAt: string
  expiresAt: string
  acceptedAt: string | null
  revokedAt: string | null
  resendCount: number
}
export interface InvitationPreview {
  email: string
  targetRole: StaffRole
  invitedByName: string
  expiresAt: string
}
export interface ModeratorLoad {
  inProgress: number
  waitingResponse: number
  appealReview: number
  resolvedLast30Days: number
}
export interface Moderator {
  id: string
  name: string
  email: string | null
  removed: boolean
  createdAt: string
  personalLinked: boolean
  load: ModeratorLoad
}
export interface RemoveModeratorResult {
  casesReturned: number
  appealReviewsReturned: number
  personalUnlinked: boolean
}
export interface AcceptResult {
  twoFactorToken: string
}

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))
let seq = 0
const MOCK_CODE = '123456'

function checkMockConfirmation(c: Confirmation) {
  if (!c.password) throw new ApiError('step_up_required', 400)
  if (!c.code) throw new ApiError('two_factor_code_required', 400)
  if (c.code !== MOCK_CODE) throw new ApiError('invalid_two_factor_code', 400)
}

const daysAgo = (d: number) => new Date(Date.now() - d * 86400_000).toISOString()
let mockInvitations: StaffInvitation[] = [
  { id: 'inv-1', email: 'new.moderator@test.com', targetRole: 'Moderator', status: 'Pending', invitedBy: { id: 'a', name: 'Админ Тест' }, createdAt: daysAgo(1), expiresAt: new Date(Date.now() + 2 * 86400_000).toISOString(), acceptedAt: null, revokedAt: null, resendCount: 0 },
  { id: 'inv-2', email: 'old@test.com', targetRole: 'Moderator', status: 'Expired', invitedBy: { id: 'a', name: 'Админ Тест' }, createdAt: daysAgo(9), expiresAt: daysAgo(6), acceptedAt: null, revokedAt: null, resendCount: 1 },
]
const emptyLoad: ModeratorLoad = { inProgress: 0, waitingResponse: 0, appealReview: 0, resolvedLast30Days: 0 }
let mockModerators: Moderator[] = [
  { id: 'mod-1', name: 'Модератор Тест', email: 'mod@test.com', removed: false, createdAt: daysAgo(40), personalLinked: true, load: { inProgress: 2, waitingResponse: 1, appealReview: 0, resolvedLast30Days: 7 } },
  { id: 'mod-2', name: 'Второй Модератор', email: 'mod2@test.com', removed: false, createdAt: daysAgo(12), personalLinked: false, load: emptyLoad },
  { id: 'mod-3', name: 'Бывший Модератор', email: 'gone@test.com', removed: true, createdAt: daysAgo(80), personalLinked: false, load: emptyLoad },
]
const mockLinks = new Map<string, InvitationPreview>() // токен → приглашение

async function http<T>(run: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await run()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /admin/staff-invitations
export async function getInvitations(status?: InvitationStatus): Promise<StaffInvitation[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockInvitations.filter((i) => !status || i.status === status)
  }
  const data = await http(() => api.get<{ items: StaffInvitation[] }>('/admin/staff-invitations', { params: { status, pageSize: 50 } }))
  return data.items
}

// POST /admin/staff-invitations. Токена в ответе нет: он приходит только в письме. В моках отдаём ссылку для проверки.
export async function inviteStaff(req: { email: string; role: StaffRole; language: UiLanguage; confirmation: Confirmation }): Promise<{ mockToken?: string }> {
  if (USE_MOCKS) {
    await sleep()
    checkMockConfirmation(req.confirmation)
    if (mockInvitations.some((i) => i.email === req.email && i.status === 'Pending')) throw new ApiError('invitation_already_exists', 409)
    const token = `invite-${Date.now()}-${++seq}`
    const expiresAt = new Date(Date.now() + 72 * 3600_000).toISOString()
    mockLinks.set(token, { email: req.email, targetRole: req.role, invitedByName: 'Админ Тест', expiresAt })
    mockInvitations = [{ id: `inv-${token}`, email: req.email, targetRole: req.role, status: 'Pending', invitedBy: { id: 'a', name: 'Админ Тест' }, createdAt: new Date().toISOString(), expiresAt, acceptedAt: null, revokedAt: null, resendCount: 0 }, ...mockInvitations]
    return { mockToken: token }
  }
  await http(() => api.post('/admin/staff-invitations', req))
  return {}
}

// POST /admin/staff-invitations/{id}/revoke
export async function revokeInvitation(id: string, confirmation: Confirmation): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    checkMockConfirmation(confirmation)
    mockInvitations = mockInvitations.map((i) => (i.id === id ? { ...i, status: 'Revoked', revokedAt: new Date().toISOString() } : i))
    return
  }
  await http(() => api.post(`/admin/staff-invitations/${id}/revoke`, { confirmation }))
}

// POST /admin/staff-invitations/{id}/resend: новая ссылка и новые 72 часа
export async function resendInvitation(id: string, confirmation: Confirmation, language: UiLanguage): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    checkMockConfirmation(confirmation)
    mockInvitations = mockInvitations.map((i) =>
      i.id === id ? { ...i, status: 'Pending', resendCount: i.resendCount + 1, expiresAt: new Date(Date.now() + 72 * 3600_000).toISOString() } : i,
    )
    return
  }
  await http(() => api.post(`/admin/staff-invitations/${id}/resend`, { confirmation, language }))
}

// GET /admin/staff/moderators?removed=
export async function getModerators(removed: boolean): Promise<Moderator[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockModerators.filter((m) => m.removed === removed)
  }
  const data = await http(() => api.get<{ items: Moderator[] }>('/admin/staff/moderators', { params: { removed, pageSize: 100 } }))
  return data.items
}

// POST /admin/staff/moderators/{id}/remove
export async function removeModerator(id: string, req: { reason: string; confirmation: Confirmation }): Promise<RemoveModeratorResult> {
  if (USE_MOCKS) {
    await sleep()
    checkMockConfirmation(req.confirmation)
    const m = mockModerators.find((x) => x.id === id)
    mockModerators = mockModerators.map((x) => (x.id === id ? { ...x, removed: true } : x))
    return { casesReturned: m ? m.load.inProgress + m.load.waitingResponse : 0, appealReviewsReturned: m?.load.appealReview ?? 0, personalUnlinked: !!m?.personalLinked }
  }
  const data = await http(() => api.post<RemoveModeratorResult>(`/admin/staff/moderators/${id}/remove`, req))
  return { casesReturned: data.casesReturned, appealReviewsReturned: data.appealReviewsReturned, personalUnlinked: data.personalUnlinked }
}

// GET /staff-invitations/{token} (без входа)
export async function getStaffInvitation(token: string): Promise<InvitationPreview> {
  if (USE_MOCKS) {
    await sleep()
    const inv = mockLinks.get(token)
    if (!inv) throw new ApiError('invitation_not_found', 404)
    return inv
  }
  return http(() => api.get<InvitationPreview>(`/staff-invitations/${encodeURIComponent(token)}`))
}

// POST /staff-invitations/accept (без входа). Ответ: requires2faSetup + twoFactorToken, дальше настройка 2FA по этому токену.
export async function acceptStaffInvitation(req: { token: string; name: string; password: string; uiLanguage: UiLanguage }): Promise<AcceptResult> {
  if (USE_MOCKS) {
    await sleep()
    const inv = mockLinks.get(req.token)
    if (!inv) throw new ApiError('invitation_not_found', 404)
    mockLinks.delete(req.token)
    mockInvitations = mockInvitations.map((i) => (i.email === inv.email && i.status === 'Pending' ? { ...i, status: 'Accepted', acceptedAt: new Date().toISOString() } : i))
    return { twoFactorToken: `mock-setup-${req.token}` }
  }
  const data = await http(() => api.post<{ twoFactorToken: string | null }>('/staff-invitations/accept', req))
  if (!data.twoFactorToken) throw new ApiError('UNKNOWN', 0)
  return { twoFactorToken: data.twoFactorToken }
}

