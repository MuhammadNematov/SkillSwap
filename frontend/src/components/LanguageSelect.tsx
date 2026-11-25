import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import type { LanguageOption } from '../api/types'
import { MAX_LANGUAGE_RESULTS, POPULAR_LANGUAGES, searchLanguages } from '../utils/languageSearch'
import { pickName } from '../utils/localized'
import Select, { type SelectOption } from './Select'

// Основные языки сайта сразу под «Любой»; остальные популярные под разделителем (список POPULAR_LANGUAGES без основных)
const MAIN = ['tg', 'ru', 'en']
const POPULAR = POPULAR_LANGUAGES.filter((c) => !MAIN.includes(c))

interface Props {
  value: string // код языка или '' = любой
  onChange: (code: string) => void
  options: readonly LanguageOption[]
  anyLabel: string
  ariaLabel: string
  className?: string
}

// Выбор одного языка (фильтр «Язык общения»): та же логика поиска, что в выборе языков занятий (searchLanguages): по названию на tg, ru, en и по коду, без учёта регистра, до 8 результатов.
// При открытии: «Любой», основные языки, под разделителем популярные; поле поиска сразу в фокусе; список ограничен по высоте, на телефоне шторка снизу.
export default function LanguageSelect({ value, onChange, options, anyLabel, ariaLabel, className }: Props) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'

  const selectOptions = useMemo<SelectOption[]>(
    () => [{ value: '', label: anyLabel }, ...options.map((o) => ({ value: o.code, label: pickName(o.names, lang), hint: o.code })) ],
    [options, lang, anyLabel],
  )
  const byCode = useMemo(() => new Map(selectOptions.map((o) => [o.value, o])), [selectOptions])
  const search = (query: string) =>
    searchLanguages(options, query, [], lang)
      .map((o) => byCode.get(o.code))
      .filter((o): o is SelectOption => !!o)

  return (
    <Select
      className={className}
      value={value}
      onChange={onChange}
      ariaLabel={ariaLabel}
      options={selectOptions}
      searchable
      searchPlaceholder={t('languagePicker.search')}
      pinned={MAIN}
      popular={POPULAR}
      popularLabel={t('languagePicker.popular')}
      search={search}
      maxResults={MAX_LANGUAGE_RESULTS}
    />
  )
}
