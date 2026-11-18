import { useMutation, useQueryClient } from '@tanstack/react-query'
import { confirmSession, declineSession } from '../api/exchanges'

// После любого действия обновляем все данные, которые могли измениться
export function useInvalidateAll() {
  const qc = useQueryClient()
  return () => {
    for (const key of ['exchanges', 'exchange', 'sessions', 'messages', 'requests', 'dashboard', 'conversations', 'reviews', 'notifications']) {
      qc.invalidateQueries({ queryKey: [key] })
    }
  }
}

// Принять / отклонить предложенный партнёром слот
export function useSessionActions() {
  const invalidate = useInvalidateAll()
  const confirm = useMutation({ mutationFn: confirmSession, onSuccess: invalidate })
  const decline = useMutation({ mutationFn: declineSession, onSuccess: invalidate })
  return { confirm, decline }
}
