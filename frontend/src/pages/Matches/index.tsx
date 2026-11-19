import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { isErrorCode } from '../../api/errors'
import { useAuth } from '../../hooks/useAuth'
import { getMatches } from '../../api/matches'
import { getLanguages, getMySkills } from '../../api/onboarding'
import type { MatchItem, MatchType, SkillLevel } from '../../api/types'
import Avatar from '../../components/Avatar'
import ExchangeRequestModal from '../../components/ExchangeRequestModal'
import Icon from '../../components/Icon'
import RatingBadge from '../../components/RatingBadge'
import Select from '../../components/Select'
import SkillChip from '../../components/SkillChip'
import { pickName } from '../../utils/localized'
import { utcOffsetLabel } from '../../utils/dates'

interface Filters {
  skill: string
  lang: string
  level: string
  minRating: string
}

const EMPTY: Filters = { skill: '', lang: '', level: '', minRating: '' }
const LEVELS: SkillLevel[] = ['Beginner', 'Intermediate', 'Advanced']

export default function Matches() {
  const { t, i18n } = useTranslation()
  const [params] = useSearchParams()
  const q = params.get('q') ?? ''

  const [filters, setFilters] = useState<Filters>(EMPTY)
  const [tab, setTab] = useState<'all' | MatchType>('all')
  const [requestFor, setRequestFor] = useState<MatchItem | null>(null)
  const [requested, setRequested] = useState<Set<string>>(new Set())
  const [justSent, setJustSent] = useState(false)

  // Фильтр по навыку: только из моего «Хочу изучать» (так устроен подбор)
  const { data: mine } = useQuery({ queryKey: ['mySkills'], queryFn: getMySkills })
  const { data: languages = [] } = useQuery({ queryKey: ['languages'], queryFn: getLanguages })
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { user, updateUser } = useAuth()
  const { data: matches = [], isFetching, error } = useQuery({
    queryKey: ['matches', q, filters],
    queryFn: () =>
      getMatches({
        q: q || undefined,
        skill: filters.skill || undefined,
        lang: filters.lang || undefined,
        level: (filters.level || undefined) as SkillLevel | undefined,
        minRating: filters.minRating ? Number(filters.minRating) : undefined,
      }),
    placeholderData: keepPreviousData,
    retry: false,
  })

  // Анкета перестала быть полной (например, убрали язык): подбор закрыт, возвращаем на анкету
  useEffect(() => {
    if (user && isErrorCode(error, 'skills_incomplete')) updateUser({ ...user, skillsComplete: false })
  }, [error]) // eslint-disable-line react-hooks/exhaustive-deps

  // Сообщение «Заявка отправлена» пропадает само
  useEffect(() => {
    if (!justSent) return
    const timer = setTimeout(() => setJustSent(false), 4000)
    return () => clearTimeout(timer)
  }, [justSent])

  const set = (key: keyof Filters) => (value: string) => setFilters((f) => ({ ...f, [key]: value }))
  const any = { value: '', label: t('matches.filters.any') }
  const hasFilters = Object.values(filters).some(Boolean)

  const visible = tab === 'all' ? matches : matches.filter((m) => m.matchType === tab)
  const tabs: { key: 'all' | MatchType; label: string }[] = [
    { key: 'all', label: t('matches.tabAll', { count: matches.length }) },
    { key: 'full', label: t('matches.tabFull') },
    { key: 'partial', label: t('matches.tabPartial') },
  ]

  return (
    <div className="space-y-5">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('matches.title')}</h1>
      {q && <p className="text-sm text-slate-500">{t('matches.searchResults', { q })}</p>}

      {justSent && (
        <div className="flex items-center gap-2 rounded-xl border border-emerald-200 bg-emerald-50/80 px-4 py-3 text-sm text-emerald-800 backdrop-blur">
          <Icon name="check" className="h-4 w-4" />
          {t('matches.sent')}
        </div>
      )}

      <div className="grid gap-6 lg:grid-cols-[16rem_1fr]">
        <aside className="card h-fit space-y-4 lg:sticky lg:top-24">
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('matches.filters.skill')}</span>
            <Select
              value={filters.skill}
              onChange={set('skill')}
              ariaLabel={t('matches.filters.skill')}
              options={[any, ...(mine?.learn ?? []).map((s) => ({ value: s.skillId, label: s.names ? pickName(s.names, lang) : s.skillId }))]}
            />
          </div>
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('matches.filters.language')}</span>
            <Select
              value={filters.lang}
              onChange={set('lang')}
              ariaLabel={t('matches.filters.language')}
              options={[any, ...languages.map((l) => ({ value: l.code, label: pickName(l.names, lang) }))]}
            />
          </div>
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('matches.filters.level')}</span>
            <Select
              value={filters.level}
              onChange={set('level')}
              ariaLabel={t('matches.filters.level')}
              options={[any, ...LEVELS.map((l) => ({ value: l, label: t(`levels.${l}`) }))]}
            />
          </div>
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('matches.filters.rating')}</span>
            <Select
              value={filters.minRating}
              onChange={set('minRating')}
              ariaLabel={t('matches.filters.rating')}
              options={[any, ...['3', '4', '4.5'].map((v) => ({ value: v, label: t('matches.filters.ratingFrom', { value: v }) }))]}
            />
          </div>
          {hasFilters && (
            <button className="btn-secondary w-full" onClick={() => setFilters(EMPTY)}>
              {t('matches.filters.reset')}
            </button>
          )}
        </aside>

        <section className="min-w-0">
          <div className="mb-4 flex flex-wrap gap-2">
            {tabs.map((tb) => (
              <button
                key={tb.key}
                onClick={() => setTab(tb.key)}
                className={`rounded-xl border px-4 py-2 text-sm font-medium transition ${
                  tab === tb.key
                    ? 'border-brand-600 bg-brand-600 text-white shadow-lg shadow-brand-600/25'
                    : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-white'
                }`}
              >
                {tb.label}
              </button>
            ))}
          </div>

          {visible.length === 0 ? (
            <div className="card text-center text-sm text-slate-500">{isFetching ? t('common.loading') : t('matches.empty')}</div>
          ) : (
            <div className={`grid gap-4 sm:grid-cols-2 xl:grid-cols-3 ${isFetching ? 'opacity-70' : ''}`}>
              {visible.map((m) => {
                const done = requested.has(m.user.id)
                return (
                  <article key={m.user.id} className="card flex flex-col">
                    <div className="flex items-start gap-3">
                      <Avatar name={m.user.name} src={m.user.avatarUrl} className="h-14 w-14" />
                      <div className="min-w-0 flex-1">
                        <Link to={`/app/users/${m.user.id}`} className="block truncate font-bold text-slate-900 hover:text-brand-700">
                          {m.user.name}
                        </Link>
                        <RatingBadge ratingAvg={m.user.ratingAvg} ratingCount={m.user.ratingCount} />
                      </div>
                    </div>

                    <div className="mt-3 flex flex-wrap gap-1.5">
                      {m.teaches.map((s) => (
                        <SkillChip key={s.skillId} name={s.name} />
                      ))}
                    </div>

                    <div className="mt-2 text-xs text-slate-500">
                      {m.user.languages.map((l) => l.toUpperCase()).join(' · ')} · {utcOffsetLabel(m.user.timeZone)}
                    </div>

                    <span
                      className={`mt-3 w-fit rounded-full px-2.5 py-1 text-xs font-semibold ${
                        m.matchType === 'full' ? 'bg-emerald-100/80 text-emerald-700' : 'bg-amber-100/80 text-amber-700'
                      }`}
                    >
                      {m.matchType === 'full' ? t('matches.fullMatch') : t('matches.partialMatch')}
                    </span>

                    <div className="mt-auto flex gap-2 pt-4">
                      <Link to={`/app/users/${m.user.id}`} className="btn-secondary flex-1 px-3">
                        {t('matches.profile')}
                      </Link>
                      <button className="btn-primary flex-1 px-3" disabled={done} onClick={() => setRequestFor(m)}>
                        {done ? t('matches.requested') : t('matches.request')}
                      </button>
                    </div>
                  </article>
                )
              })}
            </div>
          )}
        </section>
      </div>

      {requestFor && (
        <ExchangeRequestModal
          partner={{ id: requestFor.user.id, name: requestFor.user.name }}
          partnerSkills={requestFor.teaches}
          matchType={requestFor.matchType}
          onClose={() => setRequestFor(null)}
          onSent={(id) => {
            setRequested((prev) => new Set(prev).add(id))
            setRequestFor(null)
            setJustSent(true)
          }}
        />
      )}
    </div>
  )
}
