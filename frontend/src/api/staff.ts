import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'

// ───────── Сотрудники и приглашения ─────────
// Эндпоинтов пока нет в openapi.json, формы взяты из ТЗ (раздел с таблицей API) и могут уточниться.
// Вся работа с бэкендом только здесь: при уточнении правим этот файл.

export type StaffRole = 'Moderator' | 'Admin'

export interface StaffMember {
  id: string
  name: string
  email: string
  role: StaffRole
  twoFactorEnabled: boolean
}
export interface StaffInvitationInfo {
  email: string
  role: StaffRole
  expiresAt: string
}

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))
let seq = 0
const inviteBase = (role: StaffRole) => (role === 'Admin' ? '/superadmin/staff' : '/admin/staff')

let mockStaff: StaffMember[] = [
  { id: 'staff-1', name: 'Модератор Тест', email: 'mod@test.com', role: 'Moderator', twoFactorEnabled: true },
  { id: 'staff-2', name: 'Админ Тест', email: 'admin@test.com', role: 'Admin', twoFactorEnabled: true },
]
const mockInvites = new Map<string, StaffInvitationInfo>()

// GET /admin/staff
export async function getStaff(): Promise<StaffMember[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockStaff
  }
  try {
    return (await api.get<StaffMember[]>('/admin/staff')).data
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /admin/staff/invite (модератор) или /superadmin/staff/invite (админ, только суперадмин)
export async function inviteStaff(email: string, role: StaffRole): Promise<{ token?: string }> {
  if (USE_MOCKS) {
    await sleep()
    const token = `invite-${Date.now()}-${++seq}`
    mockInvites.set(token, { email, role, expiresAt: new Date(Date.now() + 72 * 3600_000).toISOString() })
    return { token } // в настоящем режиме письмо уходит на почту, токена фронт не видит
  }
  try {
    await api.post(`${inviteBase(role)}/invite`, { email })
    return {}
  } catch (e) {
    throw toApiError(e)
  }
}

// DELETE /admin/staff/{id} или /superadmin/staff/{id}
export async function removeStaff(member: StaffMember): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    mockStaff = mockStaff.filter((m) => m.id !== member.id)
    return
  }
  try {
    await api.delete(`${inviteBase(member.role)}/${member.id}`)
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /staff-invitations/{token} (публичный)
export async function getStaffInvitation(token: string): Promise<StaffInvitationInfo> {
  if (USE_MOCKS) {
    await sleep()
    const inv = mockInvites.get(token)
    if (!inv) throw new ApiError('invitation_not_found', 404)
    return inv
  }
  try {
    return (await api.get<StaffInvitationInfo>(`/staff-invitations/${encodeURIComponent(token)}`)).data
  } catch (e) {
    throw toApiError(e)
  }
}

// POST /staff-invitations/{token}/accept (публичный). 2FA настроится при первом входе.
export async function acceptStaffInvitation(token: string, body: { name: string; password: string }): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    const inv = mockInvites.get(token)
    if (!inv) throw new ApiError('invitation_not_found', 404)
    mockInvites.delete(token)
    mockStaff = [...mockStaff, { id: `staff-${++seq}-${Date.now()}`, name: body.name, email: inv.email, role: inv.role, twoFactorEnabled: false }]
    return
  }
  try {
    await api.post(`/staff-invitations/${encodeURIComponent(token)}/accept`, body)
  } catch (e) {
    throw toApiError(e)
  }
}
