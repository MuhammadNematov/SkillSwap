import { useMutation } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { proposeSession } from '../api/exchanges'
import { useInvalidateAll } from '../hooks/useSessionActions'
import { todayIso } from '../utils/dates'
import FormField from './FormField'
import Modal from './Modal'
import Select from './Select'

interface FormValues {
  date: string
  time: string
  teacher: 'me' | 'partner'
  topic: string
}

// Предложение слота (ТЗ 6.2): дата, время, кто преподаёт, тема. Длительность всегда 45 минут.
// Ограничения (не меньше чем за час, не позже конца обмена, конфликты) проверяет сервер.
export default function SessionProposalModal({
  exchangeId,
  partnerName,
  onClose,
}: {
  exchangeId: string
  partnerName: string
  onClose: () => void
}) {
  const { t } = useTranslation()
  const invalidate = useInvalidateAll()
  const {
    register,
    control,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({ defaultValues: { date: '', time: '', teacher: 'me', topic: '' } })

  const mutation = useMutation({
    mutationFn: (v: FormValues) =>
      proposeSession({
        exchangeId,
        // Дата и время вводятся в часовом поясе пользователя, на сервер уходит UTC
        startAt: new Date(`${v.date}T${v.time}`).toISOString(),
        teacherIsMe: v.teacher === 'me',
        topic: v.topic.trim() || undefined,
      }),
    onSuccess: () => {
      invalidate()
      onClose()
    },
  })

  return (
    <Modal title={t('session.proposeTitle')} onClose={onClose}>
      <form onSubmit={handleSubmit((v) => mutation.mutate(v))} className="space-y-4" noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField label={t('session.date')} error={errors.date}>
            <input type="date" min={todayIso()} className="input" {...register('date', { required: 'validation.required' })} />
          </FormField>
          <FormField label={t('session.time')} error={errors.time}>
            <input type="time" className="input" {...register('time', { required: 'validation.required' })} />
          </FormField>
        </div>

        <div>
          <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('session.whoTeaches')}</span>
          <Controller
            control={control}
            name="teacher"
            render={({ field }) => (
              <Select
                value={field.value}
                onChange={(v) => field.onChange(v)}
                ariaLabel={t('session.whoTeaches')}
                options={[
                  { value: 'me', label: t('session.iTeach') },
                  { value: 'partner', label: partnerName },
                ]}
              />
            )}
          />
        </div>

        <FormField label={t('session.topic')} error={errors.topic} hint={t('session.duration')}>
          <input className="input" maxLength={100} placeholder={t('session.topicPlaceholder')} {...register('topic')} />
        </FormField>

        {mutation.isError && (
          <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, mutation.error)}</p>
        )}

        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={mutation.isPending}>
            {mutation.isPending ? t('common.loading') : t('session.propose')}
          </button>
        </div>
      </form>
    </Modal>
  )
}
