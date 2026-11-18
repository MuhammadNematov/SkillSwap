import { useEffect, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import Icon from './Icon'

interface Props {
  title: string
  onClose: () => void
  children: ReactNode
}

// Стеклянное модальное окно: закрывается по Esc и по клику на фон
export default function Modal({ title, onClose, children }: Props) {
  const { t } = useTranslation()

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    const prevOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = prevOverflow
    }
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-label={title}>
      <div className="absolute inset-0 bg-slate-900/30 backdrop-blur-sm" onClick={onClose} />
      <div className="card relative max-h-[90vh] w-full max-w-lg overflow-y-auto bg-white/80 p-6">
        <div className="flex items-center justify-between gap-4">
          <h2 className="text-xl font-bold text-slate-900">{title}</h2>
          <button className="rounded-lg p-1.5 text-slate-500 hover:bg-white/60" aria-label={t('shell.close')} onClick={onClose}>
            <Icon name="x" />
          </button>
        </div>
        <div className="mt-5">{children}</div>
      </div>
    </div>
  )
}
