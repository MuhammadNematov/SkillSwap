import { useEffect, useRef, useState, type CSSProperties, type MouseEvent, type RefObject } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import Icon, { type IconName } from './Icon'
import './HeroBook.css'

// Пары навыков, которые по кругу меняются на левой странице книги
const PAIRS: [string, string][] = [
  ['C#', 'English'],
  ['Python', 'Figma'],
  ['JavaScript', 'Deutsch'],
  ['Photoshop', 'Español'],
]

const STEPS: { key: 's1' | 's2' | 's3'; icon: IconName }[] = [
  { key: 's1', icon: 'user' },
  { key: 's2', icon: 'search' },
  { key: 's3', icon: 'video' },
]

// Навыки, которые летают вокруг книги
const CHIPS = [
  { label: 'English', style: { left: 30, top: -70 }, d: '5s', delay: '.2s' },
  { label: 'C#', style: { right: 40, top: -60 }, d: '6s', delay: '.4s' },
  { label: 'Figma', style: { left: -10, bottom: -50 }, d: '5.5s', delay: '.6s' },
  { label: 'Python', style: { right: -10, bottom: -40 }, d: '4.6s', delay: '.8s' },
]

// Пыль-частицы на фоне hero
function useParticles(ref: RefObject<HTMLCanvasElement>) {
  useEffect(() => {
    const canvas = ref.current
    if (!canvas || matchMedia('(prefers-reduced-motion: reduce)').matches) return
    const ctx = canvas.getContext('2d')!
    const dpr = window.devicePixelRatio || 1
    let w = 0
    let h = 0
    let raf = 0
    let dots: { x: number; y: number; z: number; a: number }[] = []

    const resize = () => {
      w = canvas.width = canvas.offsetWidth * dpr
      h = canvas.height = canvas.offsetHeight * dpr
      dots = Array.from({ length: Math.min(90, Math.round(w / 16)) }, () => ({
        x: Math.random() * w,
        y: Math.random() * h,
        z: Math.random() * 2 + 0.4,
        a: Math.random() * Math.PI * 2,
      }))
    }
    const tick = () => {
      ctx.clearRect(0, 0, w, h)
      for (const p of dots) {
        p.y -= p.z * 0.3
        p.a += 0.02
        p.x += Math.sin(p.a) * 0.3
        if (p.y < 0) {
          p.y = h
          p.x = Math.random() * w
        }
        ctx.beginPath()
        ctx.arc(p.x, p.y, p.z * 1.4 * dpr, 0, Math.PI * 2)
        ctx.fillStyle = `rgba(139, 92, 246, ${0.15 + Math.sin(p.a) * 0.1 + p.z * 0.1})`
        ctx.fill()
      }
      raf = requestAnimationFrame(tick)
    }
    const ro = new ResizeObserver(resize)
    ro.observe(canvas)
    resize()
    tick()
    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
    }
  }, [ref])
}

export function HeroParticles() {
  const ref = useRef<HTMLCanvasElement>(null)
  useParticles(ref)
  return <canvas ref={ref} className="hb-particles" aria-hidden />
}

