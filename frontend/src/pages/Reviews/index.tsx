import { useQueries, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getExchanges } from '../../api/exchanges'
import { getExchangeReviews } from '../../api/reviews'
import type { Exchange, ExchangeReviews, MyReview } from '../../api/types'
import Avatar from '../../components/Avatar'
import ReviewList from '../../components/ReviewList'
import CaseModal from '../../components/CaseModal'
import ReviewModal from '../../components/ReviewModal'
import Spinner from '../../components/Spinner'
import StarRating from '../../components/StarRating'
import { useAuth } from '../../hooks/useAuth'
import { formatDateTime } from '../../utils/dates'

interface Writing {
  exchange: Exchange
  state: ExchangeReviews
  existing?: MyReview | null
}

// Отзывы: что нужно написать, мои отзывы и что написали обо мне.
// Отзывы слепые: виден чужой отзыв только после публикации (когда написали оба или через 7 дней).
export default function Reviews() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { user } = useAuth()
  const [writing, setWriting] = useState<Writing | null>(null)
  const [disputing, setDisputing] = useState<{ exchange: Exchange; reviewId: string } | null>(null)

  const exchanges = useQuery({ queryKey: ['exchanges'], queryFn: getExchanges })
  // Отзыв возможен только по закрытому обмену
  const closed = (exchanges.data ?? []).filter((e) => e.status === 'Completed' || e.status === 'Aborted')
  const states = useQueries({
    queries: closed.map((e) => ({ queryKey: ['exchangeReviews', e.id], queryFn: () => getExchangeReviews(e.id) })),
  })

  if (exchanges.isLoading) return <Spinner />

  const rows = closed.flatMap((exchange, i) => {
    const state = states[i]?.data
    return state ? [{ exchange, state }] : []
  })
  const toWrite = rows.filter((r) => r.state.canWrite)
  const mine = rows.filter((r) => r.state.myReview)

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('reviews.title')}</h1>

      <section className="space-y-3">
        <h2 className="font-bold text-slate-900">{t('reviews.toWrite')}</h2>
        {states.some((s) => s.isLoading) && toWrite.length === 0 ? (
          <Spinner />
        ) : toWrite.length === 0 ? (
          <div className="card text-sm text-slate-500">{t('reviews.noneToWrite')}</div>
        ) : (
          toWrite.map(({ exchange, state }) => (
            <div key={exchange.id} className="card flex items-center gap-3">
              <Avatar name={exchange.partner.name} src={exchange.partner.avatarUrl} className="h-11 w-11" />
              <div className="min-w-0 flex-1">
                <div className="font-bold text-slate-900">{exchange.partner.name}</div>
                {state.deadlineAt && <div className="text-xs text-slate-400">{t('reviews.deadline', { date: formatDateTime(state.deadlineAt, lang) })}</div>}
                {state.isFullExchange && <div className="text-xs text-emerald-700">{t('reviews.fullExchangeHint')}</div>}
              </div>
              <button className="btn-primary px-4 py-2" onClick={() => setWriting({ exchange, state })}>
                {t('reviews.write')}
              </button>
            </div>
          ))
        )}
      </section>

      {mine.length > 0 && (
        <section className="space-y-3">
          <h2 className="font-bold text-slate-900">{t('reviews.mine')}</h2>
          {mine.map(({ exchange, state }) => {
            const r = state.myReview!
            return (
              <article key={exchange.id} className="card space-y-3">
                <div className="flex items-center gap-3">
                  <Avatar name={exchange.partner.name} src={exchange.partner.avatarUrl} className="h-10 w-10" />
                  <div className="min-w-0 flex-1">
                    <div className="font-semibold text-slate-900">{exchange.partner.name}</div>
                    <div className="text-xs text-slate-400">
                      {r.status === 'Pending'
                        ? r.publishAt
                          ? t('reviews.pendingUntil', { date: formatDateTime(r.publishAt, lang) })
                          : t('reviews.pending')
                        : t(`reviews.status.${r.status}`)}
                    </div>
                  </div>
                  <StarRating value={Math.round(r.score)} />
                </div>
                <p className="whitespace-pre-wrap break-words text-sm text-slate-700">{r.text}</p>
                {state.canEdit && (
                  <div className="flex justify-end">
                    <button className="btn-secondary px-4 py-2" onClick={() => setWriting({ exchange, state, existing: r })}>
                      {t('reviews.edit')}
                    </button>
                  </div>
                )}
                {state.reviewAboutMe && (
                  <div className="rounded-lg bg-brand-50 p-3">
                    <div className="flex items-center justify-between gap-2 text-xs text-slate-500">
                      <span>{t('reviews.aboutMeFrom', { name: exchange.partner.name })}</span>
                      <StarRating value={Math.round(state.reviewAboutMe.score)} />
                    </div>
                    <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-700">{state.reviewAboutMe.text}</p>
                    <button
                      className="mt-2 text-xs font-semibold text-brand-700 hover:underline"
                      onClick={() => setDisputing({ exchange, reviewId: state.reviewAboutMe!.id })}
                    >
                      {t('cases.disputeReview')}
                    </button>
                  </div>
                )}
              </article>
            )
          })}
        </section>
      )}

      <section className="space-y-3">
        <h2 className="font-bold text-slate-900">{t('reviews.aboutMe')}</h2>
        {user && <ReviewList userId={user.id} />}
      </section>

      {disputing && (
        <CaseModal
          exchangeId={disputing.exchange.id}
          partnerName={disputing.exchange.partner.name}
          fixed={{ type: 'ReviewDispute', reviewId: disputing.reviewId }}
          onClose={() => setDisputing(null)}
        />
      )}
      {writing && (
        <ReviewModal exchange={writing.exchange} existing={writing.existing} oneSided={writing.state.oneSided} onClose={() => setWriting(null)} />
      )}
    </div>
  )
}
