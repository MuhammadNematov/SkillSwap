import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { cancelSession } from '../api/exchanges'
import type { LessonSession } from '../api/types'
import { useInvalidateAll } from '../hooks/useSessionActions'
import { formatDateTime } from '../utils/dates'
import Modal from './Modal'

// Отмена не позднее чем за 2 часа — без последствий, позже — «поздняя отмена» (ТЗ 6.2)
export default function CancelSessionModal({ session, onClose }: { session: LessonSession; onClose: () => void }) {
  const { t, i18n } = useTranslation()
  const invalidate = useInvalidateAll()
  const mutation = useMutation({
    mutationFn: () => cancelSession(session.id),
    onSuccess: () => {
      invalidate()
      onClose()
    },
  })

  const isLate = session.status === 'Scheduled' && new Date(session.startAt).getTime() - Date.now() < 2 * 3_600_000
  const when = formatDateTime(session.startAt, i18n.resolvedLanguage ?? 'ru')

  return (
    <Modal title={t('session.cancelTitle')} onClose={onClose}>
      <p className="text-sm text-slate-700">{t('session.cancelText', { when })}</p>
      {isLate && (
        <p className="mt-3 rounded-lg bg-amber-50/80 px-3 py-2 text-sm text-amber-800">{t('session.lateCancelWarning')}</p>
      )}
      {mutation.isError && (
        <p className="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, mutation.error)}</p>
      )}
      <div className="mt-5 flex justify-end gap-3">
        <button className="btn-secondary" onClick={onClose}>
          {t('session.keep')}
        </button>
        <button className="btn-primary" disabled={mutation.isPending} onClick={() => mutation.mutate()}>
          {mutation.isPending ? t('common.loading') : t('session.cancelConfirm')}
        </button>
      </div>
    </Modal>
  )
}
