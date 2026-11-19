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

  // Ссылка на файл действует всего 5 минут, поэтому для каждого нового прослушивания просим свежую.
  // Пауза и продолжение работают с уже созданным плеером.
  async function toggle() {
    setError(null)
    if (playing) {
      audio.current?.pause()
      stopTimer()
      setPlaying(false)
      return
    }
    const el0 = audio.current
    if (el0 && !el0.ended && el0.currentTime > 0) {
      try {
        await el0.play()
        setPlaying(true)
        return
      } catch {
        audio.current = null
      }
    }
    try {
      audio.current = null
      const a = attachmentId ? await getAttachmentUrl(attachmentId) : null
      if (a) {
        const el = new Audio()
        el.preload = 'auto'
        el.ontimeupdate = () => setPos(el.currentTime)
        el.onerror = () => {
          setPlaying(false)
          audio.current = null
          const code = el.error ? ` (код ${el.error.code})` : ''
          setError(`${t('chat.voiceFailed')}${code}`)
        }
        el.onended = () => {
          setPlaying(false)
          setPos(0)
          audio.current = null
        }
        el.src = a.url
        audio.current = el
        await el.play()
      } else {
        // заглушка: файла нет, просто крутим таймер
        const started = Date.now()
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
      audio.current = null
      setPlaying(false)
      const detail = e instanceof DOMException ? ` (${e.name})` : ''
      setError(e instanceof DOMException ? `${t('chat.voiceFailed')}${detail}` : errorMessage(t, e))
    }
  }

  // Скачать файл на компьютер: ссылка подписанная и короткая, запрашиваем её в момент нажатия
  async function download() {
    setError(null)
    try {
      const a = attachmentId ? await getAttachmentUrl(attachmentId) : null
      if (!a) return
      const link = document.createElement('a')
      link.href = a.url
      link.download = a.fileName || 'voice.webm'
      link.rel = 'noopener'
      document.body.appendChild(link)
      link.click()
      link.remove()
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
      {attachmentId && (
        <button type="button" onClick={download} aria-label={t('chat.downloadVoice')} title={t('chat.downloadVoice')} className="shrink-0 opacity-80 hover:opacity-100">
          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 4v11m0 0-4-4m4 4 4-4M5 20h14" />
          </svg>
        </button>
      )}
      <span className="text-xs tabular-nums opacity-80">{fmt(playing || pos ? pos : durationSec)}</span>
    </div>
  )
}
