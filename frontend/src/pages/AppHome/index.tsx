import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getDashboard } from '../../api/dashboard'
import Avatar from '../../components/Avatar'
import Icon, { type IconName } from '../../components/Icon'
import Spinner from '../../components/Spinner'
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
      <div>
        <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">
          {t('dashboard.greeting', { name: user?.name })} <span aria-hidden>👋</span>
        </h1>
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="grid gap-4 sm:grid-cols-3 lg:col-span-2">
          <StatCard icon="swap" tone="bg-brand-100 text-brand-700" label={t('dashboard.activeExchanges')} value={data.activeExchanges} />
          <StatCard icon="calendar" tone="bg-emerald-100 text-emerald-700" label={t('dashboard.upcomingLessons')} value={data.upcomingLessons} />
          <StatCard icon="star" tone="bg-amber-100 text-amber-600" label={t('dashboard.rating')} value={data.rating.toFixed(1)} />
        </div>

        <div className="flex flex-col justify-between rounded-xl2 border border-white/30 bg-gradient-to-br from-brand-600/95 to-brand-400/90 p-5 text-white shadow-glass backdrop-blur-xl">
          <div className="text-lg font-bold leading-snug">{t('dashboard.findPartnerTitle')}</div>
          <Link
            to="/app/matches"
            className="mt-4 inline-flex w-fit items-center gap-2 rounded-xl bg-white px-5 py-2.5 text-sm font-semibold text-brand-700 transition hover:bg-brand-50"
          >
            {t('dashboard.find')}
            <Icon name="arrowRight" className="h-4 w-4" />
          </Link>
        </div>
      </div>

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
          <ul className="mt-4 divide-y divide-white/70">
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