export default function HeroBook() {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [go, setGo] = useState(false)
  const [pair, setPair] = useState(0)
  const [fade, setFade] = useState(false)
  const sceneRef = useRef<HTMLDivElement>(null)

  // Таймлайн: прилёт → открытие обложки → появление контента
  useEffect(() => {
    const a = setTimeout(() => setOpen(true), 3000)
    const b = setTimeout(() => setGo(true), 4300)
    return () => {
      clearTimeout(a)
      clearTimeout(b)
    }
  }, [])

  // Смена пар навыков
  useEffect(() => {
    let inner: ReturnType<typeof setTimeout>
    const id = setInterval(() => {
      setFade(true)
      inner = setTimeout(() => {
        setPair((p) => (p + 1) % PAIRS.length)
        setFade(false)
      }, 400)
    }, 2800)
    return () => {
      clearInterval(id)
      clearTimeout(inner)
    }
  }, [])

  // Лёгкий наклон за мышкой
  const onMove = (e: MouseEvent<HTMLDivElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    const dx = (e.clientX - r.left) / r.width - 0.5
    const dy = (e.clientY - r.top) / r.height - 0.5
    sceneRef.current?.style.setProperty('--ry', `${dx * 12}deg`)
    sceneRef.current?.style.setProperty('--rx', `${-dy * 8}deg`)
  }
  const onLeave = () => {
    sceneRef.current?.style.setProperty('--ry', '0deg')
    sceneRef.current?.style.setProperty('--rx', '0deg')
  }

  const [skillA, skillB] = PAIRS[pair]
  const state = `${open ? ' hb-open' : ''}${go ? ' hb-go' : ''}`

  return (
    <div className="hb-wrap" onMouseMove={onMove} onMouseLeave={onLeave}>
      <div ref={sceneRef} className={`hb-scene${go ? ' hb-go' : ''}`}>
        <div className="hb-shadow" />

        <div className="hb-float">
          <div className={`hb-book${state}`}>
            <div className="hb-glow" />
            <div className="hb-backcover-left" />
            <div className="hb-backcover" />
            <div className="hb-spine" />
            <div className="hb-block" />

            {/* Правая страница: как это работает */}
            <div className="hb-right">
              <div className="hb-paper">
                <div className="hb-content hb-reveal">
                  <div className="hb-kicker">{t('landing.how.title')}</div>
                  <ul className="hb-steps">
                    {STEPS.map((s, i) => (
                      <li key={s.key}>
                        <i>
                          <Icon name={s.icon} className="h-4 w-4" />
                        </i>
                        <div>
                          <b>
                            {i + 1}. {t(`landing.how.${s.key}.title`)}
                          </b>
                          <span>{t(`landing.how.${s.key}.text`)}</span>
                        </div>
                      </li>
                    ))}
                  </ul>
                  <Link to="/register" className="hb-btn">
                    {t('landing.cta.start')}
                    <Icon name="arrowRight" className="h-4 w-4" />
                  </Link>
                </div>
                <div className="hb-pagenum">2</div>
              </div>
            </div>

            {/* Листающиеся страницы */}
            {['0s', '.35s', '.7s'].map((delay) => (
              <div key={delay} className="hb-flip" style={{ animationDelay: delay }}>
                <div className="hb-paper">
                  <div className="hb-lines" />
                </div>
                <div className="hb-paper hb-paper--back">
                  <div className="hb-lines" />
                </div>
              </div>
            ))}

            {/* Обложка */}
            <div className="hb-cover">
              <div className="hb-cover-front">
                <div className="hb-badge">
                  <img src="/logo-icon.png" alt="" />
                </div>
                <div className="hb-cover-title">{t('common.appName')}</div>
                <div className="hb-cover-sub">{t('landing.subtitle')}</div>
              </div>

              {/* Внутренняя сторона обложки = левая страница: обмен навыками */}
              <div className="hb-cover-back">
                <div className="hb-paper hb-paper--left">
                  <div className={`hb-content hb-reveal${fade ? ' hb-fade' : ''}`}>
                    <div className="hb-kicker">{t('common.appName')}</div>
                    <div className="hb-person">
                      <span className="hb-letter">A</span>
                      <div className="hb-skills">
                        <div className="hb-row">
                          {t('landing.hero.teach')}
                          <br />
                          <span className="hb-tag hb-tag--teach">{skillA}</span>
                        </div>
                        <div className="hb-row">
                          {t('landing.hero.learn')}
                          <br />
                          <span className="hb-tag hb-tag--learn">{skillB}</span>
                        </div>
                      </div>
                    </div>
                    <span className="hb-swapicon">
                      <Icon name="swap" className="h-5 w-5" />
                    </span>
                    <div className="hb-person">
                      <span className="hb-letter">S</span>
                      <div className="hb-skills">
                        <div className="hb-row">
                          {t('landing.hero.teach')}
                          <br />
                          <span className="hb-tag hb-tag--teach">{skillB}</span>
                        </div>
                        <div className="hb-row">
                          {t('landing.hero.learn')}
                          <br />
                          <span className="hb-tag hb-tag--learn">{skillA}</span>
                        </div>
                      </div>
                    </div>
                  </div>
                  <div className="hb-pagenum">1</div>
                </div>
              </div>
            </div>
          </div>
        </div>

        {CHIPS.map((c) => (
          <span
            key={c.label}
            className="hb-chip"
            style={{ ...c.style, '--d': c.d, animationDelay: `${c.delay}, 0s` } as unknown as CSSProperties}
          >
            {c.label}
          </span>
        ))}
      </div>
    </div>
  )
}
