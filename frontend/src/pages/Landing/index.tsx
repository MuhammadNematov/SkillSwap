import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import Icon, { type IconName } from '../../components/Icon'
import LanguageSwitcher from '../../components/LanguageSwitcher'
import AuthFloaters from '../../components/AuthFloaters'
import Logo from '../../components/Logo'
import SubjectMarquee from '../../components/SubjectMarquee'
import SwapStage from '../../components/SwapScene'
import { useAuth } from '../../hooks/useAuth'
import { homeForRole } from '../../utils/roles'

const NAV = [
  { key: 'home', href: '#top' },
  { key: 'features', href: '#features' },
  { key: 'subjects', href: '#subjects' },
  { key: 'about', href: '#how' },
  { key: 'faq', href: '#faq' },
] as const

const FEATURES_MORE: { key: 'f5' | 'f6' | 'f7' | 'f8'; icon: IconName }[] = [
  { key: 'f5', icon: 'calendar' },
  { key: 'f6', icon: 'paperclip' },
  { key: 'f7', icon: 'bell' },
  { key: 'f8', icon: 'bolt' },
]

const FEATURES: { key: 'f1' | 'f2' | 'f3' | 'f4' | 'f5' | 'f6' | 'f7' | 'f8'; icon: IconName }[] = [
  { key: 'f1', icon: 'search' },
  { key: 'f2', icon: 'video' },
  { key: 'f3', icon: 'scale' },
  { key: 'f4', icon: 'star' },
  ...FEATURES_MORE,
]

const HERO_FACTS: { key: 'free' | 'video' | 'mutual' | 'safe'; icon: IconName }[] = [
  { key: 'free', icon: 'bolt' },
  { key: 'video', icon: 'video' },
  { key: 'mutual', icon: 'swap' },
  { key: 'safe', icon: 'scale' },
]

const STATS = ['s1', 's2', 's3', 's4'] as const
const JOURNEY = ['j1', 'j2', 'j3', 'j4', 'j5', 'j6'] as const
const SAFETY = ['k1', 'k2', 'k3', 'k4'] as const

const SUBJECTS = [
  { key: 'it' },
  { key: 'math' },
  { key: 'design' },
  { key: 'smm' },
  { key: 'languages' },
  { key: 'business' },
  { key: 'science' },
  { key: 'creative' },
] as const

const AFTER = ['a1', 'a2', 'a3', 'a4'] as const

const FAQ = ['q1', 'q2', 'q3', 'q4'] as const

