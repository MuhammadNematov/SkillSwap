import { useEffect, useMemo, useRef } from 'react'
import { useTranslation } from 'react-i18next'

const SYMBOLS = ['</>', 'Aa', 'Σ', '♫', '✎', 'π', '{ }', '∞', '#', '%']
const TONES = [
  'bg-brand-100 text-brand-700',
  'bg-surface text-brand-700 border border-ink-line',
  'bg-[#E5F7EC] text-[#12A150]',
  'bg-brand-600 text-white',
]

// Детерминированный ряд «случайных» чисел: одинаковые стартовые данные при каждой отрисовке
function rnd(seed: number) {
  const x = Math.sin(seed * 9301 + 49297) * 233280
  return x - Math.floor(x)
}

interface Body {
  x: number
  y: number
  vx: number
  vy: number
  turn: number // скорость медленного поворота курса, чтобы путь был плавно «блуждающим»
  w: number
  h: number
}

// Учебные значки и названия предметов бесцельно дрейфуют по всей панели с формой (в том числе позади полей),
// отталкиваясь от краёв. Форма лежит выше, поэтому читаемость не страдает. При reduced motion значки стоят на месте.
// quiet: фон страниц входа. Метки приглушены, движутся медленно и живут только по бокам от формы: по центру маска (SAFE_BAND) их скрывает,
// так что они не заходят на заголовок, поля и подвал. inset задаёт область (без полосы шапки и подвала).
const SAFE_BAND = 'linear-gradient(90deg, #000 0, #000 calc(50% - 236px), transparent calc(50% - 212px), transparent calc(50% + 212px), #000 calc(50% + 236px), #000 100%)'

export default function AuthFloaters({
  count = 36,
  className = 'hidden md:block',
  quiet = false,
  inset = 'inset-0',
}: {
  count?: number
  className?: string
  quiet?: boolean
  inset?: string
}) {
  const { t } = useTranslation()
  const box = useRef<HTMLDivElement>(null)
  const nodes = useRef<(HTMLSpanElement | null)[]>([])

  const items = useMemo(() => {
    const subjects = t('authPromo.floaters', { returnObjects: true }) as unknown
    const labels = [...(Array.isArray(subjects) ? (subjects as string[]) : []), ...SYMBOLS]
    return Array.from({ length: count }, (_, n) => ({
      key: n,
      label: labels[(n * 7) % labels.length],
      tone: quiet ? TONES[Math.floor(rnd(n + 201) * 2)] : TONES[Math.floor(rnd(n + 201) * TONES.length)],
    }))
  }, [t, count])

  useEffect(() => {
    const el = box.current
    if (!el) return
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    let W = el.clientWidth
    let H = el.clientHeight

    const bodies: Body[] = items.map((_, n) => {
      const a = rnd(n + 11) * Math.PI * 2
      const speed = quiet ? 5 + rnd(n + 21) * 8 : 14 + rnd(n + 21) * 26 // px/с
      return {
        x: rnd(n + 31) * Math.max(W - 100, 1),
        y: rnd(n + 41) * Math.max(H - 36, 1),
        vx: Math.cos(a) * speed,
        vy: Math.sin(a) * speed,
        turn: (rnd(n + 51) - 0.5) * 0.5,
        w: 0,
        h: 0,
      }
    })
    const apply = (n: number) => {
      const node = nodes.current[n]
      if (node) node.style.transform = `translate(${bodies[n].x}px, ${bodies[n].y}px)`
    }
    bodies.forEach((b, n) => {
      const node = nodes.current[n]
      if (node) {
        b.w = node.offsetWidth
        b.h = node.offsetHeight
      }
      apply(n)
    })
    if (reduce) return

    const ro = new ResizeObserver(() => {
      W = el.clientWidth
      H = el.clientHeight
    })
    ro.observe(el)

    let last = performance.now()
    let raf = 0
    const tick = (now: number) => {
      const dt = Math.min((now - last) / 1000, 0.05)
      last = now
      bodies.forEach((b, n) => {
        // плавно поворачиваем курс: путь не прямая, а блуждающая кривая
        const c = Math.cos(b.turn * dt)
        const s = Math.sin(b.turn * dt)
        const vx = b.vx * c - b.vy * s
        b.vy = b.vx * s + b.vy * c
        b.vx = vx
        b.x += b.vx * dt
        b.y += b.vy * dt
        if (b.x < 0) {
          b.x = 0
          b.vx = Math.abs(b.vx)
        } else if (b.x > W - b.w) {
          b.x = Math.max(W - b.w, 0)
          b.vx = -Math.abs(b.vx)
        }
        if (b.y < 0) {
          b.y = 0
          b.vy = Math.abs(b.vy)
        } else if (b.y > H - b.h) {
          b.y = Math.max(H - b.h, 0)
          b.vy = -Math.abs(b.vy)
        }
        apply(n)
      })
      raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)
    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
    }
  }, [items, quiet])

  return (
    <div
      ref={box}
      className={`pointer-events-none absolute ${inset} z-0 select-none overflow-hidden ${className}`}
      style={quiet ? { maskImage: SAFE_BAND, WebkitMaskImage: SAFE_BAND } : undefined}
      aria-hidden
    >
      {items.map((i) => {
        const center = rnd(i.key + 61) > 0.5 // часть значков бледнее, чтобы не спорить с формой
        return (
          <span
            key={i.key}
            ref={(node) => {
              nodes.current[i.key] = node
            }}
            className={`absolute left-0 top-0 flex h-9 items-center justify-center whitespace-nowrap rounded-xl px-3 text-xs font-bold shadow-sm will-change-transform ${i.tone} ${quiet ? 'opacity-30' : center ? 'opacity-40' : 'opacity-90'}`}
          >
            {i.label}
          </span>
        )
      })}
    </div>
  )
}
