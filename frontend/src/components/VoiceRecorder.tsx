import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

const MAX_SEC = 120
const MIN_SEC = 1
const MAX_BYTES = 4 * 1024 * 1024

// Запись голосового: .webm (Opus), до 120 секунд и 4 МБ. Кнопка-микрофон → идёт запись → «отправить» или «отмена».
export default function VoiceRecorder({ disabled, onDone, onError, onRecordingChange }: { disabled?: boolean; onRecordingChange?: (v: boolean) => void; onDone: (blob: Blob, sec: number) => void; onError: (message: string) => void }) {
  const { t } = useTranslation()
  const [recording, setRecording] = useState(false)
  const [sec, setSec] = useState(0)
  const rec = useRef<MediaRecorder | null>(null)
  const chunks = useRef<Blob[]>([])
  const stream = useRef<MediaStream | null>(null)
  const startedAt = useRef(0)
  const cancelled = useRef(false)
  const tick = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => onRecordingChange?.(recording), [recording]) // eslint-disable-line react-hooks/exhaustive-deps

  const cleanup = () => {
    if (tick.current) clearInterval(tick.current)
    tick.current = null
    stream.current?.getTracks().forEach((tr) => tr.stop())
    stream.current = null
    setRecording(false)
    setSec(0)
  }
  useEffect(
    () => () => {
      cancelled.current = true
      if (rec.current?.state === 'recording') rec.current.stop()
      cleanup()
    },
    [],
  )

  const supported = typeof MediaRecorder !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
  if (!supported) return null

  async function start() {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: true })
      stream.current = s
      chunks.current = []
      cancelled.current = false
      const r = new MediaRecorder(s, { mimeType: 'audio/webm;codecs=opus' })
      r.ondataavailable = (e) => e.data.size > 0 && chunks.current.push(e.data)
      r.onstop = () => {
        const elapsed = (Date.now() - startedAt.current) / 1000
        const blob = new Blob(chunks.current, { type: 'audio/webm' })
        cleanup()
        if (cancelled.current) return
        if (elapsed < MIN_SEC) return onError(t('chat.voiceTooShort'))
        if (blob.size > MAX_BYTES) return onError(t('chat.voiceTooLarge'))
        onDone(blob, Math.min(MAX_SEC, elapsed))
      }
      rec.current = r
      startedAt.current = Date.now()
      r.start()
      setRecording(true)
      tick.current = setInterval(() => {
        const e = (Date.now() - startedAt.current) / 1000
        setSec(e)
        if (e >= MAX_SEC && r.state === 'recording') r.stop()
      }, 250)
    } catch {
      cleanup()
      onError(t('chat.micDenied'))
    }
  }

  function stop(cancel: boolean) {
    cancelled.current = cancel
    if (rec.current?.state === 'recording') rec.current.stop()
    else cleanup()
  }

  if (recording) {
    return (
      <div className="flex flex-1 items-center gap-3">
        <span className="h-2.5 w-2.5 animate-pulse rounded-full bg-red-500" />
        <span className="text-sm tabular-nums text-slate-700">
          {Math.floor(sec / 60)}:{String(Math.floor(sec) % 60).padStart(2, '0')} / 2:00
        </span>
        <button type="button" className="ml-auto text-sm text-slate-500 hover:underline" onClick={() => stop(true)}>
          {t('chat.cancelRecord')}
        </button>
        <button type="button" className="btn-primary px-4 py-2" onClick={() => stop(false)}>
          {t('chat.send')}
        </button>
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
