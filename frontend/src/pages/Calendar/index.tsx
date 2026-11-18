import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getMySessions } from '../../api/exchanges'
import type { LessonSession } from '../../api/types'
import CancelSessionModal from '../../components/CancelSessionModal'
import SessionItem from '../../components/SessionItem'
import Spinner from '../../components/Spinner'
import { dayKey, formatDay, isSameDay } from '../../utils/dates'

const LESSON_MS = 45 * 60_000

// Календарь уроков по всем обменам: ближайшие по дням, ниже прошедшие
export default function Calendar() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [cancelling, setCancelling] = useState<LessonSession | null>(null)
  const { data = [], isLoading } = useQuery({ queryKey: ['sessions', 'mine'], queryFn: getMySessions })

  if (isLoading) return <Spinner />

  const now = Date.now()
  const isUpcoming = (s: LessonSession) =>
    (s.status === 'Scheduled' || s.status === 'Proposed' || s.status === 'InProgress') && new Date(s.startAt).getTime() + LESSON_MS >= now
  const upcoming = data.filter(isUpcoming)
  const past = data.filter((s) => !isUpcoming(s)).reverse()

  // Группировка ближайших по дням
  const days: { key: string; first: string; items: LessonSession[] }[] = []
  for (const s of upcoming) {
    const key = dayKey(s.startAt)
    const group = days.find((d) => d.key === key)
    if (group) group.items.push(s)
    else days.push({ key, first: s.startAt, items: [s] })
  }

  function dayTitle(iso: string) {
    const date = new Date(iso)
    const today = new Date()
    if (isSameDay(date, today)) return t('calendar.today')
    if (isSameDay(date, new Date(today.getTime() + 86_400_000))) return t('calendar.tomorrow')
    return formatDay(iso, lang)
  }

  const row = (s: LessonSession) => (
    <div key={s.id}>
      <SessionItem session={s} onCancel={setCancelling} showPartner />
      <Link to={`/app/exchanges/${s.exchangeId}`} className="ml-1 mt-1 inline-block text-xs text-brand-600 hover:underline">
        {t('exchanges.open')}
      </Link>
    </div>
  )

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('calendar.title')}</h1>

      <section className="space-y-5">
        <h2 className="font-bold text-slate-900">{t('calendar.upcoming')}</h2>
        {days.length === 0 ? (
          <div className="card text-sm text-slate-500">{t('calendar.none')}</div>
        ) : (
          days.map((d) => (
            <div key={d.key}>
              <div className="mb-2 text-sm font-semibold capitalize text-brand-700">{dayTitle(d.first)}</div>
              <ul className="space-y-3">{d.items.map((s) => <li key={s.id}>{row(s)}</li>)}</ul>
            </div>
          ))
        )}
      </section>

      {past.length > 0 && (
        <section className="space-y-3">
          <h2 className="font-bold text-slate-900">{t('calendar.past')}</h2>
          <ul className="space-y-3 opacity-80">{past.map((s) => <li key={s.id}>{row(s)}</li>)}</ul>
        </section>
      )}

      {cancelling && <CancelSessionModal session={cancelling} onClose={() => setCancelling(null)} />}
    </div>
  )
}
