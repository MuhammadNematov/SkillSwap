import type { ReactNode } from 'react'

export type TagTone = 'red' | 'amber' | 'green' | 'brand' | 'gray'

// Названия классов записаны целиком: Tailwind находит классы по тексту исходников, и собранное из частей `tag-${tone}` в сборку не попало бы
const TONE: Record<TagTone, string> = {
  red: 'tag tag-red',
  amber: 'tag tag-amber',
  green: 'tag tag-green',
  brand: 'tag tag-brand',
  gray: 'tag tag-gray',
}

// Цветная метка статуса. Пары «фон/текст» лежат в index.css (переменные --tag-*) и подобраны для обеих тем с контрастом не ниже 4.5:1.
export default function Tag({ tone, children, className = '' }: { tone: TagTone; children: ReactNode; className?: string }) {
  return <span className={`${TONE[tone]} ${className}`}>{children}</span>
}
