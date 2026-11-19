import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getAttachmentUrl } from '../api/attachments'
import { errorMessage } from '../api/errors'

const fmt = (s: number) => `${Math.floor(s / 60)}:${String(Math.round(s) % 60).padStart(2, '0')}`

// Голосовое сообщение: ссылку на файл запрашиваем только при нажатии «воспроизвести» (она временная).
// В режиме моков файла нет, поэтому просто крутим таймер.
export default function VoiceBubble({ attachmentId, durationSec = 0, mine }: { attachmentId?: string; durationSec?: number; mine: boolean }) {
  const { t } = useTranslation()
  const audio = useRef<HTMLAudioElement | null>(null)
  const timer = useRef<ReturnType<typeof setInterval> | null>(null)
  const [playing, setPlaying] = useState(false)
  const [pos, setPos] = useState(0)
  const [error, setError] = useState<string | null>(null)

  const stopTimer = () => {
    if (timer.current) clearInterval(timer.current)
    timer.current = null
  }
  useEffect(
    () => () => {
      stopTimer()
      audio.current?.pause()
    },
    [],
  )

  async function toggle() {
    setError(null)
    if (playing) {
      audio.current?.pause()
      stopTimer()
      setPlaying(false)
      return
    }
    try {
      if (!audio.current && attachmentId) {
        const a = await getAttachmentUrl(attachmentId)
        if (a) {
          const el = new Audio(a.url)
          el.ontimeupdate = () => setPos(el.currentTime)
          el.onended = () => {
            setPlaying(false)
            setPos(0)
          }
          audio.current = el
        }
      }
      if (audio.current) {
        await audio.current.play()
      } else {
        // заглушка
        const started = Date.now() - pos * 1000
        timer.current = setInterval(() => {
          const p = (Date.now() - started) / 1000
          if (p >= durationSec) {
            stopTimer()
            setPlaying(false)
            setPos(0)
          } else setPos(p)
        }, 200)
      }
      setPlaying(true)
    } catch (e) {
      setError(errorMessage(t, e))
    }
  }

  const pct = durationSec ? Math.min(100, (pos / durationSec) * 100) : 0
  return (
    <div className={`flex w-60 max-w-full items-center gap-3 rounded-2xl px-3 py-2.5 text-sm ${mine ? 'bg-brand-600 text-white' : 'bg-surface text-slate-800'}`}>
      <button
        type="button"
        onClick={toggle}
        aria-label={playing ? t('chat.pause') : t('chat.play')}
        className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-full ${mine ? 'bg-white/20' : 'bg-brand-100 text-brand-700'}`}
      >
        <svg viewBox="0 0 24 24" className="h-4 w-4" fill="currentColor">
          {playing ? <path d="M6 5h4v14H6zM14 5h4v14h-4z" /> : <path d="M8 5v14l11-7z" />}
        </svg>
      </button>
      <div className="min-w-0 flex-1">
        <div className={`h-1.5 overflow-hidden rounded-full ${mine ? 'bg-white/30' : 'bg-brand-100'}`}>
          <div className={`h-full ${mine ? 'bg-surface' : 'bg-brand-600'}`} style={{ width: `${pct}%` }} />
        </div>
        {error && <div className="mt-1 text-[11px] opacity-90">{error}</div>}
      </div>
      <span className="text-xs tabular-nums opacity-80">{fmt(playing || pos ? pos : durationSec)}</span>
    </div>
  )
}
