import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'

// GET /attachments/{id}/url — временная подписанная ссылка на файл из чата
export async function getAttachmentUrl(id: string): Promise<{ url: string; fileName: string } | null> {
  if (USE_MOCKS) return null
  try {
    const { data } = await api.get<{ url: string; expiresAt: string; fileName: string }>(`/attachments/${id}/url`)
    return { url: data.url, fileName: data.fileName }
  } catch (e) {
    throw toApiError(e)
  }
}
