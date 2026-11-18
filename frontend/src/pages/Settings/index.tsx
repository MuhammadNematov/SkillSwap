import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getBlocks, unblockUser } from '../../api/blocks'
import { errorMessage } from '../../api/errors'
import { getMe, updateMe } from '../../api/me'
import { getLanguages } from '../../api/onboarding'
import { disableTwoFactor, getAuthSessions, revokeAllAuthSessions, revokeAuthSession } from '../../api/twoFactor'
import Modal from '../../components/Modal'
import Spinner from '../../components/Spinner'
import TwoFactorSetup from '../../components/TwoFactorSetup'
import { useAuth } from '../../hooks/useAuth'
import { formatDateTime } from '../../utils/dates'
import { pickName } from '../../utils/localized'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'

export default function Settings() {
  const { t } = useTranslation()
  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('settings.title')}</h1>
      <ProfileSection />
      <TwoFactorSection />
      <SessionsSection />
      <BlocksSection />
    </div>
  )
}

// ───────── Профиль: PUT /me ─────────
function ProfileSection() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const { user, updateUser } = useAuth()
  const me = useQuery({ queryKey: ['me'], queryFn: getMe })
  const langs = useQuery({ queryKey: ['languages'], queryFn: getLanguages })

  const [name, setName] = useState('')
  const [bio, setBio] = useState('')
  const [timeZone, setTimeZone] = useState('')
  const [languages, setLanguages] = useState<string[]>([])
  const [searchable, setSearchable] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const zones: string[] = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []

  useEffect(() => {
    if (!me.data) return
    setName(me.data.name)
    setBio(me.data.bio ?? '')
    setTimeZone(me.data.timeZone)
    setLanguages(me.data.languages)
    setSearchable(me.data.isSearchable)
  }, [me.data])

  const save = useMutation({
    mutationFn: updateMe,
    onSuccess: (p) => {
      qc.invalidateQueries({ queryKey: ['me'] })
      qc.invalidateQueries({ queryKey: ['profile'] })
      if (user) updateUser({ ...user, name: p.name, skillsComplete: p.skillsComplete })
      setSaved(true)
      setTimeout(() => setSaved(false), 3000)
    },
  })

  if (me.isLoading || langs.isLoading) return <Spinner />

  function submit() {
    setFormError(null)
    setSaved(false)
    if (!name.trim() || name.trim().length > 100) return setFormError(t('settings.badName'))
    if (languages.length === 0) return setFormError(t('onboarding.needLanguage'))
    if (zones.length > 0 && !zones.includes(timeZone.trim())) return setFormError(t('onboarding.badTimeZone'))
    save.mutate({
      name: name.trim(),
      bio: bio.trim() || null,
      uiLanguage: lang,
      timeZone: timeZone.trim(),
      languages,
      isSearchable: searchable,
    })
  }

  return (
    <section className="card space-y-4">
      <h2 className="font-bold text-slate-900">{t('settings.profile')}</h2>
      <label className="block">
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('registerPage.name')}</span>
        <input className="input" maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="block">
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('settings.bio')}</span>
        <textarea className="input resize-none" rows={3} maxLength={1000} value={bio} onChange={(e) => setBio(e.target.value)} />
      </label>
      <div>
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('onboarding.languages')}</span>
        <div className="flex flex-wrap gap-2">
          {(langs.data ?? []).map((l) => {
            const on = languages.includes(l.code)
            return (
              <button
                key={l.code}
                type="button"
                onClick={() => setLanguages((cur) => (on ? cur.filter((c) => c !== l.code) : [...cur, l.code]))}
                className={`rounded-xl border px-4 py-2 text-sm font-medium transition ${
                  on ? 'border-brand-600 bg-brand-600 text-white' : 'border-white/70 bg-white/50 text-slate-600 hover:bg-white/80'
                }`}
              >
                {pickName(l.names, lang)}
              </button>
            )
          })}
        </div>
      </div>
      <label className="block">
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('onboarding.timeZone')}</span>
        <input className="input" list="settings-tz" value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
        <datalist id="settings-tz">
          {zones.map((z) => (
            <option key={z} value={z} />
          ))}
        </datalist>
      </label>
      <label className="flex items-start gap-2 text-sm text-slate-700">
        <input type="checkbox" className="mt-0.5" checked={searchable} onChange={(e) => setSearchable(e.target.checked)} />
        <span>
          {t('settings.searchable')}
          <span className="block text-xs text-slate-400">{t('settings.searchableHint')}</span>
        </span>
      </label>
      {(formError || save.isError) && <p className={errorBox}>{formError ?? errorMessage(t, save.error)}</p>}
      {saved && <p className="text-sm text-emerald-700">{t('settings.saved')}</p>}
      <button className="btn-primary" disabled={save.isPending} onClick={submit}>
        {save.isPending ? t('common.loading') : t('settings.save')}
      </button>
    </section>
  )
}

