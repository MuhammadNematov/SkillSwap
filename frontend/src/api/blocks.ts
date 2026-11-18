import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'

export interface BlockedUser {
  userId: string
  name: string
  avatarUrl: string | null
  blockedAt: string
}

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))
let mock: BlockedUser[] = []

// POST /users/{id}/block (204, безопасно при повторе). Блокировка действует в обе стороны.
export async function blockUser(userId: string, name = ''): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    if (!mock.some((b) => b.userId === userId)) mock = [...mock, { userId, name, avatarUrl: null, blockedAt: new Date().toISOString() }]
    return
  }
  try {
    await api.post(`/users/${userId}/block`)
  } catch (e) {
    throw toApiError(e)
  }
}

// DELETE /users/{id}/block (204)
export async function unblockUser(userId: string): Promise<void> {
  if (USE_MOCKS) {
    await sleep()
    mock = mock.filter((b) => b.userId !== userId)
    return
  }
  try {
    await api.delete(`/users/${userId}/block`)
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /me/blocks — кого я заблокировал (профиль заблокированного через /users/{id} недоступен)
export async function getBlocks(): Promise<BlockedUser[]> {
  if (USE_MOCKS) {
    await sleep()
    return mock
  }
  try {
    const { data } = await api.get<BlockedUser[]>('/me/blocks')
    return data
  } catch (e) {
    throw toApiError(e)
  }
}
