import type { ReactNode } from 'react'
import { useMemo } from 'react'

// Сцена лендинга: слева парень, справа девушка (3D, без фона), посередине содержимое.
// При links знания летают между ними по знаку ∞ за содержимым. Только SVG/CSS, без библиотек.

const TOKENS = ['React', 'UI/UX', 'English', 'Python', 'Figma', 'Math']

// Лемниската Бернулли: точки знака бесконечности
function infinityPath(w: number, h: number): string {
  const a = w * 0.49
  const pts: string[] = []
  for (let i = 0; i <= 140; i++) {
    const t = (i / 140) * Math.PI * 2
    const d = 1 + Math.sin(t) ** 2
    pts.push(`${i === 0 ? 'M' : 'L'}${(w / 2 + (a * Math.cos(t)) / d).toFixed(1)} ${(h / 2 + (a * Math.sin(t) * Math.cos(t)) / d).toFixed(1)}`)
  }
  return pts.join(' ') + 'Z'
}

function Infinity() {
  const W = 1200
  const H = 420
  const d = useMemo(() => infinityPath(W, H), [])
  const still = typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      className="pointer-events-none absolute left-1/2 top-[44%] hidden w-[135vw] max-w-none -translate-x-1/2 -translate-y-1/2 overflow-visible lg:block"
      aria-hidden
    >
      <defs>
        <linearGradient id="inf-g" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="#6841E8" />
          <stop offset=".5" stopColor="#B9A8F5" />
          <stop offset="1" stopColor="#6841E8" />
        </linearGradient>
        <filter id="inf-glow" x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="8" />
        </filter>
      </defs>
      <path d={d} fill="none" stroke="url(#inf-g)" strokeWidth="4" strokeLinecap="round" strokeDasharray="2 14" style={{ animation: 'swap-dash 2.2s linear infinite' }} />
      <path id="inf-path" d={d} fill="none" stroke="none" />
      {!still &&
        TOKENS.map((tk, i) => (
          <g key={tk}>
            <animateMotion dur="14s" begin={`${-i * (14 / TOKENS.length)}s`} repeatCount="indefinite" rotate="0">
              <mpath href="#inf-path" />
            </animateMotion>
            <rect x="-38" y="-15" width="76" height="30" rx="15" style={{ fill: 'rgb(var(--surface))', stroke: 'rgb(var(--brand-200))' }} />
            <text y="5" textAnchor="middle" fontSize="15" fontWeight="700" fill={i % 2 ? '#12A150' : '#5A33DA'} fontFamily="Inter, system-ui, sans-serif">
              {tk}
            </text>
          </g>
        ))}
    </svg>
  )
}

function Character({ side }: { side: 'left' | 'right' }) {
  const left = side === 'left'
  return (
    <div
      className={`pointer-events-none relative z-30 hidden self-end lg:block ${
        left ? 'w-[clamp(170px,18vw,270px)] justify-self-end -mr-6' : 'w-[clamp(200px,21vw,310px)] justify-self-start -ml-6'
      }`}
      style={{ animation: `swap-float 6s ease-in-out ${left ? '0s' : '-3s'} infinite` }}
    >
      <img src={left ? '/characters/boy.webp' : '/characters/girl.webp'} alt="" className="block w-full drop-shadow-[0_24px_30px_rgba(23,20,61,.28)]" draggable={false} />
    </div>
  )
}

// Центральная колонка в обрамлении двух персонажей
export default function SwapStage({ children, centerWidth = 540, className = '', links = true }: { children: ReactNode; centerWidth?: number; className?: string; links?: boolean }) {
  return (
    <main
      className={`relative flex flex-1 items-center justify-center overflow-hidden lg:grid lg:[grid-template-columns:1fr_minmax(0,var(--cw))_1fr] lg:items-end ${className}`}
      style={{ ['--cw' as string]: `${centerWidth}px` }}
    >
      {links && <Infinity />}
      <Character side="left" />
      <div className="relative z-20 flex w-full self-stretch lg:self-center" style={{ maxWidth: centerWidth }}>
        {children}
      </div>
      <Character side="right" />
    </main>
  )
}
