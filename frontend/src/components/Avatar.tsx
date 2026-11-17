interface Props {
  name: string
  src?: string | null
  className?: string
}

// Фото или кружок с первой буквой имени
export default function Avatar({ name, src, className = 'h-10 w-10' }: Props) {
  if (src) {
    return <img src={src} alt={name} className={`${className} rounded-full object-cover`} />
  }
  return (
    <span
      className={`${className} inline-flex shrink-0 items-center justify-center rounded-full bg-brand-100 text-sm font-semibold text-brand-700`}
      aria-label={name}
    >
      {name.trim().charAt(0).toUpperCase() || '?'}
    </span>
  )
}
