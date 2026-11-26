import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useAuth } from './useAuth'
import { getNotifications, getUnreadCount, toNotification } from '../api/notifications'
import { createHub, startHub } from '../api/realtime'
import type { AppNotification } from '../api/types'

// Опрос оставлен запасным вариантом (реже, пока работает хаб).
const POLL_MS = 30_000

export function useNotifications() {
  return useQuery({ queryKey: ['notifications'], queryFn: () => getNotifications(), refetchInterval: POLL_MS })
}

// Значок: отдельный лёгкий запрос. Ключ начинается с 'notifications', поэтому обновляется вместе со списком.
export function useUnreadCount() {
  const { data } = useQuery({ queryKey: ['notifications', 'unread'], queryFn: getUnreadCount, refetchInterval: POLL_MS })
  return data ?? 0
}

// Подписка на /hubs/notifications. Монтируется один раз в AppLayout.
export function useNotificationsHub() {
  const qc = useQueryClient()
  const { user } = useAuth()
  const userId = user?.id

  useEffect(() => {
    if (!userId) return
    const conn = createHub('/hubs/notifications')
    let stopped = false

    conn.on('notification', (dto) => {
      const n = toNotification(dto)
      // то же id — заменяем (NewMessages обновляется), иначе добавляем сверху
      qc.setQueryData<AppNotification[]>(['notifications'], (old) => {
        if (!old) return old
        return [n, ...old.filter((x) => x.id !== n.id)]
      })
      qc.invalidateQueries({ queryKey: ['notifications', 'unread'] })
    })
    // Telegram подключили или отключили (в боте или на другой вкладке): обновляем блок настроек
    conn.on('telegramStatus', () => {
      qc.invalidateQueries({ queryKey: ['telegram'] })
      qc.invalidateQueries({ queryKey: ['notificationSettings'] })
    })
    // после обрыва подтягиваем пропущенное
    conn.onreconnected(() => {
      qc.invalidateQueries({ queryKey: ['notifications'] })
      qc.invalidateQueries({ queryKey: ['telegram'] })
    })

    void startHub(conn).then((ok) => {
      if (stopped && ok) void conn.stop()
    })
    return () => {
      stopped = true
      void conn.stop()
    }
  }, [userId, qc])
}