export default function Landing() {
  const { t } = useTranslation()
  const { user } = useAuth()
  const year = new Date().getFullYear()

  return (
    <div id="top" className="bg-surface">
      <header className="sticky top-0 z-40 border-b border-ink-line bg-surface">
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

      {/* Герой: парень слева, девушка справа, между ними бесконечный обмен навыками */}
      <section className="relative overflow-hidden">
        <div className="relative">
        <AuthFloaters count={26} className="block opacity-70" />
        <SwapStage centerWidth={720} links={false} className="min-h-[680px]">
          <div className="w-full px-4 py-16 text-center md:py-24 lg:pb-32">
            <p className="text-xs font-bold uppercase tracking-[0.14em] text-brand-600">{t('landing.badge')}</p>
            <h1 className="mt-4 text-4xl font-extrabold leading-[1.08] tracking-tight text-ink sm:text-[68px]">
              {t('landing.title1')}
              <br />
              <span className="text-brand-600">{t('landing.title2')}</span>
            </h1>
            <p className="mx-auto mt-6 max-w-xl text-xl text-ink-soft">{t('landing.subtitle')}</p>
            <div className="mt-8 flex flex-wrap justify-center gap-3">
              <Link to="/register" className="btn-primary h-14 rounded-2xl px-8 text-lg">
                {t('landing.cta.start')}
                <Icon name="arrowRight" className="h-4 w-4" />
              </Link>
              <a href="#how" className="btn-secondary h-14 rounded-2xl px-7 text-lg">
                <Icon name="play" className="h-4 w-4" />
                {t('landing.cta.more')}
              </a>
            </div>
            <ul className="mx-auto mt-8 flex max-w-2xl flex-wrap justify-center gap-3">
              {HERO_FACTS.map((f) => (
                <li key={f.key} className="flex items-center gap-2 rounded-full border border-ink-line bg-surface/90 px-4 py-2 text-sm font-semibold text-ink">
                  <Icon name={f.icon} className="h-4 w-4 text-brand-600" />
                  {t(`landing.heroFacts.${f.key}`)}
                </li>
              ))}
            </ul>
          </div>
        </SwapStage>
        </div>
        <SubjectMarquee />

        {/* Цифры из ТЗ */}
        <div className="mx-auto grid max-w-6xl gap-4 px-4 pb-20 sm:grid-cols-2 sm:px-6 lg:grid-cols-4">
          {STATS.map((k) => (
            <div key={k} className="rounded-3xl border border-ink-line bg-brand-50 p-7 text-center">
              <div className="text-4xl font-extrabold text-brand-700 sm:text-5xl">{t(`landing.stats.${k}.value`)}</div>
              <div className="mt-2 text-sm text-ink-soft">{t(`landing.stats.${k}.label`)}</div>
            </div>
          ))}
        </div>
      </section>

      {/* Чему можно научиться */}
      <section id="subjects" className="scroll-mt-16 mx-auto max-w-6xl px-4 py-20 sm:px-6">
        <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.subjects.title')}</h2>
        <p className="mx-auto mt-3 max-w-2xl text-center text-ink-soft">{t('landing.subjects.subtitle')}</p>
        <div className="mt-10 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
          {SUBJECTS.map((s) => (
            <div key={s.key} className={`rounded-3xl border border-ink-line bg-surface p-5 transition hover:border-brand-300`}>
              <div className="text-lg font-bold text-ink">{t(`landing.subjects.${s.key}.title`)}</div>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {(t(`landing.subjects.${s.key}.items`, { returnObjects: true }) as string[]).map((item) => (
                  <span key={item} className="rounded-full bg-brand-100 px-2.5 py-1 text-xs font-semibold text-brand-700">
                    {item}
                  </span>
                ))}
              </div>
            </div>
          ))}
        </div>
        <p className="mt-8 text-center text-lg font-bold text-ink">{t('landing.subjects.more')}</p>
      </section>

      {/* Для кого */}
      <section className="mx-auto max-w-6xl px-4 py-20 sm:px-6">
        <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.audience.title')}</h2>
        <div className="mt-12 grid gap-6 md:grid-cols-2">
          {(['teacher', 'learner'] as const).map((k) => (
            <div key={k} className="rounded-3xl border border-ink-line bg-surface p-8">
              <h3 className="text-2xl font-extrabold text-ink">{t(`landing.audience.${k}.title`)}</h3>
              <p className="mt-2 text-ink-soft">{t(`landing.audience.${k}.text`)}</p>
              <ul className="mt-5 space-y-2.5">
                {(t(`landing.audience.${k}.points`, { returnObjects: true }) as string[]).map((pt) => (
                  <li key={pt} className="flex items-start gap-3 text-[15px] text-ink">
                    <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-brand-100 text-brand-700">
                      <Icon name="check" className="h-3 w-3" />
                    </span>
                    {pt}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </section>

      {/* Как это работает */}
      <section id="how" className="scroll-mt-16 bg-brand-50 py-20">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.journey.title')}</h2>
          <p className="mx-auto mt-3 max-w-2xl text-center text-ink-soft">{t('landing.journey.subtitle')}</p>
          <ol className="mt-12 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
            {JOURNEY.map((k, i) => (
              <li key={k} className="relative rounded-3xl border border-ink-line bg-surface p-7">
                <span className="flex h-11 w-11 items-center justify-center rounded-full bg-brand-600 text-lg font-extrabold text-white">{i + 1}</span>
                <div className="mt-4 text-xl font-bold text-ink">{t(`landing.journey.${k}.title`)}</div>
                <p className="mt-2 text-sm leading-relaxed text-ink-soft">{t(`landing.journey.${k}.text`)}</p>
              </li>
            ))}
          </ol>
        </div>
      </section>

      {/* Возможности */}
      <section id="features" className="scroll-mt-16">
        <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6">
          <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.features.title')}</h2>
          <div className="mt-12 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
            {FEATURES.map((f) => (
              <div key={f.key} className="card flex flex-col gap-4">
                <span className="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-brand-100 text-brand-700">
                  <Icon name={f.icon} className="h-6 w-6" />
                </span>
                <div>
                  <div className="font-bold text-ink">{t(`landing.features.${f.key}.title`)}</div>
                  <p className="mt-1 text-sm text-slate-500">{t(`landing.features.${f.key}.text`)}</p>
                </div>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Безопасность и правила */}
      <section className="mx-auto max-w-6xl px-4 py-20 sm:px-6">
        <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.safety.title')}</h2>
        <p className="mx-auto mt-3 max-w-2xl text-center text-ink-soft">{t('landing.safety.subtitle')}</p>
        <div className="mt-12 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
          {SAFETY.map((k) => (
            <div key={k} className="rounded-3xl border border-ink-line bg-surface p-6">
              <span className="flex h-11 w-11 items-center justify-center rounded-2xl bg-[#E5F7EC] text-[#12A150]">
                <Icon name="check" className="h-6 w-6" />
              </span>
              <div className="mt-4 text-lg font-bold text-ink">{t(`landing.safety.${k}.title`)}</div>
              <p className="mt-2 text-sm leading-relaxed text-ink-soft">{t(`landing.safety.${k}.text`)}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Что будет после регистрации */}
      <section className="mx-auto max-w-5xl px-4 py-20 sm:px-6">
        <div className="rounded-2xl bg-[#17143d] p-8 text-white sm:p-12">
          <h2 className="text-3xl font-extrabold tracking-tight sm:text-4xl">{t('landing.after.title')}</h2>
          <ol className="mt-8 grid gap-4 sm:grid-cols-2">
            {AFTER.map((a, i) => (
              <li key={a} className="flex gap-4 rounded-2xl bg-white/10 p-4">
                <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-brand-600 text-lg font-extrabold text-white">{i + 1}</span>
                <div>
                  <div className="font-bold">{t(`landing.after.${a}.title`)}</div>
                  <p className="mt-1 text-sm text-white/70">{t(`landing.after.${a}.text`)}</p>
                </div>
              </li>
            ))}
          </ol>
          {!user && (
            <Link to="/register" className="btn-primary mt-8 h-14 rounded-2xl px-8 text-lg">
              {t('landing.cta.start')}
              <Icon name="arrowRight" className="h-4 w-4" />
            </Link>
          )}
        </div>
      </section>

      {/* FAQ */}
      <section id="faq" className="scroll-mt-16 mx-auto max-w-3xl px-4 py-16 sm:px-6">
        <h2 className="text-center text-4xl font-extrabold tracking-tight text-ink sm:text-5xl">{t('landing.faq.title')}</h2>
        <div className="mt-8 space-y-3">
          {FAQ.map((q) => (
            <details key={q} className="group card cursor-pointer">
              <summary className="flex list-none items-center justify-between gap-4 font-semibold text-ink [&::-webkit-details-marker]:hidden">
                {t(`landing.faq.${q}.q`)}
                <Icon name="chevronDown" className="h-5 w-5 shrink-0 text-brand-600 transition group-open:rotate-180" />
              </summary>
              <p className="mt-3 text-sm text-slate-500">{t(`landing.faq.${q}.a`)}</p>
            </details>
          ))}
        </div>
      </section>

      <footer className="border-t border-ink-line bg-surface py-8 text-center text-sm text-ink-soft">
        © {year} {t('common.appName')}
      </footer>
    </div>
  )
}
