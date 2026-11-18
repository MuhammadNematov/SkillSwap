import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import HeroBook, { HeroParticles } from '../../components/HeroBook'
import Icon, { type IconName } from '../../components/Icon'
import LanguageSwitcher from '../../components/LanguageSwitcher'
import Logo from '../../components/Logo'
import { useAuth } from '../../hooks/useAuth'
import { homeForRole } from '../../utils/roles'

const NAV = [
  { key: 'home', href: '#top' },
  { key: 'features', href: '#features' },
  { key: 'about', href: '#how' },
  { key: 'faq', href: '#faq' },
] as const

const STEPS: { key: 's1' | 's2' | 's3'; icon: IconName }[] = [
  { key: 's1', icon: 'user' },
  { key: 's2', icon: 'search' },
  { key: 's3', icon: 'video' },
]

const FEATURES: { key: 'f1' | 'f2' | 'f3' | 'f4'; icon: IconName }[] = [
  { key: 'f1', icon: 'search' },
  { key: 'f2', icon: 'video' },
  { key: 'f3', icon: 'scale' },
  { key: 'f4', icon: 'star' },
]

const FACTS = ['languages', 'lesson', 'free'] as const
const FAQ = ['q1', 'q2', 'q3', 'q4'] as const

export default function Landing() {
  const { t } = useTranslation()
  const { user } = useAuth()
  const year = new Date().getFullYear()

  return (
    <div id="top">
      <header className="sticky top-0 z-20 border-b border-white/60 bg-white/50 backdrop-blur-xl">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-4 px-4 py-3 sm:px-6">
          <a href="#top">
            <Logo />
          </a>

          <nav className="hidden items-center gap-6 text-sm font-medium text-slate-600 md:flex">
            {NAV.map((n) => (
              <a key={n.key} href={n.href} className="hover:text-brand-700">
                {t(`landing.nav.${n.key}`)}
              </a>
            ))}
          </nav>

          <div className="flex items-center gap-2 sm:gap-3">
            <LanguageSwitcher />
            {user ? (
              <Link to={homeForRole(user.role)} className="btn-primary">
                {t('landing.openApp')}
              </Link>
            ) : (
              <>
                <Link to="/login" className="btn-secondary hidden sm:inline-flex">
                  {t('auth.login')}
                </Link>
                <Link to="/register" className="btn-primary">
                  {t('auth.register')}
                </Link>
              </>
            )}
          </div>
        </div>
      </header>

      {/* Герой */}
      <section className="relative overflow-hidden">
        <HeroParticles />
        <div className="relative mx-auto grid max-w-6xl items-center gap-12 px-4 py-16 sm:px-6 md:grid-cols-2 md:py-24">
          <div>
            <h1 className="text-4xl font-extrabold leading-tight text-slate-900 sm:text-5xl">
              {t('landing.title1')}
              <br />
              <span className="text-brand-600">{t('landing.title2')}</span>
            </h1>
            <p className="mt-5 max-w-md text-lg text-slate-500">{t('landing.subtitle')}</p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Link to="/register" className="btn-primary px-6 py-3">
                {t('landing.cta.start')}
                <Icon name="arrowRight" className="h-4 w-4" />
              </Link>
              <a href="#how" className="btn-secondary px-6 py-3">
                <Icon name="play" className="h-4 w-4" />
                {t('landing.cta.more')}
              </a>
            </div>
          </div>

          {/* 3D-книга: прилетает, открывается и бесконечно покачивается */}
          <HeroBook />
        </div>

        {/* Факты из ТЗ вместо выдуманной статистики */}
        <div className="relative mx-auto grid max-w-4xl gap-4 px-4 pb-14 sm:grid-cols-3 sm:px-6">
          {FACTS.map((f) => (
            <div key={f} className="glass rounded-xl2 p-5 text-center">
              <div className="text-2xl font-extrabold text-brand-700">{t(`landing.facts.${f}.value`)}</div>
              <div className="mt-1 text-sm text-slate-500">{t(`landing.facts.${f}.label`)}</div>
            </div>
          ))}
        </div>
      </section>

      {/* Как это работает */}
      <section id="how" className="scroll-mt-16 mx-auto max-w-6xl px-4 py-16 sm:px-6">
        <h2 className="text-center text-3xl font-bold text-slate-900">{t('landing.how.title')}</h2>
        <div className="mt-10 grid gap-6 md:grid-cols-3">
          {STEPS.map((s, i) => (
            <div key={s.key} className="card text-center">
              <span className="mx-auto flex h-14 w-14 items-center justify-center rounded-2xl bg-brand-100 text-brand-700">
                <Icon name={s.icon} className="h-7 w-7" />
              </span>
              <div className="mt-4 text-lg font-bold text-slate-900">
                {i + 1}. {t(`landing.how.${s.key}.title`)}
              </div>
              <p className="mt-2 text-sm text-slate-500">{t(`landing.how.${s.key}.text`)}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Возможности */}
      <section id="features" className="scroll-mt-16">
        <div className="mx-auto max-w-6xl px-4 py-16 sm:px-6">
          <h2 className="text-center text-3xl font-bold text-slate-900">{t('landing.features.title')}</h2>
          <div className="mt-10 grid gap-6 sm:grid-cols-2">
            {FEATURES.map((f) => (
              <div key={f.key} className="card flex gap-4">
                <span className="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-brand-100 text-brand-700">
                  <Icon name={f.icon} className="h-6 w-6" />
                </span>
                <div>
                  <div className="font-bold text-slate-900">{t(`landing.features.${f.key}.title`)}</div>
                  <p className="mt-1 text-sm text-slate-500">{t(`landing.features.${f.key}.text`)}</p>
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* FAQ */}
      <section id="faq" className="scroll-mt-16 mx-auto max-w-3xl px-4 py-16 sm:px-6">
        <h2 className="text-center text-3xl font-bold text-slate-900">{t('landing.faq.title')}</h2>
        <div className="mt-8 space-y-3">
          {FAQ.map((q) => (
            <details key={q} className="group card cursor-pointer">
              <summary className="flex list-none items-center justify-between gap-4 font-semibold text-slate-900 [&::-webkit-details-marker]:hidden">
                {t(`landing.faq.${q}.q`)}
                <Icon name="chevronDown" className="h-5 w-5 shrink-0 text-brand-600 transition group-open:rotate-180" />
              </summary>
              <p className="mt-3 text-sm text-slate-500">{t(`landing.faq.${q}.a`)}</p>
            </details>
          ))}
        </div>
      </section>

      <footer className="border-t border-white/60 py-8 text-center text-sm text-slate-500">
        © {year} {t('common.appName')}
      </footer>
    </div>
  )
}
