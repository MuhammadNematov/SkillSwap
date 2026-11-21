import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Route, Routes } from 'react-router-dom'
import {
  createCategory,
  createSkill,
  deleteCategory,
  deleteSkill,
  getAdminCategories,
  getAdminSkills,
  updateCategory,
  updateSkill,
  type AdminCategory,
  type AdminSkill,
} from '../../api/admin'
import { errorMessage } from '../../api/errors'
import { getStaff, inviteStaff, removeStaff, type StaffMember, type StaffRole } from '../../api/staff'
import Modal from '../../components/Modal'
import Select from '../../components/Select'
import Spinner from '../../components/Spinner'
import StaffShell from '../../components/StaffShell'
import { useAuth } from '../../hooks/useAuth'
import Notifications from '../Notifications'
import AdminCaseDetail from './AdminCaseDetail'
import BansTab from './BansTab'
import CasesTab from './CasesTab'
import { pickName } from '../../utils/localized'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const PAGE_SIZE = 20
const TABS = ['cases', 'bans', 'skills', 'categories', 'staff'] as const

// Админ-панель: обращения, запросы на блокировку, каталог навыков и сотрудники
function AdminHome() {
  const { t } = useTranslation()
  const [tab, setTab] = useState<(typeof TABS)[number]>('cases')

  return (
    <>
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('admin.title')}</h1>
      <div className="flex flex-wrap gap-2">
        {TABS.map((k) => (
          <button
            key={k}
            onClick={() => setTab(k)}
            className={`rounded-xl border px-4 py-2 text-sm font-medium transition ${
              tab === k ? 'border-brand-600 bg-brand-600 text-white' : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-surface'
            }`}
          >
            {t(`admin.tab_${k}`)}
          </button>
        ))}
      </div>
      {tab === 'cases' ? <CasesTab /> : tab === 'bans' ? <BansTab /> : tab === 'skills' ? <SkillsTab /> : tab === 'categories' ? <CategoriesTab /> : <StaffTab />}
    </>
  )
}

export default function Admin() {
  return (
    <StaffShell wide>
      <Routes>
        <Route index element={<AdminHome />} />
        <Route path="cases/:id" element={<AdminCaseDetail />} />
        <Route path="notifications" element={<Notifications area="admin" />} />
        <Route path="*" element={<AdminHome />} />
      </Routes>
    </StaffShell>
  )
}

