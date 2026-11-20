import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

export interface JoinPrefs {
  video: boolean
  audio: boolean
}

type Problem = 'insecure' | 'denied' | 'notFound' | 'busy' | 'other' | null

function problemOf(e: unknown): Problem {
  const name = (e as { name?: string } | null)?.name
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'denied'
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'notFound'
  if (name === 'NotReadableError' || name === 'AbortError') return 'busy'
  return 'other'
}

// Проверка камеры и микрофона перед входом в комнату: превью, переключатели, понятные причины отказа.
// Войти можно и без устройств (например, только слушать), поэтому кнопка входа доступна всегда.
export default function PreJoin({ busy, onJoin }: { busy: boolean; onJoin: (p: JoinPrefs) => void }) {
  const { t } = useTranslation()
  const videoRef = useRef<HTMLVideoElement>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const [cam, setCam] = useState(true)
  const [mic, setMic] = useState(true)
  const [problem, setProblem] = useState<Problem>(null)
  const [level, setLevel] = useState(0)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    let cancelled = false
    let raf = 0
    let ctx: AudioContext | null = null

    const stop = () => {
      cancelAnimationFrame(raf)
      streamRef.current?.getTracks().forEach((tr) => tr.stop())
      streamRef.current = null
      void ctx?.close()
      ctx = null
    }

    async function start() {
      setProblem(null)
      setReady(false)
      setLevel(0)
      if (!cam && !mic) return
      if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        setProblem('insecure')
        return
      }
      try {
        const stream = await navigator.mediaDevices.getUserMedia({ video: cam, audio: mic })
        if (cancelled) {
          stream.getTracks().forEach((tr) => tr.stop())
          return
        }
        streamRef.current = stream
        setReady(true)
        if (videoRef.current) videoRef.current.srcObject = stream
        if (mic && stream.getAudioTracks().length > 0) {
          ctx = new AudioContext()
          const analyser = ctx.createAnalyser()
          analyser.fftSize = 256
          ctx.createMediaStreamSource(stream).connect(analyser)
          const data = new Uint8Array(analyser.frequencyBinCount)
          const tick = () => {
            analyser.getByteFrequencyData(data)
            setLevel(Math.min(1, data.reduce((a, b) => a + b, 0) / data.length / 80))
            raf = requestAnimationFrame(tick)
          }
          tick()
        }
      } catch (e) {
        if (!cancelled) setProblem(problemOf(e))
      }
    }

    void start()
    return () => {
      cancelled = true
      stop()
    }
  }, [cam, mic])

  const join = () => {
    // устройства отпускаем до входа: комнату откроет LiveKit
    streamRef.current?.getTracks().forEach((tr) => tr.stop())
    onJoin({ video: cam && problem === null, audio: mic && problem === null })
  }

  return (
    <div className="card mx-auto w-full max-w-xl space-y-4">
      <h2 className="text-lg font-bold text-ink">{t('call.prejoin.title')}</h2>

      <div className="relative aspect-video overflow-hidden rounded-2xl bg-[#17143d]">
        {cam && ready ? (
          <video ref={videoRef} autoPlay playsInline muted className="h-full w-full -scale-x-100 object-cover" />
        ) : (
          <div className="flex h-full items-center justify-center px-4 text-center text-sm text-white/70">
            {cam ? t('call.prejoin.starting') : t('call.camOff')}
          </div>
        )}
      </div>

      {mic && ready && (
        <div>
          <div className="mb-1 text-xs text-ink-soft">{t('call.prejoin.micLevel')}</div>
          <div className="h-2 overflow-hidden rounded-full bg-brand-100">
            <div className="h-full rounded-full bg-brand-600 transition-[width] duration-100" style={{ width: `${Math.round(level * 100)}%` }} />
          </div>
        </div>
      )}

      {problem && (
        <p className="rounded-2xl bg-amber-50 px-4 py-3 text-sm text-amber-800">{t(`call.prejoin.problem.${problem}`)}</p>
      )}

      <div className="flex flex-wrap gap-3">
        <button className={cam ? 'btn-secondary' : 'btn-primary'} onClick={() => setCam((v) => !v)}>
          {cam ? t('call.camOn') : t('call.camOff')}
        </button>
        <button className={mic ? 'btn-secondary' : 'btn-primary'} onClick={() => setMic((v) => !v)}>
          {mic ? t('call.micOn') : t('call.micOff')}
        </button>
      </div>

      <button className="btn-primary w-full" disabled={busy} onClick={join}>
        {busy ? t('common.loading') : t('call.prejoin.enter')}
      </button>
    </div>
  )
}
