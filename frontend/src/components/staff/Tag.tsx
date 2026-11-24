import type { ReactNode } from 'react'

export type TagTone = 'red' | 'amber' | 'green' | 'brand' | 'gray'

// Цветная метка статуса. Пары «фон/текст» лежат в index.css (переменные --tag-*) и подобраны для обеих тем с контрастом не ниже 4.5:1.
export default function Tag({ tone, children, className = '' }: { tone: TagTone; children: ReactNode; className?: string }) {
  return <span className={`tag tag-${tone} ${className}`}>{children}</span>
}
