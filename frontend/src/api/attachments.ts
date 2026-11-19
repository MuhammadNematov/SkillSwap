import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import { mockVoiceUrls } from './mocks/exchanges'

// GET /attachments/{id}/url — временная подписанная ссылка на файл из чата
export async function getAttachmentUrl(id: string): Promise<{ url: string; fileName: string } | null> {
  if (USE_MOCKS) {
    const url = mockVoiceUrls.get(id)
    return url ? { url, fileName: 'voice.webm' } : null
  }
  try {
    const { data } = await api.get<{ url: string; expiresAt: string; fileName: string }>(`/attachments/${id}/url`)
    return { url: data.url, fileName: data.fileName }
  } catch (e) {
    throw toApiError(e)
  }
}
