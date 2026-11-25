import { useEffect, useRef, useState, type PointerEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

const OPEN = 0.5
const MAX = 0.85
const CLOSE_BELOW = 0.28

// Нижняя шторка чата на телефоне: по умолчанию половина экрана (видео собеседника остаётся видно сверху), тянется вверх до ~85% и вниз до закрытия.
// Высота и нижний край считаются от visualViewport, поэтому поле ввода не уходит под экранную клавиатуру.
export default function ChatSheet({ onClose, children }: { onClose: () => void; children: (handle: ReactNode) => ReactNode }) {
  const { t } = useTranslation()
  const [share, setShare] = useState(OPEN)
  const [viewport, setViewport] = useState(() => ({ height: window.visualViewport?.height ?? window.innerHeight, keyboard: 0 }))
  const drag = useRef<{ startY: number; startShare: number } | null>(null)

  useEffect(() => {
    const vv = window.visualViewport
    if (!vv) return
    const on = () => setViewport({ height: vv.height, keyboard: Math.max(0, window.innerHeight - vv.height - vv.offsetTop) })
    on()
    vv.addEventListener('resize', on)
    vv.addEventListener('scroll', on)
    return () => {
      vv.removeEventListener('resize', on)
      vv.removeEventListener('scroll', on)
    }
  }, [])

  function down(e: PointerEvent<HTMLDivElement>) {
    e.currentTarget.setPointerCapture(e.pointerId)
    drag.current = { startY: e.clientY, startShare: share }
  }
  function move(e: PointerEvent<HTMLDivElement>) {
    if (!drag.current) return
    const next = drag.current.startShare + (drag.current.startY - e.clientY) / viewport.height
    setShare(Math.min(MAX, Math.max(0.15, next)))
  }
  function up() {
    if (!drag.current) return
    drag.current = null
    if (share < CLOSE_BELOW) onClose()
    else if (share < OPEN) setShare(OPEN)
  }

  // С открытой клавиатурой шторка занимает почти всё оставшееся место, чтобы были видны последние сообщения и поле ввода
  const height = Math.round(viewport.height * (viewport.keyboard > 80 ? MAX : share))
  const handle = (
    <div
      className="flex h-6 shrink-0 cursor-grab touch-none items-center justify-center active:cursor-grabbing"
      onPointerDown={down}
      onPointerMove={move}
      onPointerUp={up}
      onPointerCancel={up}
      role="separator"
      aria-label={t('call.room.chatTitle')}
      data-testid="chat-sheet-handle"
    >
      <span className="h-1.5 w-10 rounded-full bg-ink-faint/60" />
    </div>
  )

  return (
    <div
      className="fixed inset-x-0 z-40 overflow-hidden rounded-t-2xl shadow-[0_-8px_30px_rgba(0,0,0,0.4)]"
      style={{ bottom: viewport.keyboard, height, paddingBottom: viewport.keyboard > 0 ? 0 : 'env(safe-area-inset-bottom)' }}
      data-testid="chat-sheet"
    >
      <div className="h-full bg-surface">{children(handle)}</div>
    </div>
  )
}
