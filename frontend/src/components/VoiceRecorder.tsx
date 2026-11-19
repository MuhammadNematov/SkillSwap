import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

const MAX_SEC = 120
const MIN_SEC = 1
const MAX_BYTES = 4 * 1024 * 1024
const BARS = 36

// Запись голосового: .webm (Opus), до 120 секунд и 4 МБ.
// Идёт запись: видны волны звука с микрофона и таймер. «Отмена» сбрасывает запись, «Отправить» останавливает и сразу отправляет.
export default function VoiceRecorder({
  disabled,
  onDone,
  onError,
  onRecordingChange,
}: {
  disabled?: boolean
  onDone: (blob: Blob, sec: number) => void
  onError: (message: string) => void
  onRecordingChange?: (v: boolean) => void
}) {
  const { t } = useTranslation()
  const [phase, setPhase] = useState<'idle' | 'recording'>('idle')
  const [sec, setSec] = useState(0)
  const [quiet, setQuiet] = useState(false)

  const rec = useRef<MediaRecorder | null>(null)
  const chunks = useRef<Blob[]>([])
  const stream = useRef<MediaStream | null>(null)
  const startedAt = useRef(0)
  const cancelled = useRef(false)
  const tick = useRef<ReturnType<typeof setInterval> | null>(null)
  const raf = useRef<number | null>(null)
  const ctx = useRef<AudioContext | null>(null)
  const canvas = useRef<HTMLCanvasElement | null>(null)
  const peak = useRef(0)

  useEffect(() => onRecordingChange?.(phase !== 'idle'), [phase]) // eslint-disable-line react-hooks/exhaustive-deps

  const stopMeters = () => {
    if (tick.current) clearInterval(tick.current)
    tick.current = null
    if (raf.current) cancelAnimationFrame(raf.current)
    raf.current = null
    void ctx.current?.close().catch(() => undefined)
    ctx.current = null
    stream.current?.getTracks().forEach((tr) => tr.stop())
    stream.current = null
  }
  useEffect(
    () => () => {
      cancelled.current = true
      if (rec.current?.state === 'recording') rec.current.stop()
      stopMeters()
    },
    [],
  )

  const supported = typeof MediaRecorder !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
  if (!supported) return null

  // Волны: последние BARS значений громкости, самый новый справа
  function startWaves(s: MediaStream) {
    const AC = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ac = new AC()
    ctx.current = ac
    const analyser = ac.createAnalyser()
    analyser.fftSize = 512
    ac.createMediaStreamSource(s).connect(analyser)
    const data = new Uint8Array(analyser.fftSize)
    const levels: number[] = Array(BARS).fill(0)
    let last = 0
    const draw = (now: number) => {
      raf.current = requestAnimationFrame(draw)
      if (now - last < 60) return
      last = now
      analyser.getByteTimeDomainData(data)
      let max = 0
      for (const v of data) max = Math.max(max, Math.abs(v - 128) / 128)
      peak.current = Math.max(peak.current, max)
      levels.push(max)
      levels.shift()
      const c = canvas.current
      const g = c?.getContext('2d')
      if (!c || !g) return
      g.clearRect(0, 0, c.width, c.height)
      g.fillStyle = '#7C5CF5'
      const w = c.width / BARS
      levels.forEach((l, i) => {
        const h = Math.max(3, Math.min(c.height, l * 2.2 * c.height))
        g.fillRect(i * w + 1, (c.height - h) / 2, w - 2, h)
      })
    }
    raf.current = requestAnimationFrame(draw)
  }

  async function start() {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: true })
      stream.current = s
      chunks.current = []
      cancelled.current = false
      peak.current = 0
      setQuiet(false)
      const r = new MediaRecorder(s, { mimeType: 'audio/webm;codecs=opus' })
      r.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
      r.onstop = () => {
        const elapsed = (Date.now() - startedAt.current) / 1000
        const blob = new Blob(chunks.current, { type: 'audio/webm' })
        stopMeters()
        if (cancelled.current) return setPhase('idle')
        if (elapsed < MIN_SEC) {
          setPhase('idle')
          return onError(t('chat.voiceTooShort'))
        }
        if (blob.size > MAX_BYTES) {
          setPhase('idle')
          return onError(t('chat.voiceTooLarge'))
        }
        setPhase('idle')
        onDone(blob, Math.min(MAX_SEC, elapsed))
      }
      rec.current = r
      startedAt.current = Date.now()
      r.start()
      startWaves(s)
      setSec(0)
      setPhase('recording')
      tick.current = setInterval(() => {
        const e = (Date.now() - startedAt.current) / 1000
        setSec(e)
        // через 2 секунды тишины подсказываем про микрофон
        if (e > 2 && peak.current < 0.03) setQuiet(true)
        else setQuiet(false)
        if (e >= MAX_SEC && r.state === 'recording') r.stop()
      }, 250)
    } catch {
      stopMeters()
      setPhase('idle')
      onError(t('chat.micDenied'))
    }
  }

  function stop(cancel: boolean) {
    cancelled.current = cancel
    if (rec.current?.state === 'recording') rec.current.stop()
    else {
      stopMeters()
      setPhase('idle')
    }
  }

  const mmss = (v: number) => `${Math.floor(v / 60)}:${String(Math.floor(v) % 60).padStart(2, '0')}`

  if (phase === 'recording') {
    return (
      <div className="grid flex-1 grid-cols-[1fr_auto_1fr] items-center gap-3">
        <div className="flex min-w-0 items-center gap-3">
          <span className="h-2.5 w-2.5 shrink-0 animate-pulse rounded-full bg-red-500" />
          <span className="shrink-0 text-sm tabular-nums text-slate-700">{mmss(sec)}</span>
          <canvas ref={canvas} width={216} height={32} className="h-8 w-[216px] min-w-0 max-w-full" aria-hidden />
          {quiet && <span className="hidden text-xs text-amber-700 md:inline">{t('chat.noSound')}</span>}
        </div>
        <button type="button" className="btn-secondary px-5 py-2" onClick={() => stop(true)}>
          {t('chat.cancelRecord')}
        </button>
        <div className="flex justify-end">
          <button type="button" className="btn-primary px-5 py-2" onClick={() => stop(false)}>
            {t('chat.send')}
          </button>
        </div>
      </div>
    )
  }

  return (
    <button type="button" disabled={disabled} className="rounded-lg p-2 text-slate-500 hover:bg-brand-50" aria-label={t('chat.record')} title={t('chat.record')} onClick={start}>
      <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <rect x="9" y="3" width="6" height="12" rx="3" />
        <path d="M5 11a7 7 0 0 0 14 0M12 18v3" />
      </svg>
    </button>
  )
}
