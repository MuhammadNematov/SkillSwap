import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'
import AuthFloaters from './AuthFloaters'
import AuthInfo from './AuthInfo'
import PhoneSwapScene from './PhoneSwapScene'

interface Props {
  title: string
  subtitle?: string
  children: ReactNode
}

// Общая рамка для входа, регистрации и писем: слева форма, справа витрина продукта (как в дизайне).
// В правой панели: короткое описание, как это работает, и бесконечно летающие навыки.
export default function AuthLayout({ title, subtitle, children }: Props) {
  const { t } = useTranslation()
  return (
    <div className="flex h-screen gap-5 overflow-hidden bg-brand-50 p-5">
      <section className="relative flex min-h-0 flex-1 basis-[480px] flex-col overflow-hidden rounded-2xl border border-ink-line bg-surface px-6 py-6 sm:px-12">
        <div className="flex items-center justify-between">
          <Link to="/">
            <Logo />
          </Link>
          <LanguageSwitcher />
        </div>
        <div className="flex min-h-0 flex-1 overflow-y-auto py-4">
          <div className="relative z-10 m-auto w-full max-w-[400px]">
            <h1 className="text-[32px] font-extrabold leading-tight tracking-tight text-ink">{title}</h1>
            {subtitle && <p className="mb-7 mt-2 text-[15px] text-ink-soft">{subtitle}</p>}
            <div className={subtitle ? '' : 'mt-7'}>{children}</div>
          </div>
        </div>
        <div className="relative z-10 mb-2 hidden xl:hidden [@media(min-height:700px)]:block">
          <AuthInfo compact />
        </div>
        <p className="relative z-10 text-center text-xs text-ink-soft">{t('authPromo.footer')}</p>
        <AuthFloaters />
      </section>

      <section className="hidden min-h-0 flex-1 basis-[480px] flex-col justify-between overflow-hidden rounded-2xl border border-ink-line bg-brand-100/60 p-10 xl:flex">
        <div className="space-y-5">
          <h2 className="max-w-[440px] text-[38px] font-extrabold leading-[1.1] tracking-tight text-ink">{t('authPromo.title')}</h2>
          <p className="max-w-[420px] text-[15px] leading-relaxed text-ink-soft">{t('authPromo.text')}</p>
          <AuthInfo />
        </div>
        <PhoneSwapScene />
      </section>
    </div>
  )
}
