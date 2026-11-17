import { useTranslation } from 'react-i18next'

// Временная страница для разделов, которые сделаем в следующих шагах
export default function Placeholder({ titleKey }: { titleKey: string }) {
  const { t } = useTranslation()
  return (
    <div>
      <h1 className="text-2xl font-bold text-slate-900">{t(titleKey)}</h1>
      <div className="card mt-6 text-sm text-slate-500">{t('shell.soon')}</div>
    </div>
  )
}
