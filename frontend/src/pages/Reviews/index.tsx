import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getExchanges } from '../../api/exchanges'
import { getUserReviews } from '../../api/reviews'
import type { Exchange } from '../../api/types'
import Avatar from '../../components/Avatar'
import ReviewModal from '../../components/ReviewModal'
import Spinner from '../../components/Spinner'
import StarRating from '../../components/StarRating'
import { useAuth } from '../../hooks/useAuth'
import { formatDateTime } from '../../utils/dates'

// Отзывы: что нужно написать и что написали обо мне
export default function Reviews() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { user } = useAuth()
  const [writing, setWriting] = useState<Exchange | null>(null)

  const exchanges = useQuery({ queryKey: ['exchanges'], queryFn: getExchanges })
  const received = useQuery({ queryKey: ['reviews', user?.id], queryFn: () => getUserReviews(user!.id), enabled: !!user })

  if (exchanges.isLoading || received.isLoading) return <Spinner />

  const list = exchanges.data ?? []
  const toWrite = list.filter((e) => e.review?.canWrite && !e.review.submitted)
  const waiting = list.filter((e) => e.review?.submitted)

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('reviews.title')}</h1>

      <section className="space-y-3">
        <h2 className="font-bold text-slate-900">{t('reviews.toWrite')}</h2>
        {toWrite.length === 0 ? (
          <div className="card text-sm text-slate-500">{t('reviews.noneToWrite')}</div>
        ) : (
          toWrite.map((e) => (
            <div key={e.id} className="card flex items-center gap-3">
              <Avatar name={e.partner.name} className="h-11 w-11" />
              <div className="min-w-0 flex-1">
                <div className="font-bold text-slate-900">{e.partner.name}</div>
                {e.review?.deadlineAt && (
                  <div className="text-xs text-slate-400">{t('reviews.deadline', { date: formatDateTime(e.review.deadlineAt, lang) })}</div>
                )}
              </div>
              <button className="btn-primary px-4 py-2" onClick={() => setWriting(e)}>
                {t('reviews.write')}
              </button>
            </div>
          ))
        )}
        {waiting.map((e) => (
          <div key={e.id} className="card flex items-center gap-3 opacity-80">
            <Avatar name={e.partner.name} className="h-11 w-11" />
            <div className="min-w-0 flex-1 font-bold text-slate-900">{e.partner.name}</div>
            <span className="text-xs text-slate-500">{t('reviews.waitingPartner')}</span>
          </div>
        ))}
      </section>

      <section className="space-y-3">
        <h2 className="font-bold text-slate-900">{t('reviews.aboutMe')}</h2>
        {(received.data ?? []).length === 0 ? (
          <div className="card text-sm text-slate-500">{t('profile.noReviews')}</div>
        ) : (
          received.data!.map((r) => (
            <article key={r.id} className="card">
              <div className="flex items-center justify-between gap-3">
                <span className="font-semibold text-slate-900">{r.authorName}</span>
                <StarRating value={Math.round(r.score)} />
              </div>
              <p className="mt-2 text-sm text-slate-700">{r.text}</p>
              <div className="mt-2 flex items-center gap-2 text-xs text-slate-400">
                {formatDateTime(r.createdAt, lang)}
                {r.isFullExchange && <span className="rounded-full bg-brand-100/80 px-2 py-0.5 text-brand-700">{t('profile.fullExchange')}</span>}
              </div>
            </article>
          ))
        )}
      </section>

      {writing && <ReviewModal exchange={writing} onClose={() => setWriting(null)} />}
    </div>
  )
}
