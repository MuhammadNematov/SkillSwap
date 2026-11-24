import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'

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

// Первая буква имени (с учётом символов вне базовой плоскости); без имени «?»
function firstLetter(name: string) {
  return (Array.from(name.trim())[0] ?? '?').toUpperCase()
}

function toneOf(name: string) {
  let h = 0
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) >>> 0
  return TONES[h % TONES.length]
}

// Фото поверх кружка с первой буквой имени. Кружок есть всегда: пока фото грузится (к недоступному адресу браузер может ждать десятки секунд),
// если фото нет или оно не загрузилось (onError), на месте остаётся буква, а не пустота. Фото плавно появляется, когда загрузилось.
// Ссылка на аватар подписанная и живёт около часа: если она протухла (403), показываем букву и один раз за минуту
// просим перезагрузить данные, где мог быть аватар.
let refetchedAt = 0

export default function Avatar({ name, src, className = 'h-10 w-10' }: Props) {
  const qc = useQueryClient()
  const [loaded, setLoaded] = useState<string | null>(null) // ссылка, которая успешно загрузилась
  const [failed, setFailed] = useState<string | null>(null) // ссылка, которая не загрузилась
  const showPhoto = !!src && failed !== src

  return (
    <span role="img" aria-label={name} className={`relative inline-flex shrink-0 overflow-hidden rounded-full text-sm ${className}`}>
      <span className={`flex h-full w-full items-center justify-center font-bold ${toneOf(name)}`} aria-hidden>
        {firstLetter(name)}
      </span>
      {showPhoto && (
        <img
          src={src}
          alt=""
          className={`absolute inset-0 h-full w-full object-cover transition-opacity duration-200 ${loaded === src ? 'opacity-100' : 'opacity-0'}`}
          onLoad={() => setLoaded(src)}
          onError={() => {
            setFailed(src)
            if (Date.now() - refetchedAt > 60_000) {
              refetchedAt = Date.now()
              for (const key of ['me', 'profile', 'matches', 'exchanges', 'exchange', 'dashboard', 'conversations']) qc.invalidateQueries({ queryKey: [key] })
            }
          }}
        />
      )}
    </span>
  )
}
