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
import LanguageSelect from '../../components/LanguageSelect'
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
    { key: 'all', label: t('matches.tabAll') },
    { key: 'full', label: t('matches.tabFull') },
    { key: 'partial', label: t('matches.tabPartial') },
  ]

  return (
    <div className="space-y-5">
      <h1 className="text-2xl font-bold text-ink sm:text-3xl">{t('matches.title')}</h1>
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
              searchPlaceholder={t('onboarding.searchSkill')}
              options={[any, ...(mine?.learn ?? []).map((s) => ({ value: s.skillId, label: s.names ? pickName(s.names, lang) : s.skillId }))]}
            />
          </div>
          <div>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('matches.filters.language')}</span>
            <LanguageSelect value={filters.lang} onChange={set('lang')} ariaLabel={t('matches.filters.language')} options={languages} anyLabel={t('matches.filters.any')} />
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
                    ? 'border-brand-600 bg-brand-600 text-white'
                    : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-surface'
                }`}
              >
                {tb.label}
              </button>
            ))}
          </div>

          {visible.length === 0 ? (
            <div className="card text-center text-sm text-slate-500">{isFetching ? t('common.loading') : t('matches.empty')}</div>
          ) : (
            <div className={`space-y-4 ${isFetching ? 'opacity-70' : ''}`}>
              {visible.map((m) => {
                const done = requested.has(m.user.id)
                const full = m.matchType === 'full'
                return (
                  <article key={m.user.id} className="card flex flex-wrap items-center gap-5 p-5">
                    <div className="flex min-w-0 flex-1 basis-[300px] items-start gap-4">
                      <Avatar name={m.user.name} src={m.user.avatarUrl} className="h-16 w-16" />
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <Link to={`/app/users/${m.user.id}`} className="truncate text-lg font-bold text-ink hover:text-brand-700">
                            {m.user.name}
                          </Link>
                          <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${full ? 'bg-[#E5F7EC] text-[#12A150]' : 'bg-amber-100 text-amber-700'}`}>
                            {full ? t('matches.fullMatch') : t('matches.partialMatch')}
                          </span>
                        </div>
                        <div className="mt-1 flex flex-wrap items-center gap-x-3 text-xs text-ink-soft">
                          <RatingBadge ratingAvg={m.user.ratingAvg} ratingCount={m.user.ratingCount} />
                          <span>
                            {m.user.languages.map((l) => l.toUpperCase()).join(' · ')} · {utcOffsetLabel(m.user.timeZone)}
                          </span>
                        </div>
                        <div className="mt-3 flex flex-wrap gap-1.5">
                          {m.teaches.map((s) => (
                            <SkillChip key={s.skillId} name={s.name} />
                          ))}
                        </div>
                      </div>
                    </div>
                    <div className="flex w-full gap-2 sm:w-auto sm:flex-col">
                      <button className="btn-primary flex-1 px-5" disabled={done} onClick={() => setRequestFor(m)}>
                        {done ? t('matches.requested') : t('matches.request')}
                      </button>
                      <Link to={`/app/users/${m.user.id}`} className="btn-secondary flex-1 px-5">
                        {t('matches.profile')}
                      </Link>
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
