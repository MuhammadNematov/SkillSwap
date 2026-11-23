import { useTranslation } from 'react-i18next'
import Icon, { type IconName } from './Icon'

// Учебные значки, медленно плавающие над блоком «Как это работает» (не мешают чтению, выключаются при reduced motion)
const FLOATERS: { label: string; left: string; top: string; delay: string; dur: string }[] = [
  { label: '</>', left: '4%', top: '-6px', delay: '0s', dur: '5s' },
  { label: 'Aa', left: '26%', top: '-14px', delay: '-1.4s', dur: '6s' },
  { label: 'Σ', left: '49%', top: '-8px', delay: '-2.6s', dur: '5.5s' },
  { label: '♫', left: '70%', top: '-14px', delay: '-0.8s', dur: '6.5s' },
  { label: '✎', left: '90%', top: '-6px', delay: '-3.4s', dur: '5.2s' },
]

const STEPS: { icon: IconName; n: 1 | 2 | 3 }[] = [
  { icon: 'search', n: 1 },
  { icon: 'swap', n: 2 },
  { icon: 'video', n: 3 },
]

export default function AuthInfo({ compact = false }: { compact?: boolean }) {
  const { t } = useTranslation()
  // Компактный вид под формой, когда правая панель скрыта: три шага в строку и теги
  if (compact)
    return (
      <div className="space-y-2 text-center">
        <p className="text-xs font-semibold text-ink-soft">{STEPS.map((s) => t(`authPromo.step${s.n}Title`)).join(' → ')}</p>
        <ul className="flex flex-wrap justify-center gap-1.5 text-[11px] font-semibold text-brand-700">
          {(['free', 'video', 'safe'] as const).map((k) => (
            <li key={k} className="rounded-full bg-brand-100 px-2.5 py-0.5">
              {t(`authPromo.tag_${k}`)}
            </li>
          ))}
        </ul>
      </div>
    )
  return (
    <div className="hidden [@media(min-height:780px)]:block">
      <div className="relative h-8" aria-hidden>
        {FLOATERS.map((f) => (
          <span
            key={f.label}
            className="absolute flex h-8 min-w-8 items-center justify-center rounded-lg bg-surface px-2 text-sm font-bold text-brand-700 shadow-sm motion-reduce:animate-none"
            style={{ left: f.left, top: f.top, animation: `swap-float ${f.dur} ease-in-out ${f.delay} infinite` }}
          >
            {f.label}
          </span>
        ))}
      </div>
      <h3 className="mb-2 mt-1 text-xs font-bold uppercase tracking-wider text-ink-soft">{t('authPromo.how')}</h3>
      <ol className="space-y-2.5">
        {STEPS.map((s) => (
          <li key={s.n} className="flex items-start gap-3 rounded-xl bg-surface/70 px-3 py-2.5">
            <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand-600 text-white">
              <Icon name={s.icon} className="h-5 w-5" />
            </span>
            <div className="min-w-0">
              <div className="text-sm font-bold text-ink">
                {s.n}. {t(`authPromo.step${s.n}Title`)}
              </div>
              <div className="text-xs leading-relaxed text-ink-soft">{t(`authPromo.step${s.n}Text`)}</div>
            </div>
          </li>
        ))}
      </ol>
      <ul className="mt-3 flex flex-wrap gap-2 text-xs font-semibold text-brand-700">
        {(['free', 'video', 'safe', 'languages'] as const).map((k) => (
          <li key={k} className="rounded-full bg-surface px-3 py-1">
            {t(`authPromo.tag_${k}`)}
          </li>
        ))}
      </ul>
    </div>
  )
}
