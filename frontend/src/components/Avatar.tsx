import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'

interface Props {
  name: string
  src?: string | null
  className?: string
}

// Пастельные градиенты из дизайна: цвет выбирается по имени, чтобы у человека он был всегда один и тот же
const TONES = [
  'bg-[#D6E6FB] text-[#24467A]',
  'bg-[#FBD6E6] text-[#7A2448]',
  'bg-[#D0F5EC] text-[#1E6A5C]',
  'bg-[#FBE3D0] text-[#7A4A1E]',
  'bg-[#F0D6FB] text-[#5E247A]',
  'bg-[#E4D9FF] text-[#4A2A9A]',
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
// Ссылка на аватар подписанная и живёт около часа: если она протухла (403), показываем инициалы
// и один раз за сеанс просим перезагрузить данные, где мог быть аватар.
let refetchedAt = 0

export default function Avatar({ name, src, className = 'h-10 w-10' }: Props) {
  const qc = useQueryClient()
  const [failed, setFailed] = useState<string | null>(null)
  if (src && failed !== src) {
    return (
      <img
        src={src}
        alt={name}
        className={`${className} rounded-full object-cover`}
        onError={() => {
          setFailed(src)
          if (Date.now() - refetchedAt > 60_000) {
            refetchedAt = Date.now()
            for (const key of ['me', 'profile', 'matches', 'exchanges', 'exchange', 'dashboard', 'conversations']) qc.invalidateQueries({ queryKey: [key] })
          }
        }}
      />
    )
  }
  return (
    <span
      className={`${className} inline-flex shrink-0 items-center justify-center rounded-full text-sm font-bold ${toneOf(name)}`}
      aria-label={name}
    >
      {initials(name)}
    </span>
  )
}
