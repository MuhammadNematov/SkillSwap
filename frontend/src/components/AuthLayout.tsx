import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import LanguageSwitcher from './LanguageSwitcher'
import Logo from './Logo'
import PhoneSwapScene from './PhoneSwapScene'

interface Props {
  title: string
  subtitle?: string
  children: ReactNode
}

// Общая рамка для входа, регистрации и писем: слева форма, справа витрина продукта (как в дизайне).
// По бокам формы стоят два небольших 3D-персонажа, в правой панели бесконечно летают навыки.
export default function AuthLayout({ title, subtitle, children }: Props) {
  const { t } = useTranslation()
  return (
    <div className="flex h-screen gap-5 overflow-hidden bg-brand-50 p-5">
      <section className="relative flex min-h-0 flex-1 basis-[480px] flex-col overflow-hidden rounded-[2rem] border border-ink-line bg-white px-6 py-6 sm:px-12">
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
        <p className="relative z-10 text-center text-xs text-ink-soft">{t('authPromo.footer')}</p>

        {/* 3D-модели без фона по бокам формы */}
        <img
          src="/characters/boy.webp"
          alt=""
          draggable={false}
          className="pointer-events-none absolute bottom-12 left-[3%] hidden w-[clamp(80px,11vw,150px)] drop-shadow-[0_12px_14px_rgba(23,20,61,.25)] md:block"
        />
        <img
          src="/characters/girl.webp"
          alt=""
          draggable={false}
          className="pointer-events-none absolute bottom-12 right-[3%] hidden w-[clamp(90px,12vw,165px)] drop-shadow-[0_12px_14px_rgba(23,20,61,.25)] md:block"
        />
      </section>

      <section className="hidden min-h-0 flex-1 basis-[480px] flex-col justify-between overflow-hidden rounded-[2rem] border border-ink-line bg-[radial-gradient(120%_140%_at_100%_0%,#E7DFFF_0%,rgba(231,223,255,0)_55%),radial-gradient(90%_120%_at_0%_100%,#F1ECFF_0%,rgba(241,236,255,0)_60%),#fff] p-10 lg:flex">
        <div>
          <p className="text-xs font-bold uppercase tracking-[0.14em] text-brand-600">{t('authPromo.eyebrow')}</p>
          <h2 className="mt-3 max-w-[440px] text-[38px] font-extrabold leading-[1.1] tracking-tight text-ink">{t('authPromo.title')}</h2>
          <p className="mt-4 max-w-[380px] text-[15px] leading-relaxed text-ink-soft">{t('authPromo.text')}</p>
        </div>
        <PhoneSwapScene />
      </section>
    </div>
  )
}
