import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { CATEGORIES, openCase, type CaseCategory, type CaseType } from '../api/cases'
import { errorMessage } from '../api/errors'
import { getMessages } from '../api/exchanges'
import { formatDateTime } from '../utils/dates'
import Modal from './Modal'

interface Props {
  exchangeId: string
  partnerName: string
  // Тип задан заранее: спор по отзыву (с reviewId) или жалоба из звонка
  fixed?: { type: CaseType; reviewId?: string }
  fromCall?: boolean
  onClose: () => void
}

const MAX_EVIDENCE = 20

// Подача обращения (раздел 8.2 ТЗ): тип, категория, описание 20–2000 символов, сообщения как доказательства
export default function CaseModal({ exchangeId, partnerName, fixed, fromCall, onClose }: Props) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const navigate = useNavigate()
  const [type, setType] = useState<CaseType>(fixed?.type ?? 'Conduct')
  const [category, setCategory] = useState<CaseCategory>(CATEGORIES[fixed?.type ?? 'Conduct'][0])
  const [description, setDescription] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [formError, setFormError] = useState<string | null>(null)

  const withEvidence = type !== 'ReviewDispute'
  const { data: messages = [] } = useQuery({ queryKey: ['messages', exchangeId], queryFn: () => getMessages(exchangeId), enabled: withEvidence })
  const evidence = messages.filter((m) => m.type === 'Text').slice(-40)

  const send = useMutation({
    mutationFn: openCase,
    onSuccess: (c) => {
      onClose()
      navigate(`/app/cases/${c.id}`)
    },
  })

  function chooseType(next: CaseType) {
    setType(next)
    setCategory(CATEGORIES[next][0])
  }

  function toggle(id: string) {
    setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : p.length >= MAX_EVIDENCE ? p : [...p, id]))
  }

  function submit() {
    const len = description.trim().length
    if (len < 20 || len > 2000) return setFormError(t('cases.descriptionLength'))
    setFormError(null)
    send.mutate({
      type,
      category,
      exchangeId,
      reviewId: fixed?.reviewId ?? null,
      description: description.trim(),
      messageIds: withEvidence ? picked : [],
      fromCall: !!fromCall,
    })
  }

  const title = fixed?.type === 'ReviewDispute' ? t('cases.disputeTitle') : fromCall ? t('cases.fromCallTitle') : t('cases.openTitle', { name: partnerName })

  return (
    <Modal title={title} onClose={onClose}>
      <div className="space-y-4">
        {fromCall && <p className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800">{t('cases.fromCallHint')}</p>}

        {!fixed && !fromCall && (
          <div className="flex flex-wrap gap-2">
            {(['Conduct', 'NoShowAbuse'] as const).map((k) => (
              <button key={k} type="button" className={`chip ${type === k ? 'chip-on' : ''}`} onClick={() => chooseType(k)}>
                {t(`cases.type.${k}`)}
              </button>
            ))}
          </div>
        )}

        <fieldset className="space-y-1.5">
          <legend className="mb-1 text-sm font-medium text-slate-700">{t('cases.category')}</legend>
          {CATEGORIES[type as CaseType].map((c) => (
            <label key={c} className="flex cursor-pointer items-center gap-2 text-sm text-slate-700">
              <input type="radio" name="category" checked={category === c} onChange={() => setCategory(c)} />
              {t(`cases.categories.${c}`)}
            </label>
          ))}
        </fieldset>

        <div>
          <textarea
            rows={5}
            maxLength={2000}
            className="input resize-none"
            placeholder={t('cases.descriptionPlaceholder')}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
          />
          <div className="mt-1 text-right text-xs text-slate-400">{description.trim().length}/2000</div>
        </div>

        {withEvidence && evidence.length > 0 && (
          <div>
            <div className="mb-1 flex items-center justify-between text-sm font-medium text-slate-700">
              <span>{t('cases.evidence')}</span>
              <span className="text-xs font-normal text-slate-400">
                {picked.length}/{MAX_EVIDENCE}
              </span>
            </div>
            <ul className="max-h-48 space-y-1 overflow-y-auto rounded-lg border border-ink-line p-2">
              {evidence.map((m) => (
                <li key={m.id}>
                  <label className="flex cursor-pointer items-start gap-2 rounded px-1 py-1 text-xs hover:bg-brand-50">
                    <input type="checkbox" className="mt-0.5" checked={picked.includes(m.id)} onChange={() => toggle(m.id)} />
                    <span className="min-w-0 flex-1">
                      <span className="text-slate-400">
                        {m.senderIsMe ? t('session.you') : partnerName}, {formatDateTime(m.createdAt, lang)}
                      </span>
                      <span className="block break-words text-slate-700">{m.deleted ? t('chat.deleted') : m.text}</span>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          </div>
        )}

        <p className="text-xs text-slate-500">{t('cases.privacyHint')}</p>
        {(formError || send.isError) && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{formError ?? errorMessage(t, send.error)}</p>}
        <div className="flex justify-end gap-3">
          <button className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={send.isPending} onClick={submit}>
            {send.isPending ? t('common.loading') : t('cases.submit')}
          </button>
        </div>
      </div>
    </Modal>
  )
}
