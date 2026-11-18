export default function SkillChip({ name, tone = 'brand' }: { name: string; tone?: 'brand' | 'green' }) {
  const colors = tone === 'green' ? 'bg-emerald-100/80 text-emerald-700' : 'bg-brand-100/80 text-brand-700'
  return <span className={`rounded-lg px-2.5 py-1 text-xs font-semibold ${colors}`}>{name}</span>
}
