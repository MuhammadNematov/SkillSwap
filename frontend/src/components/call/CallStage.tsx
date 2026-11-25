import { useChat, useConnectionState, useLocalParticipant, useRemoteParticipants, useTracks } from '@livekit/components-react'
import { ConnectionState, Track } from 'livekit-client'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import ChatPanel from './ChatPanel'
import ChatSheet from './ChatSheet'
import ControlBar from './ControlBar'
import LocalPip, { type Corner } from './LocalPip'
import RemoteView from './RemoteView'
import { WIDE_CALL, useMediaQuery } from './useMediaQuery'

const HIDE_AFTER_MS = 4000

export interface CallChrome {
  timer: string
  overtime: boolean
  warning: string | null
  onComplain?: () => void
}

// Раскладка звонка (как в Meet/Zoom): тонкая полоска сверху, собеседник крупно, своё видео маленьким окном, кнопки внизу; чат справа на широком
// экране и нижней шторкой на телефоне. На телефоне кнопки прячутся через несколько секунд без касаний и появляются по тапу.
export default function CallStage({ chrome }: { chrome: CallChrome }) {
  const { t } = useTranslation()
  const wide = useMediaQuery(WIDE_CALL)
  const coarse = useMediaQuery('(pointer: coarse)')
  const portrait = useMediaQuery('(orientation: portrait)')
  const short = useMediaQuery('(max-height: 500px)')
  const connection = useConnectionState()
  const { localParticipant } = useLocalParticipant()
  const remote = useRemoteParticipants()[0]
  const tracks = useTracks(
    [
      { source: Track.Source.Camera, withPlaceholder: true },
      { source: Track.Source.ScreenShare, withPlaceholder: false },
    ],
    { onlySubscribed: false },
  )
  const { chatMessages, send, isSending } = useChat()

  const remoteScreen = tracks.find((tr) => !tr.participant.isLocal && tr.source === Track.Source.ScreenShare)
  const remoteCam = tracks.find((tr) => remote && tr.participant.identity === remote.identity && tr.source === Track.Source.Camera)
  const localCam = tracks.find((tr) => tr.participant.isLocal && tr.source === Track.Source.Camera)

  // Чат: непрочитанные и короткий предпросмотр нового сообщения при закрытом чате
  const [chatOpen, setChatOpen] = useState(false)
  const [seen, setSeen] = useState(0)
  const [toast, setToast] = useState<{ from: string; text: string } | null>(null)
  const incoming = chatMessages.filter((m) => !m.from?.isLocal)
  const unread = chatOpen ? 0 : Math.max(0, incoming.length - seen)
  useEffect(() => {
    if (chatOpen) {
      setSeen(incoming.length)
      setToast(null)
      return
    }
    const last = incoming[incoming.length - 1]
    if (!last || incoming.length <= seen) return
    setToast({ from: last.from?.name || last.from?.identity || '', text: last.message })
    const timer = setTimeout(() => setToast(null), 4000)
    return () => clearTimeout(timer)
  }, [incoming.length, chatOpen]) // eslint-disable-line react-hooks/exhaustive-deps

  // Автоскрытие кнопок на сенсорных экранах
  const [controlsVisible, setControlsVisible] = useState(true)
  const hideTimer = useRef<ReturnType<typeof setTimeout>>()
  const poke = useCallback(() => {
    setControlsVisible(true)
    clearTimeout(hideTimer.current)
    if (coarse && !chatOpen) hideTimer.current = setTimeout(() => setControlsVisible(false), HIDE_AFTER_MS)
  }, [coarse, chatOpen])
  useEffect(() => {
    poke()
    return () => clearTimeout(hideTimer.current)
  }, [poke])

  const stageRef = useRef<HTMLDivElement>(null)
  const [pipCorner, setPipCorner] = useState<Corner>('br')
  const partnerName = remote ? remote.name || remote.identity : t('call.title')
  const status =
    connection === ConnectionState.Connected
      ? { text: t('call.room.live'), dot: 'bg-emerald-400' }
      : connection === ConnectionState.Reconnecting || connection === ConnectionState.SignalReconnecting
        ? { text: t('call.room.reconnecting'), dot: 'bg-amber-400' }
        : { text: t('call.room.connecting'), dot: 'bg-white/50' }

  const sheetOpen = chatOpen && !wide
  // Своё окно: на телефоне вертикально 96×144, на низком экране (горизонтальный телефон) 144×81, на ноутбуке 224×126
  const pipSize = !wide && portrait ? 'h-36 w-24' : short || !wide ? 'aspect-video w-36' : 'aspect-video w-56'

  return (
    <div className="flex h-full min-h-0 flex-col">
      {/* Тонкая полоска: собеседник, статус, таймер, жалоба */}
      <header className="flex h-12 shrink-0 items-center gap-3 border-b border-white/10 px-3 pt-[env(safe-area-inset-top)] sm:px-4" data-testid="call-topbar">
        <span className={`h-2.5 w-2.5 shrink-0 rounded-full ${status.dot}`} title={status.text} aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-semibold leading-tight">{partnerName}</p>
          <p className="truncate text-[11px] leading-tight text-white/60">{status.text}</p>
        </div>
        <span className={`shrink-0 rounded-full px-3 py-1 text-xs font-bold tabular-nums ${chrome.overtime ? 'bg-amber-400/20 text-amber-200' : 'bg-white/10'}`} data-testid="call-timer">
          {chrome.timer}
        </span>
        {chrome.onComplain && (
          <button type="button" className="shrink-0 rounded-lg px-2 py-1.5 text-xs font-semibold text-white/80 hover:bg-white/10 hover:text-white" onClick={chrome.onComplain}>
            {t('cases.complainCall')}
          </button>
        )}
      </header>

      <div className="flex min-h-0 flex-1">
        <div ref={stageRef} className="relative min-w-0 flex-1 overflow-hidden bg-black" onPointerDown={poke} data-testid="call-stage">
          <RemoteView participant={remote} video={remoteScreen ?? remoteCam} labelRight={pipCorner === 'tl'} />

          {chrome.warning && (
            <div className="pointer-events-none absolute left-1/2 top-3 z-30 max-w-[90%] -translate-x-1/2 rounded-full bg-amber-400 px-4 py-1.5 text-center text-xs font-semibold text-amber-950 shadow-lg" role="status" data-testid="call-warning">
              {chrome.warning}
            </div>
          )}

          {toast && (
            <button
              type="button"
              onClick={() => setChatOpen(true)}
              className={`absolute left-1/2 z-30 flex max-w-[90%] -translate-x-1/2 items-center gap-2 rounded-2xl bg-surface px-4 py-2 text-left text-sm text-ink shadow-xl ${chrome.warning ? 'top-12' : 'top-3'}`}
              data-testid="chat-toast"
            >
              <span className="shrink-0 font-semibold">{toast.from}:</span>
              <span className="truncate">{toast.text}</span>
            </button>
          )}

          <LocalPip
            participant={localParticipant}
            video={localCam}
            containerRef={stageRef}
            initialCorner={!wide || short ? 'tr' : 'br'}
            bottomInset={wide && !short ? 88 : 0}
            sizeClass={pipSize}
            onCornerChange={setPipCorner}
          />

          {/* Панель кнопок поверх видео внизу, с учётом safe-area; на телефоне прячется и появляется по тапу */}
          <div
            className={`absolute inset-x-0 bottom-0 z-30 bg-gradient-to-t from-black/70 to-transparent px-3 pb-[max(1rem,env(safe-area-inset-bottom))] pt-8 transition duration-300 ${
              controlsVisible ? 'translate-y-0 opacity-100' : 'pointer-events-none translate-y-4 opacity-0'
            }`}
            data-testid="call-controls"
            data-visible={controlsVisible}
          >
            <ControlBar chatOpen={chatOpen} unread={unread} onToggleChat={() => setChatOpen((v) => !v)} />
          </div>
        </div>

        {chatOpen && wide && (
          <aside className="w-[min(360px,42vw)] shrink-0 border-l border-white/10" data-testid="chat-side">
            <ChatPanel messages={chatMessages} onSend={send} sending={isSending} onClose={() => setChatOpen(false)} />
          </aside>
        )}
      </div>

      {sheetOpen && (
        <ChatSheet onClose={() => setChatOpen(false)}>
          {(handle) => <ChatPanel messages={chatMessages} onSend={send} sending={isSending} onClose={() => setChatOpen(false)} dragHandle={handle} />}
        </ChatSheet>
      )}
    </div>
  )
}
