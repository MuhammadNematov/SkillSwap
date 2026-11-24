import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import {
  blockUser,
  getAdminUser,
  getAdminUsers,
  revokeWarning,
  unblockUser,
  type AdminUser,
  type AdminUserListItem,
  type AdminUserStatus,
} from '../../api/adminUsers'
import { errorMessage } from '../../api/errors'
import Modal from '../../components/Modal'
import Select from '../../components/Select'
import Spinner from '../../components/Spinner'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const PAGE_SIZE = 20

const STATUS_TONE: Record<AdminUserStatus, string> = {
  Active: 'bg-emerald-100/80 text-emerald-700',
  Blocked: 'bg-red-100/80 text-red-700',
  Deleted: 'bg-slate-100/80 text-slate-600',
}

function StatusBadge({ status }: { status: AdminUserStatus }) {
  const { t } = useTranslation()
  return <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${STATUS_TONE[status]}`}>{t(`adminUsers.status_${status}`)}</span>
}

// Окно с обязательной причиной (10–500 символов, пишется в журнал аудита, пользователю не показывается).
// Подтверждение паролем и кодом 2FA для действий над обычными пользователями не нужно.
function ReasonModal({
  title,
  intro,
  submitLabel,
  danger,
  onSubmit,
  onClose,
}: {
  title: string
  intro: ReactNode
  submitLabel: string
  danger?: boolean
  onSubmit: (reason: string) => Promise<unknown>
  onClose: () => void
}) {
  const { t } = useTranslation()
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const trimmed = reason.trim()
  const valid = trimmed.length >= 10 && trimmed.length <= 500

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!valid || busy) return
    setBusy(true)
    setError(null)
    try {
      await onSubmit(trimmed)
      onClose()
    } catch (err) {
      setError(errorMessage(t, err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={title} onClose={onClose}>
      <form className="space-y-3" onSubmit={submit}>
        <div className="text-sm text-slate-600">{intro}</div>
        <label className="block">
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('adminUsers.reason')}</span>
          <textarea className="input min-h-[80px]" maxLength={500} autoFocus value={reason} onChange={(e) => setReason(e.target.value)} />
          <span className="text-xs text-slate-400">{t('adminUsers.reasonHint')}</span>
        </label>
        {error && <p className={errorBox}>{error}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className={danger ? 'btn-primary bg-red-600 hover:bg-red-700' : 'btn-primary'} disabled={!valid || busy}>
            {busy ? t('common.loading') : submitLabel}
          </button>
        </div>
      </form>
    </Modal>
  )
}

type Action = { kind: 'block' } | { kind: 'unblock' } | { kind: 'revoke'; warningId: string }

// Карточка пользователя: предупреждения, обращения, запросы на бан, история блокировок, блокировка и разблокировка
function UserDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const [action, setAction] = useState<Action | null>(null)
  const card = useQuery({ queryKey: ['adminUser', id], queryFn: () => getAdminUser(id) })
  const dt = (iso: string) => new Date(iso).toLocaleString(lang)

  // Сервер возвращает обновлённую карточку: кладём её в кэш и обновляем список
  function applied(data: AdminUser) {
    qc.setQueryData(['adminUser', id], data)
    qc.invalidateQueries({ queryKey: ['adminUsers'] })
  }

  const back = (
    <button className="text-sm font-medium text-brand-600 hover:underline" onClick={onBack}>
      ← {t('adminUsers.back')}
    </button>
  )
  if (card.isLoading) return <section className="card space-y-3">{back}<Spinner /></section>
  if (card.isError || !card.data)
    return (
      <section className="card space-y-3">
        {back}
        <p className={errorBox}>{errorMessage(t, card.error)}</p>
      </section>
    )

  const d = card.data
  const u = d.user
  const title = (text: string) => <h3 className="mb-2 font-bold text-slate-900">{text}</h3>

  return (
    <section className="card space-y-5">
      {back}
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-lg font-bold text-slate-900">{u.name}</h2>
            <StatusBadge status={u.status} />
          </div>
          <div className="break-all text-sm text-slate-500">{u.email ?? '—'}</div>
          <div className="mt-1 text-xs text-slate-400">
            {t('adminUsers.language')}: {d.uiLanguage} · {t('adminUsers.timeZone')}: {d.timeZone} · {t(d.emailConfirmed ? 'adminUsers.emailConfirmed' : 'adminUsers.emailNotConfirmed')} ·{' '}
            {t('adminUsers.activeExchanges', { count: d.activeExchanges })}
          </div>
        </div>
        {u.status === 'Active' && (
          <button className="btn-primary bg-red-600 hover:bg-red-700" onClick={() => setAction({ kind: 'block' })}>
            {t('adminUsers.block')}
          </button>
        )}
        {u.status === 'Blocked' && (
          <button className="btn-primary" onClick={() => setAction({ kind: 'unblock' })}>
            {t('adminUsers.unblock')}
          </button>
        )}
      </div>

      <div>
        {title(t('adminUsers.warnings'))}
        {d.warnings.length === 0 ? (
          <p className="text-sm text-slate-500">{t('adminUsers.noWarnings')}</p>
        ) : (
          <ul className="space-y-2">
            {d.warnings.map((w) => (
              <li key={w.id} className="flex flex-wrap items-start gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="whitespace-pre-wrap break-words text-sm text-slate-800">{w.reason}</p>
                  <div className="mt-0.5 text-xs text-slate-400">
                    {dt(w.issuedAt)} ·{' '}
                    {w.revokedAt ? t('adminUsers.warningRevoked', { date: dt(w.revokedAt) }) : w.isActive ? t('adminUsers.warningActive', { date: dt(w.expiresAt) }) : t('adminUsers.warningExpired')}{' '}
                    ·{' '}
                    <Link className="text-brand-600 hover:underline" to={`/admin/cases/${w.caseId}`}>
                      {t('adminUsers.openCase')}
                    </Link>
                  </div>
                </div>
                {w.isActive && (
                  <button className="text-sm font-medium text-red-600 hover:underline" onClick={() => setAction({ kind: 'revoke', warningId: w.id })}>
                    {t('adminUsers.revoke')}
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div>
        {title(t('adminUsers.cases'))}
        {d.cases.length === 0 ? (
          <p className="text-sm text-slate-500">{t('adminUsers.noCases')}</p>
        ) : (
          <ul className="space-y-1.5">
            {d.cases.map((c) => (
              <li key={c.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-xl bg-brand-50 px-3 py-2 text-sm">
                <Link className="font-medium text-brand-600 hover:underline" to={`/admin/cases/${c.id}`}>
                  {t(`cases.type.${c.type}`, { defaultValue: c.type })}
                </Link>
                <span className="text-slate-600">{t(`adminUsers.caseRole_${c.role}`)}</span>
                <span className="text-slate-400">
                  {t(`cases.status.${c.status}`, { defaultValue: c.status })} · {dt(c.createdAt)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div>
        {title(t('adminUsers.bans'))}
        {d.banRequests.length === 0 ? (
          <p className="text-sm text-slate-500">{t('adminUsers.noBans')}</p>
        ) : (
          <ul className="space-y-1.5">
            {d.banRequests.map((b) => (
              <li key={b.id} className="rounded-xl bg-brand-50 px-3 py-2 text-sm">
                <div className="text-xs text-slate-400">
                  {t(`admin.bans.status.${b.status}`)} · {dt(b.createdAt)}
                  {!b.requestedById && ` · ${t('admin.bans.system')}`}
                </div>
                <p className="mt-0.5 whitespace-pre-wrap break-words text-slate-800">{b.reason}</p>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div>
        {title(t('adminUsers.history'))}
        {d.blockHistory.length === 0 ? (
          <p className="text-sm text-slate-500">{t('adminUsers.noHistory')}</p>
        ) : (
          <ul className="space-y-1.5">
            {d.blockHistory.map((e, i) => (
              <li key={i} className="rounded-xl bg-brand-50 px-3 py-2 text-sm">
                <span className="font-medium text-slate-800">{t(`adminUsers.event_${e.action}`)}</span>
                <span className="text-slate-400">
                  {' '}
                  · {dt(e.at)}
                  {e.source && ` · ${t(`adminUsers.source_${e.source}`)}`}
                </span>
                {e.reason && <p className="mt-0.5 whitespace-pre-wrap break-words text-slate-600">{e.reason}</p>}
              </li>
            ))}
          </ul>
        )}
      </div>

      {action?.kind === 'block' && (
        <ReasonModal
          danger
          title={t('adminUsers.blockTitle')}
          intro={t('adminUsers.blockWarning', { name: u.name })}
          submitLabel={t('adminUsers.block')}
          onSubmit={async (reason) => applied(await blockUser(id, reason))}
          onClose={() => setAction(null)}
        />
      )}
      {action?.kind === 'unblock' && (
        <ReasonModal
          title={t('adminUsers.unblockTitle')}
          intro={t('adminUsers.unblockHint', { name: u.name })}
          submitLabel={t('adminUsers.unblock')}
          onSubmit={async (reason) => applied(await unblockUser(id, reason))}
          onClose={() => setAction(null)}
        />
      )}
      {action?.kind === 'revoke' && (
        <ReasonModal
          title={t('adminUsers.revokeTitle')}
          intro={t('adminUsers.revokeHint')}
          submitLabel={t('adminUsers.revoke')}
          onSubmit={async (reason) => applied(await revokeWarning(action.warningId, reason))}
          onClose={() => setAction(null)}
        />
      )}
    </section>
  )
}

function UserRow({ u, onOpen }: { u: AdminUserListItem; onOpen: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  return (
    <li>
      <button className="flex w-full flex-wrap items-center gap-x-4 gap-y-1 rounded-xl bg-brand-50 px-3 py-2.5 text-left transition hover:bg-brand-100" onClick={onOpen}>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold text-slate-900">{u.name}</span>
            <StatusBadge status={u.status} />
            <span className="text-xs text-slate-400">{t(`adminUsers.age_${u.ageGroup}`)}</span>
          </div>
          <div className="break-all text-xs text-slate-400">{u.email ?? '—'}</div>
        </div>
        <div className="text-xs text-slate-500">
          {u.ratingCount > 0 ? t('adminUsers.rating', { avg: u.ratingAvg.toFixed(1), count: u.ratingCount }) : t('adminUsers.noRating')}
          {u.activeWarnings > 0 && <span className="ml-3 font-medium text-amber-700">{t('adminUsers.warningsCount', { count: u.activeWarnings })}</span>}
          {u.openCases > 0 && <span className="ml-3 font-medium text-red-700">{t('adminUsers.openCases', { count: u.openCases })}</span>}
        </div>
        <div className="w-full text-xs text-slate-400 sm:w-auto">
          {u.lastActiveAt ? t('adminUsers.lastActive', { date: new Date(u.lastActiveAt).toLocaleDateString(lang) }) : t('adminUsers.neverActive')}
        </div>
      </button>
    </li>
  )
}

// ───────── Пользователи: поиск, фильтр по статусу, карточка (docs/FRONTEND-ADMIN.md, раздел 4) ─────────
export default function UsersTab() {
  const { t } = useTranslation()
  const [input, setInput] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<AdminUserStatus | ''>('')
  const [page, setPage] = useState(1)
  const [openId, setOpenId] = useState<string | null>(null)

  const list = useQuery({
    queryKey: ['adminUsers', search, status, page],
    queryFn: () => getAdminUsers({ search, status: status || undefined, page, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  })

  if (openId) return <UserDetail id={openId} onBack={() => setOpenId(null)} />

  const total = list.data?.totalCount ?? 0
  return (
    <section className="card space-y-3">
      <form
        className="flex flex-wrap items-center gap-3"
        onSubmit={(e) => {
          e.preventDefault()
          setSearch(input)
          setPage(1)
        }}
      >
        <input className="input max-w-sm flex-1" placeholder={t('adminUsers.search')} value={input} onChange={(e) => setInput(e.target.value)} />
        <button className="btn-primary px-4 py-2">{t('adminUsers.find')}</button>
        <Select
          className="w-52"
          value={status}
          ariaLabel={t('admin.status')}
          onChange={(v) => {
            setStatus(v as AdminUserStatus | '')
            setPage(1)
          }}
          options={[
            { value: '', label: t('adminUsers.status_all') },
            { value: 'Active', label: t('adminUsers.status_Active') },
            { value: 'Blocked', label: t('adminUsers.status_Blocked') },
            { value: 'Deleted', label: t('adminUsers.status_Deleted') },
          ]}
        />
      </form>

      {list.isLoading ? (
        <Spinner />
      ) : list.isError ? (
        <p className={errorBox}>{errorMessage(t, list.error)}</p>
      ) : (list.data?.items ?? []).length === 0 ? (
        <p className="text-sm text-slate-500">{t('adminUsers.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {list.data!.items.map((u) => (
            <UserRow key={u.id} u={u} onOpen={() => setOpenId(u.id)} />
          ))}
        </ul>
      )}

      <div className="flex items-center justify-between text-sm text-slate-500">
        <span>{t('admin.total', { count: total })}</span>
        <div className="flex gap-2">
          <button className="btn-secondary px-3 py-1.5" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            ←
          </button>
          <button className="btn-secondary px-3 py-1.5" disabled={page * PAGE_SIZE >= total} onClick={() => setPage(page + 1)}>
            →
          </button>
        </div>
      </div>
    </section>
  )
}
