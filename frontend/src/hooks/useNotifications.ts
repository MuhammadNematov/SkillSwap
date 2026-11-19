import { useQuery } from '@tanstack/react-query'
import { getNotifications, getUnreadCount } from '../api/notifications'

// Пока опрашиваем сервер раз в 30 секунд. Когда будет готов хаб /hubs/notifications (SignalR),
// заменим опрос на подписку — страницы менять не придётся.
export function useNotifications() {
  return useQuery({ queryKey: ['notifications'], queryFn: () => getNotifications(), refetchInterval: 30_000 })
}

// Значок: отдельный лёгкий запрос. Ключ начинается с 'notifications', поэтому обновляется вместе со списком.
export function useUnreadCount() {
  const { data } = useQuery({ queryKey: ['notifications', 'unread'], queryFn: getUnreadCount, refetchInterval: 30_000 })
  return data ?? 0
}
