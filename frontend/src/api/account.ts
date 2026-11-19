import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'

const sleep = (ms = 300) => new Promise((r) => setTimeout(r, ms))

export type ExportStatus = 'Pending' | 'Processing' | 'Ready' | 'Failed' | 'Expired'
export interface DataExport {
  id: string
  status: ExportStatus
  requestedAt: string
  readyAt: string | null
  expiresAt: string | null
}

// Бэкенд может отдать статус строкой или числом (порядок как в документации)
const STATUS_BY_INDEX: ExportStatus[] = ['Pending', 'Processing', 'Ready', 'Failed', 'Expired']
interface ExportDto extends Omit<DataExport, 'status'> {
  status: ExportStatus | number
}
const toExport = (d: ExportDto): DataExport => ({ ...d, status: typeof d.status === 'number' ? (STATUS_BY_INDEX[d.status] ?? 'Pending') : d.status })

let mockExport: DataExport | null = null

// POST /me/export — раз в сутки; ссылка на скачивание придёт на email
export async function requestExport(): Promise<DataExport> {
  if (USE_MOCKS) {
    await sleep()
    mockExport = { id: 'export-mock', status: 'Pending', requestedAt: new Date().toISOString(), readyAt: null, expiresAt: null }
    setTimeout(() => {
      if (mockExport) mockExport = { ...mockExport, status: 'Ready', readyAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 86_400_000).toISOString() }
    }, 4000)
    return mockExport
  }
  try {
    return toExport((await api.post<ExportDto>('/me/export')).data)
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /me/export — состояние последней выгрузки; null, если не запрашивали (404 export_not_found)
export async function getExport(): Promise<DataExport | null> {
  if (USE_MOCKS) {
    await sleep(150)
    return mockExport
  }
  try {
    return toExport((await api.get<ExportDto>('/me/export')).data)
  } catch (e) {
    const err = toApiError(e)
    if (err.code === 'export_not_found') return null
    throw err
  }
}

// DELETE /me — пароль обязателен, код 2FA нужен, если включена двухфакторная защита
export async function deleteAccount(body: { password: string; code: string; isRecoveryCode: boolean }): Promise<void> {
  if (USE_MOCKS) {
    await sleep(500)
    if (body.password.length < 1) throw new ApiError('invalid_password', 400)
    return
  }
  try {
    await api.delete('/me', { data: body })
  } catch (e) {
    throw toApiError(e)
  }
}
