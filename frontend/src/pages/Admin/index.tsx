import { useTranslation } from 'react-i18next'

// Заглушка. Админ-панель — позже.
export default function Admin() {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-screen items-center justify-center">
      <h1 className="text-3xl font-bold text-brand-700">{t('admin.title')}</h1>
    </div>
  )
}
