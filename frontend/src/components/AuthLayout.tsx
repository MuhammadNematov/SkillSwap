import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import Avatar from './Avatar'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'

interface Props {
  title: string
  subtitle?: string
  children: ReactNode
}

// Общая рамка для входа, регистрации и писем: слева форма, справа витрина продукта (как в дизайне)
export default function AuthLayout({ title, subtitle, children }: Props) {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-screen flex-wrap gap-5 bg-brand-50 p-5">
      <section className="flex min-h-[640px] flex-1 basis-[480px] flex-col rounded-[2rem] border border-ink-line bg-white px-6 py-8 sm:px-12">
        <div className="flex items-center justify-between">
          <Link to="/">
            <Logo />
          </Link>
          <LanguageSwitcher />
        </div>
        <div className="flex flex-1 items-center justify-center py-8">
          <div className="w-full max-w-[400px]">
            <h1 className="text-[32px] font-extrabold leading-tight tracking-tight text-ink">{title}</h1>
            {subtitle && <p className="mb-7 mt-2 text-[15px] text-ink-soft">{subtitle}</p>}
            <div className={subtitle ? '' : 'mt-7'}>{children}</div>
          </div>
        </div>
        <p className="text-center text-xs text-ink-soft">{t('authPromo.footer')}</p>
      </section>

      <section className="hidden flex-1 basis-[480px] flex-col justify-between rounded-[2rem] border border-ink-line bg-[radial-gradient(120%_140%_at_100%_0%,#E7DFFF_0%,rgba(231,223,255,0)_55%),radial-gradient(90%_120%_at_0%_100%,#F1ECFF_0%,rgba(241,236,255,0)_60%),#fff] p-12 lg:flex">
        <div>
          <p className="text-xs font-bold uppercase tracking-[0.14em] text-brand-600">{t('authPromo.eyebrow')}</p>
          <h2 className="mt-3 max-w-[440px] text-[38px] font-extrabold leading-[1.1] tracking-tight text-ink">{t('authPromo.title')}</h2>
          <p className="mt-4 max-w-[380px] text-[15px] leading-relaxed text-ink-soft">{t('authPromo.text')}</p>
        </div>
        <div className="space-y-4">
          <div className="ml-auto w-[86%] rounded-3xl border border-ink-line bg-white p-5 shadow-card">
            <div className="flex items-center gap-3">
              <Avatar name="Alex Morgan" className="h-11 w-11" />
              <div className="flex-1">
                <p className="text-sm font-bold">Alex Morgan</p>
                <p className="text-xs text-ink-soft">React ⇄ UI/UX</p>
              </div>
              <span className="rounded-full bg-[#E5F7EC] px-2.5 py-1 text-xs font-bold text-[#12A150]">{t('authPromo.match')}</span>
            </div>
          </div>
          <div className="w-[76%] rounded-3xl border border-ink-line bg-white p-[18px]">
            <p className="text-sm font-bold">{t('authPromo.sessionTitle')}</p>
            <p className="mt-0.5 text-xs text-ink-soft">{t('authPromo.sessionTime')}</p>
          </div>
          <div className="ml-auto w-[80%] rounded-3xl border border-ink-line bg-white p-5">
            <p className="text-[15px] text-[#FFB224]">★★★★★</p>
            <p className="mt-2 text-sm leading-relaxed">{t('authPromo.quote')}</p>
          </div>
        </div>
      </section>
    </div>
  )
}
