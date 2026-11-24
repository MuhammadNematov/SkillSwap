import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { getAdminCategories, getAdminSkills } from '../../api/admin'
import { errorMessage } from '../../api/errors'
import {
  approveSuggestion,
  getSuggestions,
  mergeSuggestion,
  rejectSuggestion,
  type SimilarSkill,
  type Suggestion,
  type SuggestionStatus,
} from '../../api/skillSuggestions'
import Modal from '../../components/Modal'
import Select from '../../components/Select'
import Spinner from '../../components/Spinner'
import { pickName } from '../../utils/localized'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const PAGE_SIZE = 20
const STATUSES: SuggestionStatus[] = ['Pending', 'Approved', 'Merged', 'Rejected']

const TONE: Record<SuggestionStatus, string> = {
  Pending: 'bg-amber-100/80 text-amber-700',
  Approved: 'bg-emerald-100/80 text-emerald-700',
  Merged: 'bg-sky-100/80 text-sky-700',
  Rejected: 'bg-slate-100/80 text-slate-600',
}

type Action = { kind: 'approve' | 'merge' | 'reject'; suggestion: Suggestion; skill?: SimilarSkill }

// Общая оболочка окна: ошибка сервера показывается внутри, окно закрывается после успеха
function ActionModal({
  title,
  submitLabel,
  valid = true,
  danger,
  onSubmit,
  onClose,
  children,
}: {
  title: string
  submitLabel: string
  valid?: boolean
  danger?: boolean
  onSubmit: () => Promise<unknown>
  onClose: () => void
  children: ReactNode
}) {
  const { t } = useTranslation()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!valid || busy) return
    setBusy(true)
    setError(null)
    try {
      await onSubmit()
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
        {children}
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

// Одобрить: все три названия обязательны. Предложенное название подставляется в поле языка интерфейса, остальное дописывает админ.
function ApproveModal({ s, onDone, onClose }: { s: Suggestion; onDone: () => void; onClose: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = (i18n.resolvedLanguage ?? 'ru') as 'tg' | 'ru' | 'en'
  const cats = useQuery({ queryKey: ['admin', 'categories'], queryFn: getAdminCategories })
  const [names, setNames] = useState({ tg: lang === 'tg' ? s.name : '', ru: lang === 'ru' ? s.name : '', en: lang === 'en' ? s.name : '' })
  const [synonyms, setSynonyms] = useState('')
  const [categoryId, setCategoryId] = useState(s.categoryId)
  const valid = names.tg.trim() !== '' && names.ru.trim() !== '' && names.en.trim() !== ''
  const field = (code: 'tg' | 'ru' | 'en', label: string) => (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-slate-700">{label}</span>
      <input className="input" maxLength={100} value={names[code]} onChange={(e) => setNames({ ...names, [code]: e.target.value })} />
    </label>
  )

  return (
    <ActionModal
      title={t('suggestions.approveTitle')}
      submitLabel={t('suggestions.submitApprove')}
      valid={valid}
      onClose={onClose}
      onSubmit={async () => {
        await approveSuggestion(s.id, {
          nameTg: names.tg.trim(),
          nameRu: names.ru.trim(),
          nameEn: names.en.trim(),
          synonyms: synonyms.split(',').map((x) => x.trim()).filter(Boolean),
          categoryId: categoryId !== s.categoryId ? categoryId : undefined,
        })
        onDone()
      }}
    >
      <p className="text-sm text-slate-600">{t('suggestions.approveHint', { name: s.name })}</p>
      {field('ru', t('admin.nameRu'))}
      {field('en', t('admin.nameEn'))}
      {field('tg', t('admin.nameTg'))}
      <label className="block">
        <span className="mb-1 block text-sm font-medium text-slate-700">{t('admin.synonyms')}</span>
        <input className="input" value={synonyms} onChange={(e) => setSynonyms(e.target.value)} />
        <span className="text-xs text-slate-400">{t('admin.synonymsHint')}</span>
      </label>
      <div>
        <span className="mb-1 block text-sm font-medium text-slate-700">{t('onboarding.category')}</span>
        <Select
          value={categoryId}
          ariaLabel={t('onboarding.category')}
          onChange={setCategoryId}
          options={(cats.data ?? []).map((c) => ({ value: c.id, label: pickName({ tg: c.nameTg, ru: c.nameRu, en: c.nameEn }, lang) }))}
        />
      </div>
    </ActionModal>
  )
}

// Объединить с существующим навыком категории; по умолчанию предложенное название становится его синонимом.
// Варианты: похожие навыки из самого предложения плюс поиск по каталогу категории (сервер отдаёт не больше 50 за раз).
function MergeModal({ s, preselected, onDone, onClose }: { s: Suggestion; preselected?: string; onDone: () => void; onClose: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [q, setQ] = useState('')
  const skills = useQuery({
    queryKey: ['admin', 'skills', 'forMerge', s.categoryId, q],
    queryFn: () => getAdminSkills({ category: s.categoryId, q, page: 1, pageSize: 50 }),
    placeholderData: keepPreviousData,
  })
  const [picked, setPicked] = useState(preselected ?? s.similar[0]?.id ?? '')
  const [addAsSynonym, setAddAsSynonym] = useState(true)

  const label = (k: { nameTg: string; nameRu: string; nameEn: string; isActive: boolean }) =>
    pickName({ tg: k.nameTg, ru: k.nameRu, en: k.nameEn }, lang) + (k.isActive ? '' : ` ${t('suggestions.inactive')}`)
  // Без дублей: сначала похожие (их id точно относится к этой категории), затем найденное поиском
  const byId = new Map<string, { value: string; label: string }>()
  for (const k of s.similar) byId.set(k.id, { value: k.id, label: label(k) })
  for (const k of skills.data?.items ?? []) if (!byId.has(k.id)) byId.set(k.id, { value: k.id, label: label(k) })
  const options = [...byId.values()]
  // Выбранный навык должен быть среди вариантов: иначе список показал бы первый, а отправился бы другой
  const chosen = options.some((o) => o.value === picked) ? picked : (options[0]?.value ?? '')

  return (
    <ActionModal
      title={t('suggestions.mergeTitle')}
      submitLabel={t('suggestions.submitMerge')}
      valid={chosen !== ''}
      onClose={onClose}
      onSubmit={async () => {
        await mergeSuggestion(s.id, { skillId: chosen, addAsSynonym })
        onDone()
      }}
    >
      <p className="text-sm text-slate-600">{t('suggestions.mergeHint', { name: s.name })}</p>
      <input className="input" placeholder={t('suggestions.searchSkill')} value={q} onChange={(e) => setQ(e.target.value)} />
      {skills.isError ? (
        <p className={errorBox}>{errorMessage(t, skills.error)}</p>
      ) : options.length === 0 ? (
        skills.isLoading ? <Spinner /> : <p className="text-sm text-slate-500">{t('onboarding.nothingFound')}</p>
      ) : (
        <div>
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('suggestions.chooseSkill')}</span>
          <Select value={chosen} ariaLabel={t('suggestions.chooseSkill')} onChange={setPicked} options={options} />
        </div>
      )}
      <label className="flex items-center gap-2 text-sm text-slate-600">
        <input type="checkbox" checked={addAsSynonym} onChange={(e) => setAddAsSynonym(e.target.checked)} />
        {t('suggestions.addSynonym')}
      </label>
    </ActionModal>
  )
}

function RejectModal({ s, onDone, onClose }: { s: Suggestion; onDone: () => void; onClose: () => void }) {
  const { t } = useTranslation()
  const [reason, setReason] = useState('')
  return (
    <ActionModal
      danger
      title={t('suggestions.rejectTitle')}
      submitLabel={t('suggestions.submitReject')}
      valid={reason.length <= 500}
      onClose={onClose}
      onSubmit={async () => {
        await rejectSuggestion(s.id, reason)
        onDone()
      }}
    >
      <p className="text-sm text-slate-600">{t('suggestions.rejectHint', { name: s.name })}</p>
      <label className="block">
        <span className="mb-1 block text-sm font-medium text-slate-700">{t('suggestions.rejectReason')}</span>
        <textarea className="input min-h-[80px]" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>
    </ActionModal>
  )
}

function SuggestionCard({ s, onAction }: { s: Suggestion; onAction: (a: Action) => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const dt = (iso: string) => new Date(iso).toLocaleString(lang)
  const pending = s.status === 'Pending'

  return (
    <div className="card space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="break-words text-base font-bold text-slate-900">{s.name}</span>
        <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${TONE[s.status]}`}>{t(`suggestions.status_${s.status}`)}</span>
        {pending && s.sameNameCount > 1 && (
          <span className="rounded-full bg-brand-100 px-2.5 py-0.5 text-xs font-medium text-brand-700">{t('suggestions.sameName', { count: s.sameNameCount - 1 })}</span>
        )}
      </div>
      <div className="text-xs text-slate-400">
        {t('suggestions.author', { name: s.userName })} · {t('suggestions.category', { name: s.categoryNameEn })} · {dt(s.createdAt)}
        {s.resolvedAt && ` · ${t('suggestions.resolvedAt', { date: dt(s.resolvedAt) })}`}
      </div>

      {pending && (
        <>
          <div>
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('suggestions.similar')}</div>
            {s.similar.length === 0 ? (
              <p className="mt-0.5 text-sm text-slate-500">{t('suggestions.noSimilar')}</p>
            ) : (
              <ul className="mt-1 space-y-1">
                {s.similar.map((k) => (
                  <li key={k.id} className="flex flex-wrap items-center gap-3 rounded-lg bg-brand-50 px-3 py-1.5 text-sm">
                    <span className={`min-w-0 flex-1 ${k.isActive ? 'text-slate-800' : 'text-slate-400'}`}>
                      {k.nameRu} <span className="text-slate-400">· {k.nameEn} · {k.nameTg}</span>
                      {!k.isActive && ` ${t('suggestions.inactive')}`}
                    </span>
                    {k.isActive && (
                      <button className="font-medium text-brand-600 hover:underline" onClick={() => onAction({ kind: 'merge', suggestion: s, skill: k })}>
                        {t('suggestions.mergeWith')}
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div className="flex flex-wrap gap-2 pt-1">
            <button className="btn-primary" onClick={() => onAction({ kind: 'approve', suggestion: s })}>
              {t('suggestions.approve')}
            </button>
            <button className="btn-secondary" onClick={() => onAction({ kind: 'merge', suggestion: s })}>
              {t('suggestions.merge')}
            </button>
            <button className="btn-secondary text-red-600" onClick={() => onAction({ kind: 'reject', suggestion: s })}>
              {t('suggestions.reject')}
            </button>
          </div>
        </>
      )}
    </div>
  )
}

// ───────── Предложения навыков: очередь и разобранные (docs/FRONTEND-ADMIN.md, раздел 6) ─────────
export default function SuggestionsTab() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const [status, setStatus] = useState<SuggestionStatus>('Pending')
  const [categoryId, setCategoryId] = useState('')
  const [page, setPage] = useState(1)
  const [action, setAction] = useState<Action | null>(null)

  const cats = useQuery({ queryKey: ['admin', 'categories'], queryFn: getAdminCategories })
  const list = useQuery({
    queryKey: ['adminSuggestions', status, categoryId, page],
    queryFn: () => getSuggestions({ status, categoryId: categoryId || undefined, page, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  })
  // Одобрение и объединение меняют каталог, поэтому обновляем и его, и очередь (разбираются и «такие же» предложения)
  const done = () => {
    qc.invalidateQueries({ queryKey: ['adminSuggestions'] })
    qc.invalidateQueries({ queryKey: ['admin'] })
  }
  const total = list.data?.totalCount ?? 0

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        {STATUSES.map((st) => (
          <button
            key={st}
            className={`chip ${status === st ? 'chip-on' : ''}`}
            onClick={() => {
              setStatus(st)
              setPage(1)
            }}
          >
            {t(`suggestions.tab_${st}`)}
          </button>
        ))}
        <Select
          className="ml-auto w-52"
          value={categoryId}
          ariaLabel={t('onboarding.category')}
          onChange={(v) => {
            setCategoryId(v)
            setPage(1)
          }}
          options={[{ value: '', label: t('onboarding.allCategories') }, ...(cats.data ?? []).map((c) => ({ value: c.id, label: pickName({ tg: c.nameTg, ru: c.nameRu, en: c.nameEn }, lang) }))]}
        />
      </div>

      {list.isLoading ? (
        <Spinner />
      ) : list.isError ? (
        <p className={errorBox}>{errorMessage(t, list.error)}</p>
      ) : (list.data?.items ?? []).length === 0 ? (
        <div className="card text-sm text-slate-500">{t('suggestions.empty')}</div>
      ) : (
        <div className="space-y-3">
          {list.data!.items.map((s) => (
            <SuggestionCard key={s.id} s={s} onAction={setAction} />
          ))}
        </div>
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

      {action?.kind === 'approve' && <ApproveModal s={action.suggestion} onDone={done} onClose={() => setAction(null)} />}
      {action?.kind === 'merge' && <MergeModal s={action.suggestion} preselected={action.skill?.id} onDone={done} onClose={() => setAction(null)} />}
      {action?.kind === 'reject' && <RejectModal s={action.suggestion} onDone={done} onClose={() => setAction(null)} />}
    </div>
  )
}
