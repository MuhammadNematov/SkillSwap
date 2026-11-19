import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

// Сцена входа и регистрации: двое учатся друг у друга по видеозвонку на телефонах,
// а знания (навыки) бесконечно летают между ними по знаку ∞. Только SVG/CSS, без библиотек.

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
      <rect x="-31" y="-12" width="62" height="24" rx="12" fill="#fff" stroke="#DDD8F0" />
      <text y="4.5" textAnchor="middle" fontSize="12" fontWeight="700" fill={tone} fontFamily="Inter, system-ui, sans-serif">
        {label}
      </text>
    </g>
  )
}

function Phone({ src, name, teach, learn, className, delay }: { src: string; name: string; teach: string; learn: string; className: string; delay: string }) {
  const { t } = useTranslation()
  return (
    <div className={`absolute z-10 w-[28%] ${className}`} style={{ animation: `swap-float 6s ease-in-out ${delay} infinite` }}>
      <div className="relative aspect-[3/4.3] overflow-hidden rounded-[1.7rem] border-[5px] border-ink bg-ink shadow-[0_24px_50px_-18px_rgba(23,20,61,.55)]">
        <img src={src} alt="" className="h-full w-full object-cover" draggable={false} />
        <span className="absolute left-1/2 top-1.5 h-1.5 w-12 -translate-x-1/2 rounded-full bg-ink/90" />
        <span className="absolute left-2.5 top-5 flex items-center gap-1 rounded-full bg-black/45 px-2 py-0.5 text-[10px] font-bold text-white backdrop-blur">
          <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-red-500" />
          LIVE
        </span>
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/65 to-transparent px-3 pb-2.5 pt-8">
          <p className="text-sm font-bold text-white">{name}</p>
        </div>
      </div>
      <div className="mx-auto -mt-4 w-[108%] -translate-x-[4%] space-y-1 rounded-2xl border border-ink-line bg-white/95 px-3 py-2 text-[11px] shadow-card backdrop-blur">
        <p>
          <span className="text-ink-soft">{t('landing.hero.teach')}: </span>
          <b className="text-brand-700">{teach}</b>
        </p>
        <p>
          <span className="text-ink-soft">{t('landing.hero.learn')}: </span>
          <b className="text-[#12A150]">{learn}</b>
        </p>
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
    <div className="relative mx-auto h-[min(400px,44vh)] w-full max-w-[640px] select-none">
      <svg viewBox={`0 0 ${W} ${H}`} className="absolute left-0 top-[22%] h-auto w-full overflow-visible" aria-hidden>
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
        <path d={dA} fill="none" stroke="#6841E8" strokeOpacity=".25" strokeWidth="10" filter="url(#inf-glow)" />
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

      <Phone src="/characters/boy-phone.webp" name="Alex" teach="React" learn="UI/UX" className="left-[3%] top-[6%] -rotate-3" delay="0s" />
      <Phone src="/characters/girl-phone.webp" name="Sara" teach="UI/UX" learn="React" className="right-[3%] top-[18%] rotate-3" delay="-3s" />
    </div>
  )
}
