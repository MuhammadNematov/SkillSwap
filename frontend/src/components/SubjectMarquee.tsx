import { useTranslation } from 'react-i18next'

// Бегущая строка с предметами под героем лендинга. При reduced motion не двигается.
export default function SubjectMarquee() {
  const { t } = useTranslation()
  const raw = t('authPromo.floaters', { returnObjects: true }) as unknown
  const items = Array.isArray(raw) ? (raw as string[]) : []
  const row = (
    <ul className="flex shrink-0 items-center gap-3 pr-3">
      {items.map((s) => (
        <li key={s} className="whitespace-nowrap rounded-full border border-ink-line bg-surface px-4 py-1.5 text-sm font-semibold text-brand-700">
          {s}
        </li>
      ))}
    </ul>
  )
  return (
    <div className="overflow-hidden py-4" aria-hidden>
      <style>{`@keyframes subject-marquee { to { transform: translateX(-50%); } } @media (prefers-reduced-motion: reduce) { .subject-marquee { animation: none !important; } }`}</style>
      <div className="subject-marquee flex w-max" style={{ animation: 'subject-marquee 45s linear infinite' }}>
        {row}
        {row}
      </div>
    </div>
  )
}
