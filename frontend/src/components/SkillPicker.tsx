import { useMutation, useQuery, keepPreviousData } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage, isErrorCode } from '../api/errors'
import { getSkillCategories, searchCatalog, suggestSkill } from '../api/onboarding'
import type { CatalogSkill, UiLanguage } from '../api/types'
import { pickName } from '../utils/localized'
import Modal from './Modal'
import Select from './Select'
import Spinner from './Spinner'

const PAGE_SIZE = 12

interface Props {
  excludeIds: string[] // навык в каждое направление добавляется один раз
  onPick: (skill: CatalogSkill) => void
  onClose: () => void
}

// Выбор навыка из каталога «Категория → Навык» + предложение нового навыка
export default function SkillPicker({ excludeIds, onPick, onClose }: Props) {
  const { t, i18n } = useTranslation()
  const lang = (i18n.resolvedLanguage ?? 'ru') as UiLanguage
  const [category, setCategory] = useState('')
  const [q, setQ] = useState('')
  const [page, setPage] = useState(1)
  const [suggesting, setSuggesting] = useState(false)
  const [suggestName, setSuggestName] = useState('')
  const [suggestCategory, setSuggestCategory] = useState('')

  const categories = useQuery({ queryKey: ['skillCategories'], queryFn: getSkillCategories })
  const catalog = useQuery({
    queryKey: ['catalog', category, q, lang, page],
    queryFn: () => searchCatalog({ category: category || undefined, q, lang, page, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  })
  const suggest = useMutation({ mutationFn: suggestSkill })

  const categoryOptions = [
    { value: '', label: t('onboarding.allCategories') },
    ...(categories.data ?? []).map((c) => ({ value: c.id, label: pickName(c.names, lang) })),
  ]
  const items = (catalog.data?.items ?? []).filter((s) => !excludeIds.includes(s.id))
  const total = catalog.data?.total ?? 0

  if (suggesting) {
    const options = categoryOptions.slice(1)
    const categoryId = suggestCategory || options[0]?.value || ''
    return (
      <Modal title={t('onboarding.suggestTitle')} onClose={onClose}>
        {suggest.isSuccess ? (
          <div className="space-y-4">
            <p className="text-sm text-slate-700">{t('onboarding.suggestSent')}</p>
            <button className="btn-primary w-full" onClick={onClose}>
              {t('onboarding.ok')}
            </button>
          </div>
        ) : (
          <form
            className="space-y-4"
            onSubmit={(e) => {
              e.preventDefault()
              if (suggestName.trim()) suggest.mutate({ name: suggestName.trim(), categoryId })
            }}
          >
            <input className="input" maxLength={80} placeholder={t('onboarding.suggestName')} value={suggestName} onChange={(e) => setSuggestName(e.target.value)} />
            <Select value={categoryId} options={options} onChange={setSuggestCategory} ariaLabel={t('onboarding.category')} />
            {suggest.isError && (
              <div className="space-y-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">
                <p>{errorMessage(t, suggest.error)}</p>
                {isErrorCode(suggest.error, 'skill_already_exists') && (
                  <button
                    type="button"
                    className="font-medium underline"
                    onClick={() => {
                      setQ(suggestName.trim())
                      setPage(1)
                      suggest.reset()
                      setSuggesting(false)
                    }}
                  >
                    {t('onboarding.findExisting')}
                  </button>
                )}
              </div>
            )}
            <div className="flex justify-end gap-3">
              <button type="button" className="btn-secondary" onClick={() => setSuggesting(false)}>
                {t('common.cancel')}
              </button>
              <button className="btn-primary" disabled={suggest.isPending || !suggestName.trim()}>
                {suggest.isPending ? t('common.loading') : t('onboarding.suggestSend')}
              </button>
            </div>
          </form>
        )}
      </Modal>
    )
  }

  return (
    <Modal title={t('onboarding.pickSkill')} onClose={onClose}>
      <div className="space-y-3">
        <Select
          value={category}
          options={categoryOptions}
          onChange={(v) => {
            setCategory(v)
            setPage(1)
          }}
          ariaLabel={t('onboarding.category')}
        />
        <input
          className="input"
          placeholder={t('onboarding.searchSkill')}
          value={q}
          onChange={(e) => {
            setQ(e.target.value)
            setPage(1)
          }}
        />

        {catalog.isLoading ? (
          <Spinner />
        ) : items.length === 0 ? (
          <p className="text-sm text-slate-500">{t('onboarding.nothingFound')}</p>
        ) : (
          <ul className="max-h-64 space-y-1.5 overflow-y-auto">
            {items.map((s) => (
              <li key={s.id}>
                <button
                  className="w-full rounded-xl bg-brand-50 px-3 py-2 text-left text-sm font-medium text-slate-800 transition hover:bg-brand-100/70"
                  onClick={() => onPick(s)}
                >
                  {pickName(s.names, lang)}
                </button>
              </li>
            ))}
          </ul>
        )}

        <div className="flex items-center justify-between text-sm">
          <div className="flex gap-2">
            <button className="btn-secondary px-3 py-1.5" disabled={page <= 1} onClick={() => setPage(page - 1)}>
              ←
            </button>
            <button className="btn-secondary px-3 py-1.5" disabled={page * PAGE_SIZE >= total} onClick={() => setPage(page + 1)}>
              →
            </button>
          </div>
          <button className="font-medium text-brand-600 hover:underline" onClick={() => setSuggesting(true)}>
            {t('onboarding.suggestLink')}
          </button>
        </div>
      </div>
    </Modal>
  )
}