// ───────── Категории ─────────
function CategoriesTab() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const [editing, setEditing] = useState<AdminCategory | 'new' | null>(null)
  const { data = [], isLoading } = useQuery({ queryKey: ['admin', 'categories'], queryFn: getAdminCategories })
  const refresh = () => qc.invalidateQueries({ queryKey: ['admin'] })
  const remove = useMutation({ mutationFn: deleteCategory, onSuccess: refresh })
  const toggle = useMutation({ mutationFn: (c: AdminCategory) => updateCategory({ ...c, isActive: !c.isActive }), onSuccess: refresh })

  if (isLoading) return <Spinner />
  return (
    <section className="card space-y-3">
      <div className="flex items-center justify-between">
        <h2 className="font-bold text-slate-900">{t('admin.tab_categories')}</h2>
        <button className="btn-primary px-4 py-2" onClick={() => setEditing('new')}>
          + {t('admin.add')}
        </button>
      </div>
      {(remove.error || toggle.error) && <p className={errorBox}>{errorMessage(t, remove.error ?? toggle.error)}</p>}
      <ul className="space-y-2">
        {data.map((c) => (
          <li key={c.id} className={`flex flex-wrap items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5 ${c.isActive ? '' : 'opacity-60'}`}>
            <div className="min-w-0 flex-1">
              <div className="text-sm font-semibold text-slate-900">
                {c.nameRu} <span className="font-normal text-slate-400">· {c.nameEn} · {c.nameTg}</span>
              </div>
              <div className="text-xs text-slate-400">
                {t('admin.order')}: {c.sortOrder} · {t('admin.skillCount', { count: c.skillCount })}
              </div>
            </div>
            <button className="text-sm font-medium text-brand-600 hover:underline" onClick={() => toggle.mutate(c)}>
              {c.isActive ? t('admin.disable') : t('admin.enable')}
            </button>
            <button className="text-sm font-medium text-brand-600 hover:underline" onClick={() => setEditing(c)}>
              {t('admin.edit')}
            </button>
            <button className="text-sm font-medium text-red-600 hover:underline" disabled={remove.isPending} onClick={() => remove.mutate(c.id)}>
              {t('admin.delete')}
            </button>
          </li>
        ))}
      </ul>
      {editing && <CategoryModal category={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </section>
  )
}

function CategoryModal({ category, onClose }: { category: AdminCategory | null; onClose: () => void }) {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const [f, setF] = useState({
    nameRu: category?.nameRu ?? '',
    nameEn: category?.nameEn ?? '',
    nameTg: category?.nameTg ?? '',
    sortOrder: String(category?.sortOrder ?? 0),
  })
  const save = useMutation({
    mutationFn: () => {
      const body = { nameRu: f.nameRu.trim(), nameEn: f.nameEn.trim(), nameTg: f.nameTg.trim(), sortOrder: Number(f.sortOrder) || 0 }
      return category ? updateCategory({ ...body, id: category.id, isActive: category.isActive }) : createCategory(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin'] })
      qc.invalidateQueries({ queryKey: ['skillCategories'] })
      onClose()
    },
  })
  const field = (key: 'nameRu' | 'nameEn' | 'nameTg', label: string) => (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-slate-700">{label}</span>
      <input className="input" maxLength={100} value={f[key]} onChange={(e) => setF({ ...f, [key]: e.target.value })} />
    </label>
  )
  const valid = f.nameRu.trim() && f.nameEn.trim() && f.nameTg.trim()

  return (
    <Modal title={category ? t('admin.editCategory') : t('admin.newCategory')} onClose={onClose}>
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid) save.mutate()
        }}
      >
        {field('nameRu', t('admin.nameRu'))}
        {field('nameEn', t('admin.nameEn'))}
        {field('nameTg', t('admin.nameTg'))}
        <label className="block">
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('admin.order')}</span>
          <input className="input" type="number" value={f.sortOrder} onChange={(e) => setF({ ...f, sortOrder: e.target.value })} />
        </label>
        {save.isError && <p className={errorBox}>{errorMessage(t, save.error)}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={!valid || save.isPending}>
            {save.isPending ? t('common.loading') : t('settings.save')}
          </button>
        </div>
      </form>
    </Modal>
  )
}

