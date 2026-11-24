import { useRef, type ClipboardEvent, type KeyboardEvent } from 'react'

interface Props {
  value: string // только цифры, до length штук
  onChange: (value: string) => void
  onComplete?: (value: string) => void // вызывается, когда набраны все цифры (ввод или вставка целиком)
  length?: number
  autoFocus?: boolean
  disabled?: boolean
  invalid?: boolean
  ariaLabel: string
}

// Код из цифр в отдельных клетках: одна клетка на цифру, автопереход, вставка кода целиком, Backspace и стрелки.
// Подходит для кода из письма и для кода двухфакторной защиты: после последней цифры сразу вызывается onComplete (автоотправка).
// autoComplete="one-time-code" на всех клетках: телефон подставляет код из SMS и писем, а менеджеры паролей, из приложений.
export default function CodeInput({ value, onChange, onComplete, length = 6, autoFocus = false, disabled = false, invalid = false, ariaLabel }: Props) {
  const refs = useRef<(HTMLInputElement | null)[]>([])
  const digits = value.replace(/\D/g, '').slice(0, length)

  function commit(next: string, focusAt?: number) {
    onChange(next)
    if (focusAt !== undefined) refs.current[Math.min(focusAt, length - 1)]?.focus()
    if (next.length === length) onComplete?.(next)
  }

  function onInput(i: number, raw: string) {
    const typed = raw.replace(/\D/g, '')
    if (!typed) return
    // Несколько цифр сразу (автозаполнение или вставка в одну клетку) раскладываются с этой клетки
    const next = (digits.slice(0, i) + typed + digits.slice(i + typed.length)).slice(0, length)
    commit(next, i + typed.length)
  }

  function onKeyDown(i: number, e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Backspace') {
      e.preventDefault()
      if (digits[i]) commit(digits.slice(0, i) + digits.slice(i + 1), i)
      else if (i > 0) commit(digits.slice(0, i - 1) + digits.slice(i), i - 1)
    } else if (e.key === 'ArrowLeft' && i > 0) refs.current[i - 1]?.focus()
    else if (e.key === 'ArrowRight' && i < length - 1) refs.current[i + 1]?.focus()
  }

  function onPaste(e: ClipboardEvent<HTMLInputElement>) {
    const pasted = e.clipboardData.getData('text').replace(/\D/g, '').slice(0, length)
    if (!pasted) return
    e.preventDefault()
    commit(pasted, pasted.length)
  }

  return (
    <div className="flex gap-2" role="group" aria-label={ariaLabel}>
      {Array.from({ length }, (_, i) => (
        <input
          key={i}
          ref={(el) => {
            refs.current[i] = el
          }}
          className={`h-12 min-w-0 flex-1 rounded-lg border bg-surface text-center text-xl font-bold text-ink outline-none transition focus:border-brand-600 focus:ring-2 focus:ring-brand-600/20 disabled:opacity-60 ${invalid ? 'border-red-500' : 'border-brand-200'}`}
          inputMode="numeric"
          autoComplete="one-time-code"
          autoFocus={autoFocus && i === 0}
          maxLength={length}
          value={digits[i] ?? ''}
          disabled={disabled}
          aria-label={`${ariaLabel} ${i + 1}`}
          aria-invalid={invalid}
          onChange={(e) => onInput(i, e.target.value)}
          onKeyDown={(e) => onKeyDown(i, e)}
          onPaste={onPaste}
          onFocus={(e) => e.target.select()}
        />
      ))}
    </div>
  )
}
