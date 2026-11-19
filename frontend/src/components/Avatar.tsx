interface Props {
  name: string
  src?: string | null
  className?: string
}

// Пастельные градиенты из дизайна: цвет выбирается по имени, чтобы у человека он был всегда один и тот же
const TONES = [
  'from-[#D6E6FB] to-[#B9C9F3] text-[#24467A]',
  'from-[#FBD6E6] to-[#F3B9C9] text-[#7A2448]',
  'from-[#D0F5EC] to-[#A9E3E0] text-[#1E6A5C]',
  'from-[#FBE3D0] to-[#F3CDA9] text-[#7A4A1E]',
  'from-[#F0D6FB] to-[#DDB9F3] text-[#5E247A]',
  'from-[#E4D9FF] to-[#CBB8F8] text-[#4A2A9A]',
]

function initials(name: string) {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  const letters = parts.length > 1 ? parts[0][0] + parts[parts.length - 1][0] : (parts[0] ?? '').slice(0, 2)
  return letters.toUpperCase() || '?'
}

function toneOf(name: string) {
  let h = 0
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) >>> 0
  return TONES[h % TONES.length]
}

// Фото или кружок с инициалами
export default function Avatar({ name, src, className = 'h-10 w-10' }: Props) {
  if (src) {
    return <img src={src} alt={name} className={`${className} rounded-full object-cover`} />
  }
  return (
    <span
      className={`${className} inline-flex shrink-0 items-center justify-center rounded-full bg-gradient-to-br text-sm font-bold ${toneOf(name)}`}
      aria-label={name}
    >
      {initials(name)}
    </span>
  )
}
