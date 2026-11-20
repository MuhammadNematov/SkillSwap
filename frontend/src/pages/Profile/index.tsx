import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'
import { blockUser } from '../../api/blocks'
import { errorMessage } from '../../api/errors'
import Modal from '../../components/Modal'
import type { UserSkill } from '../../api/types'
import { getUserProfile } from '../../api/users'
import Avatar from '../../components/Avatar'
import ExchangeRequestModal from '../../components/ExchangeRequestModal'
import RatingBadge from '../../components/RatingBadge'
import ReviewList from '../../components/ReviewList'
import Spinner from '../../components/Spinner'
import { getLanguages } from '../../api/onboarding'
import { pickName } from '../../utils/localized'
import { utcOffsetLabel } from '../../utils/dates'

type Tab = 'about' | 'skills' | 'reviews'

function SkillList({ title, skills }: { title: string; skills: UserSkill[] }) {
  const { t } = useTranslation()
  return (
    <div>
      <h3 className="mb-3 font-bold text-slate-900">{title}</h3>
      <ul className="space-y-2">
        {skills.map((s) => (
          <li key={s.skillId} className="rounded-xl border border-ink-line bg-brand-50 px-4 py-3">
            <div className="flex items-center justify-between gap-3">
              <span className="font-semibold text-slate-900">{s.name}</span>
              <span className="rounded-full bg-brand-100/80 px-2.5 py-0.5 text-xs font-medium text-brand-700">
                {t(`levels.${s.level}`)}
              </span>
            </div>
            {s.description && <p className="mt-1 text-sm text-slate-500">{s.description}</p>}
          </li>
        ))}
      </ul>
    </div>
  )
}

// Профиль: свой (/app/profile) и чужой (/app/users/:id)
export default function Profile() {
  const { t, i18n } = useTranslation()
  const { id } = useParams()
  const userId = id ?? 'me'

  const [tab, setTab] = useState<Tab>('about')
  const [requesting, setRequesting] = useState(false)
  const [requested, setRequested] = useState(false)
  const [blocking, setBlocking] = useState(false)
  const navigate = useNavigate()
  const qc = useQueryClient()
  const block = useMutation({
    mutationFn: () => blockUser(userId, user?.name),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['matches'] })
      qc.invalidateQueries({ queryKey: ['blocks'] })
      navigate('/app/matches', { replace: true })
    },
  })

  const { data: user, isLoading, error } = useQuery({
    queryKey: ['profile', userId],
    queryFn: () => getUserProfile(userId),
  })

  const { data: languageList = [] } = useQuery({ queryKey: ['languages'], queryFn: getLanguages })

  if (isLoading) return <Spinner />
  if (error || !user) {
    return <div className="card text-sm text-red-700">{errorMessage(t, error)}</div>
  }

  const languageLabels = user.languages.map((code) => {
    const l = languageList.find((x) => x.code === code)
    return l ? pickName(l.names, i18n.resolvedLanguage ?? 'ru') : code.toUpperCase()
  })
  const tabs: { key: Tab; label: string }[] = [
    { key: 'about', label: t('profile.tabAbout') },
    { key: 'skills', label: t('profile.tabSkills') },
    { key: 'reviews', label: t('profile.tabReviews') },
  ]

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <section className="card overflow-hidden p-0">
        <div className="h-32 bg-brand-600 sm:h-40" />
        <div className="px-5 pb-5 sm:px-8">
          <div className="-mt-12 flex flex-wrap items-end justify-between gap-4">
            <Avatar name={user.name} src={user.avatarUrl} className="h-24 w-24 text-3xl ring-4 ring-white/80" />
            {!user.isMe && (
              <div className="flex gap-2">
                <button className="btn-secondary" onClick={() => setBlocking(true)}>
                  {t('block.action')}
                </button>
                <button className="btn-primary" disabled={requested} onClick={() => setRequesting(true)}>
                  {requested ? t('matches.requested') : t('profile.requestExchange')}
                </button>
              </div>
            )}
          </div>

          <h1 className="mt-3 text-2xl font-bold text-slate-900">{user.name}</h1>
          <div className="mt-1">
            <RatingBadge ratingAvg={user.ratingAvg} ratingCount={user.ratingCount} />
          </div>

          <div className="mt-5 grid grid-cols-3 gap-3">
            {[
              { label: t('profile.exchangesDone'), value: user.completedExchanges },
              { label: t('profile.rating'), value: user.ratingCount < 3 ? '—' : user.ratingAvg.toFixed(1) },
              { label: t('profile.reviewsCount'), value: user.ratingCount },
            ].map((s) => (
              <div key={s.label} className="rounded-xl border border-ink-line bg-brand-50 px-3 py-3 text-center">
                <div className="text-xl font-bold text-brand-700">{s.value}</div>
                <div className="text-xs text-slate-500">{s.label}</div>
              </div>
            ))}
          </div>
        </div>
      </section>

      <div className="flex flex-wrap gap-2">
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

      {tab === 'about' && (
        <section className="card space-y-5">
          <p className="text-slate-700">{user.bio || <span className="text-slate-400">{t('profile.noBio')}</span>}</p>
          <dl className="grid gap-4 sm:grid-cols-2">
            <div>
              <dt className="text-xs text-slate-400">{t('profile.languages')}</dt>
              <dd className="mt-1 font-medium text-slate-800">{languageLabels.join(', ')}</dd>
            </div>
            <div>
              <dt className="text-xs text-slate-400">{t('profile.timeZone')}</dt>
              <dd className="mt-1 font-medium text-slate-800">
                {user.timeZone} ({utcOffsetLabel(user.timeZone)})
              </dd>
            </div>
          </dl>
        </section>
      )}

      {tab === 'skills' && (
        <section className="card grid gap-6 md:grid-cols-2">
          <SkillList title={t('profile.teaches')} skills={user.teach} />
          <SkillList title={t('profile.learns')} skills={user.learn} />
        </section>
      )}

      {tab === 'reviews' && <ReviewList userId={user.id} />}

      {requesting && (
        <ExchangeRequestModal
          partner={{ id: user.id, name: user.name }}
          partnerSkills={user.teach}
          onClose={() => setRequesting(false)}
          onSent={() => {
            setRequested(true)
            setRequesting(false)
          }}
        />
      )}
      {blocking && (
        <Modal title={t('block.title', { name: user.name })} onClose={() => setBlocking(false)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-600">{t('block.text')}</p>
            {block.isError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, block.error)}</p>}
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={() => setBlocking(false)}>
                {t('common.cancel')}
              </button>
              <button className="btn-primary" disabled={block.isPending} onClick={() => block.mutate()}>
                {block.isPending ? t('common.loading') : t('block.confirm')}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  )
}
