import { useQuery } from '@tanstack/react-query'
import { countAdminCases, countBanRequests, countBlockedUsers, countModCases, countSuggestions } from '../api/staffCounts'

const REFRESH_MS = 60_000

function useCount(key: string, fn: () => Promise<number>, enabled: boolean) {
  return useQuery({ queryKey: ['staffCount', key], queryFn: fn, enabled, refetchInterval: REFRESH_MS, staleTime: 15_000 })
}

// Счётчики админа и суперадмина: те же числа идут в бейджи меню и на «Обзор» (общий кэш, один запрос на число)
export function useAdminCounts(enabled = true) {
  return {
    openCases: useCount('openCases', () => countAdminCases('Open'), enabled),
    bans: useCount('bans', () => countBanRequests('Pending'), enabled),
    suggestions: useCount('suggestions', countSuggestions, enabled),
    appeals: useCount('appeals', () => countAdminCases('Appealed'), enabled),
    blocked: useCount('blocked', countBlockedUsers, enabled),
  }
}

// Счётчики модератора
export function useModCounts(enabled = true) {
  return {
    queue: useCount('modQueue', () => countModCases('Queue'), enabled),
    mine: useCount('modMine', () => countModCases('Mine'), enabled),
    appeals: useCount('modAppeals', () => countModCases('Appeals'), enabled),
  }
}
