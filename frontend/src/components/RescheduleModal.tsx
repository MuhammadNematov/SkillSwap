import { useMutation } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { rescheduleSession } from '../api/exchanges'
import type { LessonSession } from '../api/types'
import { useInvalidateAll } from '../hooks/useSessionActions'
import { formatDateTime, todayIso } from '../utils/dates'
import FormField from './FormField'
import Modal from './Modal'

interface FormValues {
  date: string
  time: string
}

// Перенос урока: новое время уходит партнёру как предложение. Правила (не позднее чем за 2 часа и т.д.) проверяет сервер.
export default function RescheduleModal({ session, onClose }: { session: LessonSession; onClose: () => void }) {
  const { t, i18n } = useTranslation()
  const invalidate = useInvalidateAll()
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({ defaultValues: { date: '', time: '' } })

  const mutation = useMutation({
    mutationFn: (v: FormValues) => rescheduleSession({ id: session.id, startAt: new Date(`${v.date}T${v.time}`).toISOString() }),
    onSuccess: () => {
      invalidate()
      onClose()
    },
  })

  return (
    <Modal title={t('session.rescheduleTitle')} onClose={onClose}>
      <form onSubmit={handleSubmit((v) => mutation.mutate(v))} className="space-y-4" noValidate>
        <p className="text-sm text-slate-700">{t('session.rescheduleText', { when: formatDateTime(session.startAt, i18n.resolvedLanguage ?? 'ru') })}</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField label={t('session.date')} error={errors.date}>
            <input type="date" min={todayIso()} className="input" {...register('date', { required: 'validation.required' })} />
          </FormField>
          <FormField label={t('session.time')} error={errors.time}>
            <input type="time" className="input" {...register('time', { required: 'validation.required' })} />
          </FormField>
        </div>
        {mutation.isError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, mutation.error)}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={mutation.isPending}>
            {mutation.isPending ? t('common.loading') : t('session.rescheduleConfirm')}
          </button>
        </div>
      </form>
    </Modal>
  )
}
