import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { getBlocks, unblockUser } from '../../api/blocks'
import { errorMessage } from '../../api/errors'
import { deleteAccount, getExport, requestExport } from '../../api/account'
import { deleteAvatar, getMe, updateMe, uploadAvatar } from '../../api/me'
import { getLanguages } from '../../api/onboarding'
import { disableTwoFactor, getAuthSessions, revokeAllAuthSessions, revokeAuthSession } from '../../api/twoFactor'
import Avatar from '../../components/Avatar'
import { AVATAR_PRESETS, presetToFile } from '../../utils/avatars'
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
      <AccountSection />
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

  const fileRef = useRef<HTMLInputElement>(null)
  const [avatarError, setAvatarError] = useState<string | null>(null)
  const afterAvatar = (url: string | null) => {
    qc.invalidateQueries({ queryKey: ['me'] })
    qc.invalidateQueries({ queryKey: ['profile'] })
    if (user) updateUser({ ...user, avatarUrl: url })
  }
  const [editingAvatar, setEditingAvatar] = useState(false)
  const upload = useMutation({ mutationFn: uploadAvatar, onSuccess: (url) => { afterAvatar(url); setEditingAvatar(false) } })
  const pickPreset = useMutation({ mutationFn: async (url: string) => uploadAvatar(await presetToFile(url)), onSuccess: (url) => { afterAvatar(url); setEditingAvatar(false) } })
  const remove = useMutation({ mutationFn: deleteAvatar, onSuccess: () => { afterAvatar(null); setEditingAvatar(false) } })
  const busyAvatar = upload.isPending || pickPreset.isPending || remove.isPending

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
      <div className="flex flex-wrap items-center gap-4">
        <Avatar name={name || user?.name || '?'} src={me.data?.avatarUrl} className="h-20 w-20 text-2xl" />
        <div className="space-y-1.5">
          <input
            ref={fileRef}
            type="file"
            accept="image/jpeg,image/png,image/webp"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0]
              if (!f) return
              setAvatarError(null)
              if (!['image/jpeg', 'image/png', 'image/webp'].includes(f.type)) return setAvatarError(t('settings.avatarBadType'))
              if (f.size > 2 * 1024 * 1024) return setAvatarError(t('settings.avatarTooBig'))
              upload.mutate(f)
              e.target.value = ''
            }}
          />
          <button type="button" className="btn-secondary px-4 py-2" onClick={() => setEditingAvatar(true)}>
            {t('settings.avatarEdit')}
          </button>
          <p className="text-xs text-slate-400">{t('settings.avatarHint')}</p>
          {(avatarError || upload.isError || pickPreset.isError || remove.isError) && !editingAvatar && (
            <p className="text-xs text-red-600">{avatarError ?? errorMessage(t, upload.error ?? pickPreset.error ?? remove.error)}</p>
          )}
        </div>
      </div>
      {editingAvatar && (
        <Modal title={t('settings.avatarEditTitle')} onClose={() => setEditingAvatar(false)}>
          <div className="space-y-4">
            <button type="button" className="btn-primary w-full" disabled={busyAvatar} onClick={() => fileRef.current?.click()}>
              {upload.isPending ? t('common.loading') : t('settings.avatarFromGallery')}
            </button>
            <div>
              <div className="mb-2 text-sm font-medium text-slate-700">{t('settings.avatarPresets')}</div>
              <div className="flex gap-4">
                {AVATAR_PRESETS.map((p) => (
                  <button
                    key={p.key}
                    type="button"
                    disabled={busyAvatar}
                    onClick={() => {
                      setAvatarError(null)
                      pickPreset.mutate(p.url)
                    }}
                    className="flex flex-col items-center gap-1.5 text-xs text-slate-500 disabled:opacity-50"
                  >
                    <img src={p.url} alt="" className="h-20 w-20 rounded-full object-cover ring-2 ring-transparent transition hover:ring-brand-400" draggable={false} />
                    {t(`settings.avatarPreset.${p.key}`)}
                  </button>
                ))}
              </div>
            </div>
            {me.data?.avatarUrl && (
              <button type="button" className="btn-secondary w-full" disabled={busyAvatar} onClick={() => remove.mutate()}>
                {t('settings.avatarRemove')}
              </button>
            )}
            <p className="text-xs text-slate-400">{t('settings.avatarHint')}</p>
            {(avatarError || upload.isError || pickPreset.isError || remove.isError) && (
              <p className="text-xs text-red-600">{avatarError ?? errorMessage(t, upload.error ?? pickPreset.error ?? remove.error)}</p>
            )}
          </div>
        </Modal>
      )}
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
                  on ? 'border-brand-600 bg-brand-600 text-white' : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-surface'
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
            <li key={s.id} className="flex items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
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
            <li key={b.userId} className="flex items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
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

