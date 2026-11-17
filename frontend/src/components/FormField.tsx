import type { ReactNode } from 'react'
import type { FieldError } from 'react-hook-form'
import { useTranslation } from 'react-i18next'

interface Props {
  label: string
  error?: FieldError
  hint?: string
  children: ReactNode
}

// Подпись + поле + текст ошибки. В error.message лежит ключ перевода.
export default function FormField({ label, error, hint, children }: Props) {
  const { t } = useTranslation()
  return (
    <label className="block">
      <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span>
      {children}
      {error?.message ? (
        <span className="mt-1 block text-xs text-red-600">{t(error.message)}</span>
      ) : hint ? (
        <span className="mt-1 block text-xs text-slate-400">{hint}</span>
      ) : null}
    </label>
  )
}