// ───────── Двухфакторная аутентификация ─────────
function TwoFactorSection() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const { user, updateUser } = useAuth()
  const [enabling, setEnabling] = useState(false)
  const [disabling, setDisabling] = useState(false)
  const enabled = !!user?.twoFactorEnabled

  return (
    <section className="card space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-bold text-slate-900">{t('settings.twoFactor')}</h2>
        <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${enabled ? 'bg-emerald-100/80 text-emerald-700' : 'bg-slate-100/80 text-slate-600'}`}>
          {enabled ? t('settings.on') : t('settings.off')}
        </span>
      </div>
      <p className="text-sm text-slate-500">{t('settings.twoFactorHint')}</p>

      {enabling ? (
        <TwoFactorSetup
          onCancel={() => setEnabling(false)}
          onDone={() => {
            if (user) updateUser({ ...user, twoFactorEnabled: true })
            qc.invalidateQueries({ queryKey: ['me'] })
            setEnabling(false)
          }}
        />
      ) : enabled ? (
        <button className="btn-secondary" onClick={() => setDisabling(true)}>
          {t('settings.disable2fa')}
        </button>
      ) : (
        <button className="btn-primary" onClick={() => setEnabling(true)}>
          {t('settings.enable2fa')}
        </button>
      )}

      {disabling && <DisableModal onClose={() => setDisabling(false)} />}
    </section>
  )
}

// Отключение 2FA: нужны пароль и код из приложения
function DisableModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const { user, updateUser } = useAuth()
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const disable = useMutation({
    mutationFn: disableTwoFactor,
    onSuccess: () => {
      if (user) updateUser({ ...user, twoFactorEnabled: false })
      qc.invalidateQueries({ queryKey: ['me'] })
      onClose()
    },
  })

  return (
    <Modal title={t('settings.disable2fa')} onClose={onClose}>
      <form
        className="space-y-4"
        onSubmit={(e) => {
          e.preventDefault()
          disable.mutate({ password, code: code.trim() })
        }}
      >
        <input type="password" className="input" autoComplete="current-password" placeholder={t('loginPage.password')} value={password} onChange={(e) => setPassword(e.target.value)} />
        <input className="input text-center tracking-widest" inputMode="numeric" maxLength={6} placeholder="123456" value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} />
        {disable.isError && <p className={errorBox}>{errorMessage(t, disable.error)}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={!password || code.length !== 6 || disable.isPending}>
            {disable.isPending ? t('common.loading') : t('settings.disable2fa')}
          </button>
        </div>
      </form>
    </Modal>
  )
}

// ───────── Сеансы входа ─────────
function SessionsSection() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const { data = [], isLoading } = useQuery({ queryKey: ['authSessions'], queryFn: getAuthSessions })
  const refresh = () => qc.invalidateQueries({ queryKey: ['authSessions'] })
  const revoke = useMutation({ mutationFn: revokeAuthSession, onSuccess: refresh })
  const revokeAll = useMutation({ mutationFn: revokeAllAuthSessions, onSuccess: refresh })
  const error = revoke.error ?? revokeAll.error

  return (
    <section className="card space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="font-bold text-slate-900">{t('settings.sessions')}</h2>
        {data.some((s) => !s.isCurrent) && (
          <button className="btn-secondary px-3 py-1.5" disabled={revokeAll.isPending} onClick={() => revokeAll.mutate()}>
            {t('settings.revokeOthers')}
          </button>
        )}
      </div>
      {error && <p className={errorBox}>{errorMessage(t, error)}</p>}
      {isLoading ? (
        <Spinner />
      ) : (
        <ul className="space-y-2">
          {data.map((s) => (
            <li key={s.id} className="flex items-center gap-3 rounded-xl bg-white/50 px-3 py-2.5">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm font-semibold text-slate-900">
                  {s.deviceInfo ?? t('settings.unknownDevice')}
                  {s.isCurrent && <span className="ml-2 rounded-full bg-brand-100/80 px-2 py-0.5 text-xs font-medium text-brand-700">{t('settings.thisDevice')}</span>}
                </div>
                <div className="text-xs text-slate-400">
                  {s.ipAddress ?? '—'} · {t('settings.lastActive', { date: formatDateTime(s.lastActiveAt, lang) })}
                </div>
              </div>
              {!s.isCurrent && (
                <button className="text-sm font-medium text-red-600 hover:underline" disabled={revoke.isPending} onClick={() => revoke.mutate(s.id)}>
                  {t('settings.revoke')}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

// ───────── Заблокированные пользователи ─────────
function BlocksSection() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const { data = [], isLoading } = useQuery({ queryKey: ['blocks'], queryFn: getBlocks })
  const unblock = useMutation({
    mutationFn: unblockUser,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['blocks'] })
      qc.invalidateQueries({ queryKey: ['matches'] })
    },
  })

  return (
    <section className="card space-y-3">
      <h2 className="font-bold text-slate-900">{t('block.listTitle')}</h2>
      <p className="text-sm text-slate-500">{t('block.listHint')}</p>
      {unblock.isError && <p className={errorBox}>{errorMessage(t, unblock.error)}</p>}
      {isLoading ? (
        <Spinner />
      ) : data.length === 0 ? (
        <p className="text-sm text-slate-500">{t('block.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {data.map((b) => (
            <li key={b.userId} className="flex items-center gap-3 rounded-xl bg-white/50 px-3 py-2.5">
              <span className="min-w-0 flex-1 truncate text-sm font-semibold text-slate-900">{b.name}</span>
              <button className="text-sm font-medium text-brand-600 hover:underline" disabled={unblock.isPending} onClick={() => unblock.mutate(b.userId)}>
                {t('block.unblock')}
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
