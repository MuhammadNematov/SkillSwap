import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { getMe, updateMe } from '../../api/me'
import { getLanguages, getMySkills, saveMySkills } from '../../api/onboarding'
import type { CatalogSkill, LearnEntry, LocalizedName, SkillLevel, TeachEntry, UiLanguage } from '../../api/types'
import Icon from '../../components/Icon'
import Select from '../../components/Select'
import SkillPicker from '../../components/SkillPicker'
import Spinner from '../../components/Spinner'
import { useAuth } from '../../hooks/useAuth'
import { pickName } from '../../utils/localized'

const LEVELS: SkillLevel[] = ['Beginner', 'Intermediate', 'Advanced']
type Side = 'teach' | 'learn'

// Обязательная анкета (ТЗ 5.2): «Умею», «Хочу изучать», языки занятий, часовой пояс
export default function Onboarding() {
  const { t, i18n } = useTranslation()
  const lang = (i18n.resolvedLanguage ?? 'ru') as UiLanguage
  const navigate = useNavigate()
  const { user, updateUser } = useAuth()
  const qc = useQueryClient()
  const zones: string[] = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []

  const [teach, setTeach] = useState<TeachEntry[]>([])
  const [learn, setLearn] = useState<LearnEntry[]>([])
  const [languages, setLanguages] = useState<string[]>([])
  const [timeZone, setTimeZone] = useState(() => Intl.DateTimeFormat().resolvedOptions().timeZone ?? '')
  const [picking, setPicking] = useState<Side | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)

  const mine = useQuery({ queryKey: ['mySkills'], queryFn: getMySkills })
  const langs = useQuery({ queryKey: ['languages'], queryFn: getLanguages })
  const profile = useQuery({ queryKey: ['me'], queryFn: getMe })

  useEffect(() => {
    if (!mine.data || !profile.data || loaded) return
    setTeach(mine.data.teach)
    setLearn(mine.data.learn)
    setLanguages(profile.data.languages)
    if (profile.data.timeZone) setTimeZone(profile.data.timeZone)
    setLoaded(true)
  }, [mine.data, profile.data, loaded])

  const nameOf = (e: { skillId: string; names?: LocalizedName }) => (e.names ? pickName(e.names, lang) : e.skillId)

  // Сначала профиль (языки и часовой пояс), потом навыки. Показ в подборе сервер включит сам.
  const save = useMutation({
    mutationFn: async (body: { teach: TeachEntry[]; learn: LearnEntry[] }) => {
      const p = profile.data!
      await updateMe({
        name: p.name,
        bio: p.bio,
        uiLanguage: p.uiLanguage,
        timeZone: timeZone.trim(),
        languages,
        isSearchable: p.isSearchable,
      })
      await saveMySkills(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['me'] })
      qc.invalidateQueries({ queryKey: ['mySkills'] })
      if (user) updateUser({ ...user, skillsComplete: true })
      navigate('/app/matches', { replace: true })
    },
  })

  function pick(skill: CatalogSkill) {
    if (picking === 'teach') setTeach((l) => [...l, { skillId: skill.id, names: skill.names, level: 'Intermediate', description: '' }])
    else setLearn((l) => [...l, { skillId: skill.id, names: skill.names, level: 'Beginner' }])
    setPicking(null)
  }

  function submit() {
    setFormError(null)
    // Навыки, которые админ выключил, на сервер не отправляем (иначе unknown_skill)
    const teachOk = teach.filter((e) => e.isActive !== false)
    const learnOk = learn.filter((e) => e.isActive !== false)
    if (teachOk.length === 0 || learnOk.length === 0) return setFormError(t('onboarding.needSkills'))
    if (languages.length === 0) return setFormError(t('onboarding.needLanguage'))
    if (!timeZone.trim()) return setFormError(t('onboarding.needTimeZone'))
    if (zones.length > 0 && !zones.includes(timeZone.trim())) return setFormError(t('onboarding.badTimeZone'))
    save.mutate({ teach: teachOk, learn: learnOk })
  }

  if (mine.isLoading || langs.isLoading || profile.isLoading) return <Spinner />

  const levelOptions = LEVELS.map((l) => ({ value: l, label: t(`levels.${l}`) }))

  const remove = (side: Side, id: string) =>
    side === 'teach' ? setTeach((l) => l.filter((e) => e.skillId !== id)) : setLearn((l) => l.filter((e) => e.skillId !== id))

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('onboarding.title')}</h1>
        <p className="mt-1 text-sm text-slate-500">{t('onboarding.subtitle')}</p>
      </div>

      <section className="card space-y-3">
        <div className="flex items-center justify-between gap-3">
          <h2 className="font-bold text-slate-900">{t('onboarding.teach')}</h2>
          <button className="btn-secondary px-3 py-1.5" onClick={() => setPicking('teach')}>
            + {t('onboarding.add')}
          </button>
        </div>
        {teach.length === 0 && <p className="text-sm text-slate-500">{t('onboarding.teachEmpty')}</p>}
        {teach.map((e) => (
          <div key={e.skillId} className="space-y-2 rounded-xl bg-brand-50 p-3">
            <div className="flex items-center gap-3">
              <span className="min-w-0 flex-1 truncate font-semibold text-slate-900">
                {nameOf(e)}
                {e.isActive === false && <span className="ml-2 rounded-full bg-amber-100/80 px-2 py-0.5 text-xs font-medium text-amber-700">{t('onboarding.disabledSkill')}</span>}
              </span>
              <Select
                className="w-40"
                value={e.level}
                options={levelOptions}
                ariaLabel={t('onboarding.level')}
                onChange={(v) => setTeach((l) => l.map((x) => (x.skillId === e.skillId ? { ...x, level: v as SkillLevel } : x)))}
              />
              <button className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-red-600" aria-label={t('onboarding.remove')} onClick={() => remove('teach', e.skillId)}>
                <Icon name="x" className="h-4 w-4" />
              </button>
            </div>
            <textarea
              rows={2}
              maxLength={300}
              className="input resize-none"
              placeholder={t('onboarding.experience')}
              value={e.description}
              onChange={(ev) => setTeach((l) => l.map((x) => (x.skillId === e.skillId ? { ...x, description: ev.target.value } : x)))}
            />
          </div>
        ))}
      </section>

      <section className="card space-y-3">
        <div className="flex items-center justify-between gap-3">
          <h2 className="font-bold text-slate-900">{t('onboarding.learn')}</h2>
          <button className="btn-secondary px-3 py-1.5" onClick={() => setPicking('learn')}>
            + {t('onboarding.add')}
          </button>
        </div>
        {learn.length === 0 && <p className="text-sm text-slate-500">{t('onboarding.learnEmpty')}</p>}
        {learn.map((e) => (
          <div key={e.skillId} className="flex items-center gap-3 rounded-xl bg-brand-50 p-3">
            <span className="min-w-0 flex-1 truncate font-semibold text-slate-900">
                {nameOf(e)}
                {e.isActive === false && <span className="ml-2 rounded-full bg-amber-100/80 px-2 py-0.5 text-xs font-medium text-amber-700">{t('onboarding.disabledSkill')}</span>}
              </span>
            <Select
              className="w-40"
              value={e.level}
              options={levelOptions}
              ariaLabel={t('onboarding.currentLevel')}
              onChange={(v) => setLearn((l) => l.map((x) => (x.skillId === e.skillId ? { ...x, level: v as SkillLevel } : x)))}
            />
            <button className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-red-600" aria-label={t('onboarding.remove')} onClick={() => remove('learn', e.skillId)}>
              <Icon name="x" className="h-4 w-4" />
            </button>
          </div>
        ))}
      </section>

      <section className="card space-y-3">
        <h2 className="font-bold text-slate-900">{t('onboarding.languages')}</h2>
        <div className="flex flex-wrap gap-2">
          {(langs.data ?? []).map((l) => {
            const on = languages.includes(l.code)
            return (
              <button
                key={l.code}
                onClick={() => setLanguages((cur) => (on ? cur.filter((c) => c !== l.code) : [...cur, l.code]))}
                className={`rounded-xl border px-4 py-2 text-sm font-medium transition ${
                  on ? 'border-brand-600 bg-brand-600 text-white' : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-white'
                }`}
              >
                {pickName(l.names, lang)}
              </button>
            )
          })}
        </div>
      </section>

      <section className="card space-y-3">
        <h2 className="font-bold text-slate-900">{t('onboarding.timeZone')}</h2>
        <input className="input" list="tz-list" value={timeZone} onChange={(e) => setTimeZone(e.target.value)} placeholder="Asia/Dushanbe" />
        <datalist id="tz-list">
          {zones.map((z) => (
            <option key={z} value={z} />
          ))}
        </datalist>
      </section>

      {(formError || save.isError) && (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{formError ?? errorMessage(t, save.error)}</p>
      )}
      <button className="btn-primary w-full sm:w-auto sm:px-10" disabled={save.isPending} onClick={submit}>
        {save.isPending ? t('common.loading') : t('onboarding.save')}
      </button>

      {picking && (
        <SkillPicker
          excludeIds={(picking === 'teach' ? teach : learn).map((e) => e.skillId)}
          onPick={pick}
          onClose={() => setPicking(null)}
        />
      )}
    </div>
  )
}
