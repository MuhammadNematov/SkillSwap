import { useTranslation } from 'react-i18next'

// Заглушка. Панель модератора — позже.
export default function Moderation() {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-screen items-center justify-center">
      <h1 className="text-3xl font-bold text-brand-700">{t('moderation.title')}</h1>
    </div>
  )
}