// ───────── Навыки ─────────
function SkillsTab() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const [category, setCategory] = useState('')
  const [q, setQ] = useState('')
  const [status, setStatus] = useState<'' | 'active' | 'inactive'>('')
  const [page, setPage] = useState(1)
  const [editing, setEditing] = useState<AdminSkill | 'new' | null>(null)

  const cats = useQuery({ queryKey: ['admin', 'categories'], queryFn: getAdminCategories })
  const list = useQuery({
    queryKey: ['admin', 'skills', category, q, status, page],
    queryFn: () => getAdminSkills({ category: category || undefined, q, isActive: status === '' ? undefined : status === 'active', page, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  })
  const refresh = () => qc.invalidateQueries({ queryKey: ['admin'] })
  const remove = useMutation({ mutationFn: deleteSkill, onSuccess: refresh })
  const toggle = useMutation({ mutationFn: (s: AdminSkill) => updateSkill({ ...s, isActive: !s.isActive }), onSuccess: refresh })

  const catName = (id: string) => {
    const c = cats.data?.find((x) => x.id === id)
    return c ? pickName({ tg: c.nameTg, ru: c.nameRu, en: c.nameEn }, lang) : ''
  }
  const total = list.data?.total ?? 0

  return (
    <section className="card space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <Select
          className="w-52"
          value={category}
          ariaLabel={t('onboarding.category')}
          onChange={(v) => {
            setCategory(v)
            setPage(1)
          }}
          options={[{ value: '', label: t('onboarding.allCategories') }, ...(cats.data ?? []).map((c) => ({ value: c.id, label: pickName({ tg: c.nameTg, ru: c.nameRu, en: c.nameEn }, lang) }))]}
        />
        <Select
          className="w-44"
          value={status}
          ariaLabel={t('admin.status')}
          onChange={(v) => {
            setStatus(v as typeof status)
            setPage(1)
          }}
          options={[
            { value: '', label: t('admin.statusAll') },
            { value: 'active', label: t('admin.statusActive') },
            { value: 'inactive', label: t('admin.statusInactive') },
          ]}
        />
        <input className="input max-w-xs flex-1" placeholder={t('onboarding.searchSkill')} value={q} onChange={(e) => { setQ(e.target.value); setPage(1) }} />
        <button className="btn-primary ml-auto px-4 py-2" onClick={() => setEditing('new')}>
          + {t('admin.add')}
        </button>
      </div>

      {(remove.error || toggle.error) && (
        <p className={errorBox}>
          {errorMessage(t, remove.error ?? toggle.error)}
        </p>
      )}

      {list.isLoading ? (
        <Spinner />
      ) : (list.data?.items ?? []).length === 0 ? (
        <p className="text-sm text-slate-500">{t('onboarding.nothingFound')}</p>
      ) : (
        <ul className="space-y-2">
          {list.data!.items.map((s) => (
            <li key={s.id} className={`flex flex-wrap items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5 ${s.isActive ? '' : 'opacity-60'}`}>
              <div className="min-w-0 flex-1">
                <div className="text-sm font-semibold text-slate-900">
                  {s.nameRu} <span className="font-normal text-slate-400">· {s.nameEn} · {s.nameTg}</span>
                </div>
                <div className="text-xs text-slate-400">
                  {catName(s.categoryId)}
                  {s.synonyms.length > 0 && ` · ${s.synonyms.join(', ')}`}
                </div>
              </div>
              <button className="text-sm font-medium text-brand-600 hover:underline" onClick={() => toggle.mutate(s)}>
                {s.isActive ? t('admin.disable') : t('admin.enable')}
              </button>
              <button className="text-sm font-medium text-brand-600 hover:underline" onClick={() => setEditing(s)}>
                {t('admin.edit')}
              </button>
              <button className="text-sm font-medium text-red-600 hover:underline" disabled={remove.isPending} onClick={() => remove.mutate(s.id)}>
                {t('admin.delete')}
              </button>
            </li>
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

      {editing && <SkillModal skill={editing === 'new' ? null : editing} categories={cats.data ?? []} onClose={() => setEditing(null)} />}
    </section>
  )
}

function SkillModal({ skill, categories, onClose }: { skill: AdminSkill | null; categories: AdminCategory[]; onClose: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const [f, setF] = useState({
    categoryId: skill?.categoryId ?? categories[0]?.id ?? '',
    nameRu: skill?.nameRu ?? '',
    nameEn: skill?.nameEn ?? '',
    nameTg: skill?.nameTg ?? '',
    synonyms: skill?.synonyms.join(', ') ?? '',
  })
  const save = useMutation({
    mutationFn: () => {
      const body = {
        categoryId: f.categoryId,
        nameRu: f.nameRu.trim(),
        nameEn: f.nameEn.trim(),
        nameTg: f.nameTg.trim(),
        synonyms: f.synonyms.split(',').map((x) => x.trim()).filter(Boolean),
      }
      return skill ? updateSkill({ ...body, id: skill.id, isActive: skill.isActive }) : createSkill(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin'] })
      qc.invalidateQueries({ queryKey: ['catalog'] })
      onClose()
    },
  })
  const field = (key: 'nameRu' | 'nameEn' | 'nameTg', label: string) => (
    <label className="block">
      <span className="mb-1 block text-sm font-medium text-slate-700">{label}</span>
      <input className="input" maxLength={100} value={f[key]} onChange={(e) => setF({ ...f, [key]: e.target.value })} />
    </label>
  )
  const valid = f.categoryId && f.nameRu.trim() && f.nameEn.trim() && f.nameTg.trim()

  return (
    <Modal title={skill ? t('admin.editSkill') : t('admin.newSkill')} onClose={onClose}>
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid) save.mutate()
        }}
      >
        <Select
          value={f.categoryId}
          ariaLabel={t('onboarding.category')}
          onChange={(v) => setF({ ...f, categoryId: v })}
          options={categories.map((c) => ({ value: c.id, label: pickName({ tg: c.nameTg, ru: c.nameRu, en: c.nameEn }, lang) }))}
        />
        {field('nameRu', t('admin.nameRu'))}
        {field('nameEn', t('admin.nameEn'))}
        {field('nameTg', t('admin.nameTg'))}
        <label className="block">
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('admin.synonyms')}</span>
          <input className="input" value={f.synonyms} placeholder={t('admin.synonymsHint')} onChange={(e) => setF({ ...f, synonyms: e.target.value })} />
        </label>
        {save.isError && <p className={errorBox}>{errorMessage(t, save.error)}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={!valid || save.isPending}>
            {save.isPending ? t('common.loading') : t('settings.save')}
          </button>
        </div>
      </form>
    </Modal>
  )
}

// ───────── Сотрудники ─────────
function StaffTab() {
  const { t } = useTranslation()
  const { user } = useAuth()
  const qc = useQueryClient()
  const isSuper = user?.role === 'SuperAdmin'
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<StaffRole>('Moderator')
  const [link, setLink] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['admin', 'staff'], queryFn: getStaff })
  const invite = useMutation({
    mutationFn: () => inviteStaff(email.trim(), role),
    onSuccess: (r) => {
      setEmail('')
      setLink(r.token ? `${window.location.origin}/staff-invitation/${r.token}` : null)
      qc.invalidateQueries({ queryKey: ['admin', 'staff'] })
    },
  })
  const remove = useMutation({ mutationFn: (m: StaffMember) => removeStaff(m), onSuccess: () => qc.invalidateQueries({ queryKey: ['admin', 'staff'] }) })
  // Админов снимает только суперадмин
  const canRemove = (m: StaffMember) => m.id !== user?.id && (m.role === 'Moderator' || isSuper)

  return (
    <section className="card space-y-4">
      <form
        className="flex flex-wrap items-center gap-3"
        onSubmit={(e) => {
          e.preventDefault()
          if (email.trim()) invite.mutate()
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
        <button className="btn-primary px-4 py-2" disabled={!email.trim() || invite.isPending}>
          {t('staff.invite')}
        </button>
      </form>
      {invite.isError && <p className={errorBox}>{errorMessage(t, invite.error)}</p>}
      {invite.isSuccess && <p className="rounded-lg bg-green-50 px-3 py-2 text-sm text-green-700">{t('staff.sent')}</p>}
      {link && (
        <p className="break-all rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {t('staff.mockLink')} <a className="underline" href={link}>{link}</a>
        </p>
      )}
      {remove.isError && <p className={errorBox}>{errorMessage(t, remove.error)}</p>}
      {list.isLoading ? (
        <Spinner />
      ) : (
        <ul className="space-y-2">
          {(list.data ?? []).map((m) => (
            <li key={m.id} className="flex flex-wrap items-center gap-3 rounded-xl bg-brand-50 px-3 py-2.5">
              <div className="min-w-0 flex-1">
                <div className="text-sm font-semibold text-slate-900">{m.name}</div>
                <div className="text-xs text-slate-400">
                  {m.email} · {t(`staff.role_${m.role}`)}
                  {!m.twoFactorEnabled && ` · ${t('staff.no2fa')}`}
                </div>
              </div>
              {canRemove(m) && (
                <button className="text-sm font-medium text-red-600 hover:underline" disabled={remove.isPending} onClick={() => remove.mutate(m)}>
                  {t('staff.remove')}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
