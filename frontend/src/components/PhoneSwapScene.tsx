import { useMemo } from 'react'

// Сцена входа и регистрации: два телефона с видеозвонком, между ними по знаку ∞ бесконечно летают навыки. Только SVG/CSS, без библиотек.

const TOKENS_A = ['React', 'UI/UX', 'English', 'Python', 'Figma', 'Math', 'C#', 'SQL']
const TOKENS_B = ['Design', 'SMM', 'Git', 'Русский', 'Excel', 'Тоҷикӣ']
const DOTS = 14

// Лемниската Бернулли: точки знака бесконечности (k — вертикальная «пухлость»)
function infinityPath(w: number, h: number, a: number, k: number): string {
  const pts: string[] = []
  for (let i = 0; i <= 140; i++) {
    const t = (i / 140) * Math.PI * 2
    const d = 1 + Math.sin(t) ** 2
    pts.push(`${i === 0 ? 'M' : 'L'}${(w / 2 + (a * Math.cos(t)) / d).toFixed(1)} ${(h / 2 + (k * a * Math.sin(t) * Math.cos(t)) / d).toFixed(1)}`)
  }
  return pts.join(' ') + 'Z'
}

function Token({ label, path, dur, begin, reverse, tone }: { label: string; path: string; dur: number; begin: number; reverse?: boolean; tone: string }) {
  return (
    <g>
      <animateMotion
        dur={`${dur}s`}
        begin={`${begin}s`}
        repeatCount="indefinite"
        rotate="0"
        {...(reverse ? { keyPoints: '1;0', keyTimes: '0;1', calcMode: 'linear' } : {})}
      >
        <mpath href={`#${path}`} />
      </animateMotion>
      <rect x="-31" y="-12" width="62" height="24" rx="12" style={{ fill: 'rgb(var(--surface))', stroke: 'rgb(var(--brand-200))' }} />
      <text y="4.5" textAnchor="middle" fontSize="12" fontWeight="700" fill={tone} fontFamily="Inter, system-ui, sans-serif">
        {label}
      </text>
    </g>
  )
}

function Phone({ src, name, side, tilt }: { src: string; name: string; side: 'left' | 'right'; tilt: number }) {
  return (
    <div
      className="absolute top-1/2 z-10 aspect-[9/17] w-[min(22cqw,49cqh)]"
      style={{ left: side === 'left' ? '25%' : '75%', transform: `translate(-50%, -50%) rotate(${tilt}deg)` }}
    >
      <div className="relative h-full w-full overflow-hidden rounded-[22px] border-[5px] border-[#17143d] bg-[#17143d] shadow-md">
        <img src={src} alt="" className="h-full w-full object-cover" draggable={false} />
        <span className="absolute left-1/2 top-1.5 h-1.5 w-10 -translate-x-1/2 rounded-full bg-[#17143d]" />
        <span className="absolute left-2 top-5 flex items-center gap-1 rounded-full bg-black/45 px-2 py-0.5 text-[9px] font-bold text-white">
          <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-[#12A150]" />
          LIVE
        </span>
        <span className="absolute inset-x-2 bottom-2 rounded-full bg-black/45 px-2 py-1 text-center text-[10px] font-semibold text-white">{name}</span>
      </div>
    </div>
  )
}

export default function PhoneSwapScene() {
  const W = 600
  const H = 300
  const dA = useMemo(() => infinityPath(W, H, W * 0.49, 1), [])
  const dB = useMemo(() => infinityPath(W, H, W * 0.44, 1.55), [])
  const still = typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

  return (
    <div className="relative mx-auto h-[min(300px,34vh)] w-full max-w-[640px] select-none [container-type:size]">
      <svg viewBox={`0 0 ${W} ${H}`} className="absolute left-0 top-1/2 h-auto w-full -translate-y-1/2 overflow-visible" aria-hidden>
        <defs>
          <linearGradient id="inf-g" x1="0" y1="0" x2="1" y2="0">
            <stop offset="0" stopColor="#6841E8" />
            <stop offset=".5" stopColor="#B9A8F5" />
            <stop offset="1" stopColor="#6841E8" />
          </linearGradient>
          <filter id="inf-glow" x="-20%" y="-20%" width="140%" height="140%">
            <feGaussianBlur stdDeviation="6" />
          </filter>
        </defs>
        {/* две линии бесконечности: основная и вторая, шире по вертикали */}
        <path d={dA} fill="none" stroke="url(#inf-g)" strokeWidth="3.5" strokeLinecap="round" strokeDasharray="2 12" style={{ animation: 'swap-dash 2.2s linear infinite' }} />
        <path d={dB} fill="none" stroke="#12A150" strokeOpacity=".55" strokeWidth="2.5" strokeLinecap="round" strokeDasharray="1 10" style={{ animation: 'swap-dash 3s linear infinite reverse' }} />
        <path id="inf-a" d={dA} fill="none" stroke="none" />
        <path id="inf-b" d={dB} fill="none" stroke="none" />
        {!still && (
          <>
            {Array.from({ length: DOTS }, (_, i) => (
              <circle key={i} r={i % 3 === 0 ? 4 : 3} fill={i % 2 ? '#12A150' : '#8B6CF6'}>
                <animateMotion dur={`${9 + (i % 4) * 2}s`} begin={`${-i * 1.1}s`} repeatCount="indefinite" rotate="0">
                  <mpath href={i % 2 ? '#inf-b' : '#inf-a'} />
                </animateMotion>
              </circle>
            ))}
            {TOKENS_A.map((tk, i) => (
              <Token key={tk} label={tk} path="inf-a" dur={16} begin={-i * (16 / TOKENS_A.length)} tone="#5A33DA" />
            ))}
            {TOKENS_B.map((tk, i) => (
              <Token key={tk} label={tk} path="inf-b" dur={19} begin={-i * (19 / TOKENS_B.length)} reverse tone="#12A150" />
            ))}
          </>
        )}
      </svg>

      <Phone src="/characters/boy-phone.webp" name="Alex" side="left" tilt={-5} />
      <Phone src="/characters/girl-phone.webp" name="Mila" side="right" tilt={5} />
    </div>
  )
}
