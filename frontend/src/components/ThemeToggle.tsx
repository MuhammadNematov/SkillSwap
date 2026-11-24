import { useTranslation } from 'react-i18next'
import { useTheme } from '../hooks/useTheme'

// Переключатель светлой и тёмной темы (первое нажатие фиксирует тему, дальше она запоминается)
export default function ThemeToggle() {
  const { t } = useTranslation()
  const { isDark, setMode } = useTheme()
  const label = isDark ? t('common.themeLight') : t('common.themeDark')

  return (
    <button
      type="button"
      onClick={() => setMode(isDark ? 'light' : 'dark')}
      aria-label={label}
      title={label}
      className="grid h-10 w-10 shrink-0 sm:h-11 sm:w-11 place-items-center rounded-xl border border-brand-200 bg-surface text-ink transition hover:border-brand-300 hover:text-brand-600"
    >
      <svg viewBox="0 0 24 24" className="h-[18px] w-[18px]" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
        {isDark ? (
          <>
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
          </>
        ) : (
          <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
        )}
      </svg>
    </button>
  )
}
