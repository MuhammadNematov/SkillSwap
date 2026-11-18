import { useQuery } from '@tanstack/react-query'
import { getNotifications } from '../api/notifications'

// Пока опрашиваем сервер раз в 30 секунд. Когда будет готов хаб /hubs/notifications (SignalR),
// заменим опрос на подписку — страницы менять не придётся.
export function useNotifications() {
  return useQuery({ queryKey: ['notifications'], queryFn: () => getNotifications(), refetchInterval: 30_000 })
}

export function useUnreadCount() {
  const { data } = useNotifications()
  return (data ?? []).filter((n) => !n.isRead).length
}
