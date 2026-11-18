import { useMutation, useQuery } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { createExchangeRequest } from '../api/matches'
import { errorMessage } from '../api/errors'
import type { MatchType, UserSkill } from '../api/types'
import { getUserProfile } from '../api/users'
import FormField from './FormField'
import Modal from './Modal'
import Select from './Select'

interface Props {
  partner: { id: string; name: string }
  partnerSkills: UserSkill[] // чему учит партнёр
  matchType?: MatchType
  onClose: () => void
  onSent: (partnerId: string) => void
}

interface FormValues {
  fromSkill: string // '' = предложу в сообщении
  toSkill: string
  durationDays: '30' | '60'
  message: string
}

// Заявка на обмен (ТЗ 5.3): навык, которому учу я; навык, которому учат меня; срок 30 или 60 дней; сообщение.
export default function ExchangeRequestModal({ partner, partnerSkills, matchType, onClose, onSent }: Props) {
  const { t } = useTranslation()
  const { data: me } = useQuery({ queryKey: ['profile', 'me'], queryFn: () => getUserProfile('me') })
  const mySkills = me?.teach ?? []

  const {
    control,
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({
    defaultValues: {
      fromSkill: '',
      toSkill: partnerSkills[0]?.skillId ?? '',
      durationDays: '30',
      message: '',
    },
  })

  const mutation = useMutation({
    mutationFn: (v: FormValues) =>
      createExchangeRequest({
        toUserId: partner.id,
        fromTeachesSkillId: v.fromSkill || undefined,
        toTeachesSkillId: v.toSkill,
        durationDays: Number(v.durationDays) as 30 | 60,
        message: v.message.trim(),
      }),
    onSuccess: () => onSent(partner.id),
  })

  const fromOptions = [
    { value: '', label: t('request.inMessage') },
    ...mySkills.map((s) => ({ value: s.skillId, label: s.name })),
  ]
  const toOptions = partnerSkills.map((s) => ({ value: s.skillId, label: s.name }))

  return (
    <Modal title={`${t('request.title')}: ${partner.name}`} onClose={onClose}>
      <form onSubmit={handleSubmit((v) => mutation.mutate(v))} className="space-y-4" noValidate>
        {matchType === 'partial' && (
          <p className="rounded-lg bg-amber-50/80 px-3 py-2 text-sm text-amber-800">{t('request.partialHint')}</p>
        )}

        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('request.iTeach')}</span>
            <Controller
              control={control}
              name="fromSkill"
              render={({ field }) => (
                <Select value={field.value} options={fromOptions} onChange={field.onChange} ariaLabel={t('request.iTeach')} />
              )}
            />
          </div>
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('request.iLearn')}</span>
            <Controller
              control={control}
              name="toSkill"
              render={({ field }) => (
                <Select value={field.value} options={toOptions} onChange={field.onChange} ariaLabel={t('request.iLearn')} />
              )}
            />
          </div>
        </div>

        <fieldset>
          <legend className="mb-1.5 text-sm font-medium text-slate-700">{t('request.duration')}</legend>
          <div className="flex gap-3">
            {(['30', '60'] as const).map((d) => (
              <label
                key={d}
                className="flex flex-1 cursor-pointer items-center gap-2 rounded-xl border border-white/70 bg-white/50 px-4 py-2.5 text-sm has-[:checked]:border-brand-400 has-[:checked]:bg-brand-50"
              >
                <input type="radio" value={d} className="accent-brand-600" {...register('durationDays')} />
                {t(`request.days${d}`)}
              </label>
            ))}
          </div>
        </fieldset>

        <FormField label={t('request.message')} error={errors.message}>
          <textarea
            rows={4}
            maxLength={500}
            className="input resize-none"
            placeholder={t('request.messagePlaceholder')}
            {...register('message', {
              required: 'validation.required',
              validate: (v) => v.trim().length > 0 || 'validation.required',
            })}
          />
        </FormField>

        {mutation.isError && (
          <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, mutation.error)}</p>
        )}

        <div className="flex justify-end gap-3 pt-1">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('request.cancel')}
          </button>
          <button className="btn-primary" disabled={mutation.isPending}>
            {mutation.isPending ? t('common.loading') : t('request.send')}
          </button>
        </div>
      </form>
    </Modal>
  )
}
