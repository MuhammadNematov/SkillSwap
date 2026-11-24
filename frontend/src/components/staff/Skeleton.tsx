// Заглушки на время загрузки: вместо пустого экрана или полноэкранного крутилки показываем контуры будущего содержимого.
export function Skeleton({ className = '' }: { className?: string }) {
  return <div className={`skeleton ${className}`} aria-hidden />
}

// Список строк-карточек (обращения, запросы, пользователи)
export function SkeletonList({ rows = 4, label }: { rows?: number; label: string }) {
  return (
    <div className="space-y-3" role="status" aria-busy="true" aria-label={label}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="card space-y-2.5">
          <div className="flex gap-2">
            <Skeleton className="h-5 w-32" />
            <Skeleton className="h-5 w-20 !rounded-full" />
          </div>
          <Skeleton className="h-4 w-1/3" />
          <Skeleton className="h-4 w-full" />
        </div>
      ))}
    </div>
  )
}

// Карточки-счётчики обзора
export function SkeletonStats({ count = 5, label }: { count?: number; label: string }) {
  return (
    <div className="grid grid-cols-1 gap-3 min-[420px]:grid-cols-2 lg:grid-cols-3" role="status" aria-busy="true" aria-label={label}>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="card space-y-3">
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-9 w-16" />
        </div>
      ))}
    </div>
  )
}
