import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../../api/errors'
import {
  getInvitations,
  getModerators,
  inviteStaff,
  removeModerator,
  resendInvitation,
  revokeInvitation,
  type Confirmation,
  type Moderator,
  type RemoveModeratorResult,
  type StaffInvitation,
  type StaffRole,
} from '../../api/staff'
import type { UiLanguage } from '../../api/types'
import ConfirmAction from '../../components/ConfirmAction'
import Select from '../../components/Select'
import Spinner from '../../components/Spinner'
import { useAuth } from '../../hooks/useAuth'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'

type Action =
  | { kind: 'invite' }
  | { kind: 'revoke'; inv: StaffInvitation }
  | { kind: 'resend'; inv: StaffInvitation }
  | { kind: 'remove'; mod: Moderator }

// ───────── Сотрудники: приглашения и модераторы. Все действия подтверждаются паролем и кодом 2FA ─────────
export default function StaffTab() {
  const { t, i18n } = useTranslation()
  const { user } = useAuth()
  const qc = useQueryClient()
  const lang = (i18n.resolvedLanguage ?? 'ru') as UiLanguage
  const isSuper = user?.role === 'SuperAdmin'
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<StaffRole>('Moderator')
  const [action, setAction] = useState<Action | null>(null)
  const [reason, setReason] = useState('')
  const [showRemoved, setShowRemoved] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [link, setLink] = useState<string | null>(null)

  const invitations = useQuery({ queryKey: ['admin', 'invitations'], queryFn: () => getInvitations() })
  const moderators = useQuery({ queryKey: ['admin', 'moderators', showRemoved], queryFn: () => getModerators(showRemoved) })
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['admin', 'invitations'] })
    qc.invalidateQueries({ queryKey: ['admin', 'moderators'] })
  }

  function close() {
    setAction(null)
    setReason('')
  }

  async function run(c: Confirmation) {
    if (!action) return
    setNotice(null)
    setLink(null)
    if (action.kind === 'invite') {
      const r = await inviteStaff({ email: email.trim(), role, language: lang, confirmation: c })
      setEmail('')
      setNotice(t('staff.sent'))
      if (r.mockToken) setLink(`${window.location.origin}/staff/invitation/${r.mockToken}`)
    } else if (action.kind === 'revoke') {
      await revokeInvitation(action.inv.id, c)
      setNotice(t('staff.revoked'))
    } else if (action.kind === 'resend') {
      await resendInvitation(action.inv.id, c, lang)
      setNotice(t('staff.resent'))
    } else {
      const r: RemoveModeratorResult = await removeModerator(action.mod.id, { reason: reason.trim(), confirmation: c })
      setNotice(t('staff.removedResult', { cases: r.casesReturned, appeals: r.appealReviewsReturned }) + (r.personalUnlinked ? ` ${t('staff.personalUnlinked')}` : ''))
    }
    refresh()
  }

  const reasonValid = reason.trim().length >= 10 && reason.trim().length <= 500
  const dt = (iso: string) => new Date(iso).toLocaleString(lang)

  return (
    <section className="card space-y-5">
      <form
        className="flex flex-wrap items-center gap-3"
        onSubmit={(e) => {
          e.preventDefault()
          if (email.trim()) setAction({ kind: 'invite' })
        }}
      >
        <input className="input max-w-xs flex-1" type="email" placeholder="Email" value={email} onChange={(e) => setEmail(e.target.value)} />
        {isSuper && (
          <Select
            className="w-44"
            value={role}
            ariaLabel={t('staff.role')}
            onChange={(v) => setRole(v as StaffRole)}
            options={[
              { value: 'Moderator', label: t('staff.role_Moderator') },
              { value: 'Admin', label: t('staff.role_Admin') },
            ]}
          />
        )}
        <button className="btn-primary px-4 py-2" disabled={!email.trim()}>
          {t('staff.invite')}
        </button>
      </form>
      {notice && <p className="rounded-lg bg-green-50 px-3 py-2 text-sm text-green-700">{notice}</p>}
      {link && (
        <p className="break-all rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {t('staff.mockLink')} <a className="underline" href={link}>{link}</a>
        </p>
      )}

      <div className="space-y-2">
        <h3 className="font-bold text-slate-900">{t('staff.invitations')}</h3>
        {invitations.isLoading ? (
          <Spinner />
        ) : invitations.isError ? (
          <p className={errorBox}>{errorMessage(t, invitations.error)}</p>
        ) : (invitations.data ?? []).length === 0 ? (
          <p className="text-sm text-slate-500">{t('staff.noInvitations')}</p>
        ) : (
          <ul className="space-y-2">
            {invitations.data!.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <div className="break-all text-sm font-semibold text-slate-900">{i.email}</div>
                  <div className="text-xs text-slate-400">
                    {t(`staff.role_${i.targetRole}`)} · {t(`staff.inv_${i.status}`)} · {t('staff.invitedBy', { name: i.invitedBy.name })} · {t('staff.until', { date: dt(i.expiresAt) })}
                    {i.resendCount > 0 && ` · ${t('staff.resendCount', { count: i.resendCount })}`}
                  </div>
                </div>
                {(i.status === 'Pending' || i.status === 'Expired') && (
                  <button className="text-sm font-medium text-brand-600 hover:underline" onClick={() => setAction({ kind: 'resend', inv: i })}>
                    {t('staff.resend')}
                  </button>
                )}
                {i.status === 'Pending' && (
                  <button className="text-sm font-medium text-red-600 hover:underline" onClick={() => setAction({ kind: 'revoke', inv: i })}>
                    {t('staff.revoke')}
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="space-y-2">
        <div className="flex flex-wrap items-center gap-3">
          <h3 className="flex-1 font-bold text-slate-900">{t('staff.moderators')}</h3>
          <button type="button" className={`chip ${!showRemoved ? 'chip-on' : ''}`} onClick={() => setShowRemoved(false)}>
            {t('staff.active')}
          </button>
          <button type="button" className={`chip ${showRemoved ? 'chip-on' : ''}`} onClick={() => setShowRemoved(true)}>
            {t('staff.removedTab')}
          </button>
        </div>
        {moderators.isLoading ? (
          <Spinner />
        ) : moderators.isError ? (
          <p className={errorBox}>{errorMessage(t, moderators.error)}</p>
        ) : (moderators.data ?? []).length === 0 ? (
          <p className="text-sm text-slate-500">{t('staff.noModerators')}</p>
        ) : (
          <ul className="space-y-2">
            {moderators.data!.map((m) => (
              <li key={m.id} className="flex flex-wrap items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <div className="text-sm font-semibold text-slate-900">{m.name}</div>
                  <div className="text-xs text-slate-400">
                    {m.email ?? '—'}
                    {m.personalLinked && ` · ${t('staff.linked')}`}
                  </div>
                  {!m.removed && (
                    <div className="mt-0.5 text-xs text-slate-500">
                      {t('staff.load', { a: m.load.inProgress, b: m.load.waitingResponse, c: m.load.appealReview, d: m.load.resolvedLast30Days })}
                    </div>
                  )}
                </div>
                {!m.removed && (
                  <button className="text-sm font-medium text-red-600 hover:underline" onClick={() => setAction({ kind: 'remove', mod: m })}>
                    {t('staff.remove')}
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      {action && (
        <ConfirmAction
          title={t(`staff.title_${action.kind}`)}
          submitLabel={t(`staff.submit_${action.kind}`)}
          danger={action.kind === 'remove' || action.kind === 'revoke'}
          extraValid={action.kind !== 'remove' || reasonValid}
          onSubmit={run}
          onClose={close}
        >
          {action.kind === 'invite' && (
            <p className="text-sm text-slate-600">{t('staff.confirmInvite', { email: email.trim(), role: t(`staff.role_${role}`) })}</p>
          )}
          {(action.kind === 'revoke' || action.kind === 'resend') && <p className="break-all text-sm text-slate-600">{action.inv.email}</p>}
          {action.kind === 'remove' && (
            <>
              <p className="text-sm text-slate-600">{t('staff.removeWarning', { name: action.mod.name })}</p>
              <label className="block">
                <span className="mb-1 block text-sm font-medium text-slate-700">{t('staff.reason')}</span>
                <textarea className="input min-h-[80px]" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
                <span className="text-xs text-slate-400">{t('staff.reasonHint')}</span>
              </label>
            </>
          )}
        </ConfirmAction>
      )}
    </section>
  )
}
