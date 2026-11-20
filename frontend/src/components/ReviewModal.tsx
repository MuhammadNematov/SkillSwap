import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { saveReview } from '../api/reviews'
import type { Exchange, MyReview, ReviewScores } from '../api/types'
import { useInvalidateAll } from '../hooks/useSessionActions'
import Modal from './Modal'
import StarRating from './StarRating'

const CRITERIA: (keyof ReviewScores)[] = ['knowledge', 'explaining', 'punctuality', 'politeness']

// Отзыв: 4 критерия по 1–5 и текст 20–1000 символов (ТЗ 8.1). Править можно, пока отзыв не опубликован.
export default function ReviewModal({
  exchange,
  existing,
  oneSided,
  onClose,
}: {
  exchange: Pick<Exchange, 'id'> & { partner: { name: string } }
  existing?: MyReview | null // правка своего ещё не опубликованного отзыва
  oneSided?: boolean // отзыв опубликуется сразу
  onClose: () => void
}) {
  const { t } = useTranslation()
  const invalidate = useInvalidateAll()
  const qc = useQueryClient()
  const [scores, setScores] = useState<ReviewScores>(existing?.scores ?? { knowledge: 0, explaining: 0, punctuality: 0, politeness: 0 })
  const [text, setText] = useState(existing?.text ?? '')
  const [formError, setFormError] = useState<string | null>(null)

  const send = useMutation({
    mutationFn: saveReview,
    onSuccess: () => {
      invalidate()
      qc.invalidateQueries({ queryKey: ['exchangeReviews'] })
      onClose()
    },
  })

  function submit() {
    const len = text.trim().length
    if (Object.values(scores).some((v) => v === 0)) return setFormError(t('reviews.needScores'))
    if (len < 20 || len > 1000) return setFormError(t('reviews.textLength'))
    setFormError(null)
    send.mutate({ exchangeId: exchange.id, update: !!existing, ...scores, text: text.trim() })
  }

  return (
    <Modal title={t('reviews.writeTitle', { name: exchange.partner.name })} onClose={onClose}>
      <div className="space-y-4">
        {CRITERIA.map((c) => (
          <div key={c} className="flex items-center justify-between gap-3">
            <span className="text-sm font-medium text-slate-700">{t(`reviews.criteria.${c}`)}</span>
            <StarRating value={scores[c]} label={t(`reviews.criteria.${c}`)} onChange={(v) => setScores((s) => ({ ...s, [c]: v }))} />
          </div>
        ))}
        <div>
          <textarea
            rows={5}
            maxLength={1000}
            className="input resize-none"
            placeholder={t('reviews.textPlaceholder')}
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
          <div className="mt-1 text-right text-xs text-slate-400">{text.trim().length}/1000</div>
        </div>
        <p className="text-xs text-slate-500">{oneSided ? t('reviews.oneSidedHint') : existing ? t('reviews.editHint') : t('reviews.blindHint')}</p>
        {(formError || send.isError) && (
          <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{formError ?? errorMessage(t, send.error)}</p>
        )}
        <div className="flex justify-end gap-3">
          <button className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={send.isPending} onClick={submit}>
            {send.isPending ? t('common.loading') : existing ? t('reviews.saveChanges') : t('reviews.send')}
          </button>
        </div>
      </div>
    </Modal>
  )
}
