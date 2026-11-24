import { api } from './client'
import { USE_MOCKS } from './config'
import { getAdminCases, getBanRequests, getModCases, type CaseList, type CaseStatus, type ModScope } from './cases'

// Счётчики для обзора и меню кабинетов сотрудников. Отдельных эндпоинтов нет: берётся totalCount из обычных списков
// с pageSize=1 (сервер считает общее число и отдаёт одну запись). Для моков считаем по длине списка.
async function total(path: string, params: Record<string, string | number | undefined>): Promise<number> {
  const { data } = await api.get<CaseList<unknown>>(path, { params: { ...params, page: 1, pageSize: 1 } })
  return data.totalCount
}

export async function countAdminCases(status: CaseStatus): Promise<number> {
  if (USE_MOCKS) return (await getAdminCases(status)).length
  return total('/admin/cases', { status })
}

export async function countBanRequests(status: 'Pending'): Promise<number> {
  if (USE_MOCKS) return (await getBanRequests(status)).length
  return total('/admin/ban-requests', { status })
}

export async function countSuggestions(): Promise<number> {
  if (USE_MOCKS) return 0
  // Без status сервер отдаёт только неразобранные предложения
  return total('/admin/skill-suggestions', {})
}

export async function countBlockedUsers(): Promise<number> {
  if (USE_MOCKS) return 0
  return total('/admin/users', { status: 'Blocked' })
}

export async function countModCases(scope: ModScope): Promise<number> {
  if (USE_MOCKS) return (await getModCases(scope)).length
  return total('/moderation/cases', { scope })
}
