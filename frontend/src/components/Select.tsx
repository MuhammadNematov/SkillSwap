import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import Icon from './Icon'

export interface SelectOption {
  value: string
  label: string
}

interface Props {
  value: string
  options: readonly SelectOption[]
  onChange: (value: string) => void
  ariaLabel?: string
  className?: string
  align?: 'left' | 'right'
}

// Свой выпадающий список вместо системного <select>: стеклянный стиль, и сам список (options) тоже оформлен.
// Работает с клавиатурой: ↑ ↓ Enter Esc.
export default function Select({ value, options, onChange, ariaLabel, className = '', align = 'left' }: Props) {
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const listId = useId()
  const current = options.find((o) => o.value === value) ?? options[0]

  // Клик вне списка закрывает его
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [open])

  function toggle() {
    if (!open) setActive(Math.max(0, options.findIndex((o) => o.value === value)))
    setOpen((o) => !o)
  }

  function choose(index: number) {
    onChange(options[index].value)
    setOpen(false)
  }

  function onKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      setOpen(false)
    } else if (e.key === 'ArrowDown') {
      e.preventDefault()
      if (!open) toggle()
      else setActive((a) => (a + 1) % options.length)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      if (!open) toggle()
      else setActive((a) => (a - 1 + options.length) % options.length)
    } else if ((e.key === 'Enter' || e.key === ' ') && open) {
      e.preventDefault()
      choose(active)
    }
  }

  return (
    <div ref={rootRef} className={`relative ${className}`}>
      <button
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        aria-label={ariaLabel}
        onClick={toggle}
        onKeyDown={onKeyDown}
        className="flex w-full items-center justify-between gap-2 rounded-xl border border-ink-line bg-brand-50 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-white focus:outline-none focus:ring-2 focus:ring-brand-200"
      >
        <span className="truncate">{current?.label}</span>
        <Icon name="chevronDown" className={`h-4 w-4 shrink-0 text-brand-600 transition ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && (
        <ul
          id={listId}
          role="listbox"
          className={`absolute z-50 mt-2 min-w-full overflow-hidden rounded-xl border border-ink-line bg-white p-1 shadow-glass ${
            align === 'right' ? 'right-0' : 'left-0'
          }`}
        >
          {options.map((o, i) => (
            <li
              key={o.value}
              role="option"
              aria-selected={o.value === value}
              onMouseEnter={() => setActive(i)}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => choose(i)}
              className={`flex cursor-pointer items-center justify-between gap-4 whitespace-nowrap rounded-lg px-3 py-2 text-sm transition ${
                i === active ? 'bg-brand-100 text-brand-800' : 'text-slate-700'
              } ${o.value === value ? 'font-semibold' : ''}`}
            >
              {o.label}
              {o.value === value && <Icon name="check" className="h-4 w-4 text-brand-600" />}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
