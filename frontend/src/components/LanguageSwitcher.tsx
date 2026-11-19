import { useTranslation } from 'react-i18next'
import { LANGUAGES } from '../i18n'
import ThemeToggle from './ThemeToggle'
import Select, { type SelectOption } from './Select'

const options: SelectOption[] = LANGUAGES.map((l) => ({ value: l.code, label: l.label }))

// Язык переключается без перезагрузки страницы
export default function LanguageSwitcher() {
  const { i18n, t } = useTranslation()

  return (
    <div className="flex items-center gap-2">
      <ThemeToggle />
      <Select
      value={i18n.resolvedLanguage ?? 'ru'}
      options={options}
      onChange={(code) => i18n.changeLanguage(code)}
      ariaLabel={t('common.language')}
      align="right"
      className="w-36"
      />
    </div>
  )
}
