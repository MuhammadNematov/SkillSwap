import Icon from './Icon'

interface Props {
  value: number
  onChange?: (v: number) => void
  label?: string
}

// Оценка 1–5. Без onChange — только показ
export default function StarRating({ value, onChange, label }: Props) {
  return (
    <div className="flex gap-1" role={onChange ? 'radiogroup' : undefined} aria-label={label}>
      {[1, 2, 3, 4, 5].map((n) => (
        <button
          key={n}
          type="button"
          disabled={!onChange}
          role={onChange ? 'radio' : undefined}
          aria-checked={n === value}
          aria-label={`${n}`}
          onClick={() => onChange?.(n)}
          className={`rounded p-0.5 ${n <= value ? 'text-amber-400' : 'text-slate-300'} ${onChange ? 'hover:text-amber-500' : 'cursor-default'}`}
        >
          <Icon name="star" className={`h-6 w-6 ${n <= value ? 'fill-current' : ''}`} />
        </button>
      ))}
    </div>
  )
}