// ───────── Данные и удаление аккаунта: POST/GET /me/export, DELETE /me ─────────
function AccountSection() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { user, logout } = useAuth()
  const [deleting, setDeleting] = useState(false)
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [recovery, setRecovery] = useState(false)

  const exportQuery = useQuery({
    queryKey: ['dataExport'],
    queryFn: getExport,
    // пока выгрузка готовится, опрашиваем статус
    refetchInterval: (q) => (q.state.data && (q.state.data.status === 'Pending' || q.state.data.status === 'Processing') ? 5000 : false),
  })
  const request = useMutation({ mutationFn: requestExport, onSuccess: () => qc.invalidateQueries({ queryKey: ['dataExport'] }) })
  const remove = useMutation({
    mutationFn: () => deleteAccount({ password, code: code.trim(), isRecoveryCode: recovery }),
    onSuccess: async () => {
      try {
        await logout()
      } catch {
        /* сеанс на сервере уже отозван */
      }
      navigate('/', { replace: true })
    },
  })

  const ex = exportQuery.data
  const busy = ex?.status === 'Pending' || ex?.status === 'Processing'

  return (
    <section className="card space-y-4">
      <h2 className="font-bold text-slate-900">{t('settings.account.title')}</h2>

      <div className="space-y-2">
        <div className="text-sm font-medium text-slate-700">{t('settings.account.exportTitle')}</div>
        <p className="text-xs text-slate-400">{t('settings.account.exportHint')}</p>
        {ex && <p className="text-sm text-slate-600">{t(`settings.account.status.${ex.status}`, { date: ex.expiresAt ? formatDateTime(ex.expiresAt, lang) : '' })}</p>}
        {request.isError && <p className={errorBox}>{errorMessage(t, request.error)}</p>}
        <button className="btn-secondary" disabled={request.isPending || busy} onClick={() => request.mutate()}>
          {request.isPending || busy ? t('common.loading') : t('settings.account.exportButton')}
        </button>
      </div>

      <div className="space-y-2 border-t border-ink-line pt-4">
        <div className="text-sm font-medium text-red-700">{t('settings.account.deleteTitle')}</div>
        <p className="text-xs text-slate-400">{t('settings.account.deleteHint')}</p>
        <button className="rounded-xl border border-red-200 px-4 py-2 text-sm font-semibold text-red-600 hover:bg-red-50" onClick={() => setDeleting(true)}>
          {t('settings.account.deleteButton')}
        </button>
      </div>

      {deleting && (
        <Modal title={t('settings.account.deleteTitle')} onClose={() => setDeleting(false)}>
          <div className="space-y-4">
            <p className="text-sm text-slate-700">{t('settings.account.deleteConfirm')}</p>
            <label className="block">
              <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('settings.account.password')}</span>
              <input type="password" className="input" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </label>
            {user?.twoFactorEnabled && (
              <div className="space-y-2">
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium text-slate-700">{recovery ? t('settings.account.recoveryCode') : t('settings.account.code')}</span>
                  <input className="input" inputMode={recovery ? 'text' : 'numeric'} value={code} onChange={(e) => setCode(e.target.value)} />
                </label>
                <label className="flex items-center gap-2 text-xs text-slate-500">
                  <input type="checkbox" checked={recovery} onChange={(e) => setRecovery(e.target.checked)} />
                  {t('settings.account.useRecovery')}
                </label>
              </div>
            )}
            {remove.isError && <p className={errorBox}>{errorMessage(t, remove.error)}</p>}
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={() => setDeleting(false)}>
                {t('common.cancel')}
              </button>
              <button
                className="btn-primary bg-[#DC3545] hover:bg-[#c42f3d]"
                disabled={remove.isPending || !password || (!!user?.twoFactorEnabled && !code.trim())}
                onClick={() => remove.mutate()}
              >
                {remove.isPending ? t('common.loading') : t('settings.account.deleteButton')}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </section>
  )
}
