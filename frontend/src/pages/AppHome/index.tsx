import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getDashboard } from '../../api/dashboard'
import Avatar from '../../components/Avatar'
import Icon, { type IconName } from '../../components/Icon'
import Spinner from '../../components/Spinner'
import SwapStage from '../../components/SwapScene'
import { useAuth } from '../../hooks/useAuth'
import { formatDateTime, formatTime, isJoinWindowOpen, isSameDay } from '../../utils/dates'

function StatCard({ icon, label, value, tone }: { icon: IconName; label: string; value: string | number; tone: string }) {
  return (
    <div className="card flex items-center gap-4">
      <span className={`flex h-12 w-12 items-center justify-center rounded-xl ${tone}`}>
        <Icon name={icon} className="h-6 w-6" />
      </span>
      <div>
        <div className="text-xs text-slate-500">{label}</div>
        <div className="text-2xl font-bold text-slate-900">{value}</div>
      </div>
    </div>
  )
}

// Главная страница личного кабинета (по макету)
export default function AppHome() {
  const { t, i18n } = useTranslation()
  const { user } = useAuth()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { data, isLoading } = useQuery({ queryKey: ['dashboard'], queryFn: getDashboard })

  if (isLoading || !data) return <Spinner />

  const lesson = data.nextLesson
  const lessonWhen = lesson
    ? isSameDay(new Date(lesson.startsAt), new Date())
      ? t('dashboard.today', { time: formatTime(lesson.startsAt, lang) })
      : formatDateTime(lesson.startsAt, lang)
    : ''

  return (
    <div className="space-y-6">
      <section className="overflow-hidden rounded-2xl border border-ink-line bg-surface">
        <SwapStage centerWidth={560} links={false} compact className="min-h-[340px]">
          <div className="w-full px-4 py-10 text-center">
            <p className="text-xs font-bold uppercase tracking-[0.14em] text-brand-600">{t('dashboard.heroEyebrow', { name: user?.name?.split(' ')[0] })}</p>
            <h1 className="mt-3 text-4xl font-extrabold leading-[1.08] tracking-tight text-ink sm:text-[52px]">
              {t('dashboard.heroTitle1')}
              <br />
              <span className="text-brand-600">{t('dashboard.heroTitle2')}</span>
            </h1>
            <p className="mx-auto mt-4 max-w-md text-base leading-relaxed text-ink-soft">{t('dashboard.heroText')}</p>
            <div className="mt-6 flex flex-wrap justify-center gap-2.5">
              <Link to="/app/matches" className="btn-primary h-12 rounded-2xl px-6 text-[15px]">
                {t('dashboard.heroMatches')}
              </Link>
              <Link to="/app/calendar" className="btn-secondary h-12 rounded-2xl px-6 text-[15px]">
                {t('dashboard.heroSchedule')}
              </Link>
            </div>
          </div>
        </SwapStage>
      </section>

      <div className="grid gap-4">
        <div className="grid gap-4 sm:grid-cols-3">
          <StatCard icon="swap" tone="bg-brand-100 text-brand-700" label={t('dashboard.activeExchanges')} value={data.activeExchanges} />
          <StatCard icon="calendar" tone="bg-emerald-100 text-emerald-700" label={t('dashboard.upcomingLessons')} value={data.upcomingLessons} />
          <StatCard icon="star" tone="bg-amber-100 text-amber-600" label={t('dashboard.rating')} value={data.rating == null ? '—' : data.rating.toFixed(1)} />
        </div>

      </div>

      {data.actions.length > 0 && (
        <section className="card">
          <h2 className="text-lg font-bold text-ink">{t('dashboard.actionsTitle')}</h2>
          <ul className="mt-3 divide-y divide-ink-line">
            {data.actions.map((a, i) => (
              <li key={i} className="flex items-center justify-between gap-3 py-3">
                <span className="text-sm text-ink">{t(`dashboard.action.${a.type}`)}</span>
                <Link
                  to={a.requestId ? '/app/exchanges?tab=incoming' : a.type === 'WriteReview' ? '/app/reviews' : `/app/exchanges/${a.exchangeId}`}
                  className="btn-secondary px-4 py-2"
                >
                  {t('dashboard.actionOpen')}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      <div className="grid gap-6 lg:grid-cols-2">
        <section className="card">
          <h2 className="text-lg font-bold text-slate-900">{t('dashboard.nextLesson')}</h2>
          {lesson ? (
            <div className="mt-4 flex items-center gap-4">
              <Avatar name={lesson.partnerName} className="h-14 w-14" />
              <div className="min-w-0 flex-1">
                <div className="truncate font-semibold text-slate-900">{lesson.title}</div>
                <div className="mt-1 flex items-center gap-1.5 text-sm text-brand-600">
                  <Icon name="calendar" className="h-4 w-4" />
                  {lessonWhen}
                </div>
                <div className="mt-0.5 text-xs text-slate-400">{lesson.partnerName}</div>
              </div>
            </div>
          ) : (
            <p className="mt-4 text-sm text-slate-500">{t('dashboard.noLesson')}</p>
          )}
          {lesson && (
            <div className="mt-5">
              {isJoinWindowOpen(lesson.startsAt) ? (
                <Link to={`/app/call/${lesson.id}`} className="btn-primary">
                  <Icon name="video" className="h-4 w-4" />
                  {t('dashboard.join')}
                </Link>
              ) : (
                <>
                  <button className="btn-primary" disabled title={t('dashboard.joinHint')}>
                    <Icon name="video" className="h-4 w-4" />
                    {t('dashboard.join')}
                  </button>
                  <p className="mt-2 text-xs text-slate-400">{t('dashboard.joinHint')}</p>
                </>
              )}
            </div>
          )}
        </section>

        <section className="card">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-bold text-slate-900">{t('dashboard.recommended')}</h2>
            <Link to="/app/matches" className="flex items-center gap-1 text-sm font-medium text-brand-600 hover:underline">
              {t('dashboard.all')}
              <Icon name="arrowRight" className="h-4 w-4" />
            </Link>
          </div>
          <ul className="mt-4 divide-y divide-ink-line">
            {data.recommended.map((p) => (
              <li key={p.id} className="flex items-center gap-3 py-3">
                <Avatar name={p.name} />
                <div className="min-w-0 flex-1">
                  <div className="truncate font-semibold text-slate-900">{p.name}</div>
                  <div className="flex items-center gap-1.5 text-xs text-slate-500">
                    <span className={`h-2 w-2 rounded-full ${p.online ? 'bg-emerald-500' : 'bg-slate-300'}`} />
                    {p.skills.join(' · ')}
                  </div>
                </div>
                <span className="flex items-center gap-1 text-sm font-medium text-amber-600">
                  <Icon name="star" className="h-4 w-4" />
                  {p.rating.toFixed(1)}
                </span>
                <Link to={`/app/users/${p.id}`} className="btn-secondary px-4 py-2">
                  {t('dashboard.profile')}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      </div>
    </div>
  )
}
