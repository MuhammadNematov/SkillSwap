import { VideoTrack, isTrackReference, useIsSpeaking, type TrackReferenceOrPlaceholder } from '@livekit/components-react'
import type { Participant } from 'livekit-client'
import { useCallback, useEffect, useRef, useState, type PointerEvent, type RefObject } from 'react'
import { useTranslation } from 'react-i18next'

export type Corner = 'tl' | 'tr' | 'bl' | 'br'
const MARGIN = 12

// Своё видео маленьким окном поверх (PiP): закругление и тень, зеркально (как в зеркале), перетаскивается и прилипает к ближайшему углу,
// не вылезает за края. bottomInset — сколько места снизу занимает панель кнопок, чтобы окно её не закрывало.
export default function LocalPip({
  participant,
  video,
  containerRef,
  initialCorner,
  bottomInset,
  sizeClass,
  onCornerChange,
}: {
  participant: Participant
  video: TrackReferenceOrPlaceholder | undefined
  containerRef: RefObject<HTMLElement | null>
  initialCorner: Corner
  bottomInset: number
  sizeClass: string
  onCornerChange?: (corner: Corner) => void
}) {
  const { t } = useTranslation()
  const speaking = useIsSpeaking(participant)
  const boxRef = useRef<HTMLDivElement>(null)
  const [corner, setCorner] = useState<Corner>(initialCorner)
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const drag = useRef<{ dx: number; dy: number; moved: boolean } | null>(null)
  const [, force] = useState(0)

  useEffect(() => setCorner(initialCorner), [initialCorner])
  useEffect(() => onCornerChange?.(corner), [corner, onCornerChange])

  // Позиция угла внутри контейнера с учётом размеров окна и панели кнопок
  const cornerPos = useCallback(
    (c: Corner) => {
      const box = containerRef.current?.getBoundingClientRect()
      const self = boxRef.current?.getBoundingClientRect()
      if (!box || !self) return { x: MARGIN, y: MARGIN }
      const x = c.endsWith('l') ? MARGIN : box.width - self.width - MARGIN
      const y = c.startsWith('t') ? MARGIN : box.height - self.height - MARGIN - bottomInset
      return { x: Math.max(MARGIN, x), y: Math.max(MARGIN, y) }
    },
    [containerRef, bottomInset],
  )

  // Пересчёт при изменении размеров (поворот телефона, открытие чата)
  useEffect(() => {
    const el = containerRef.current
    if (!el) return
    const ro = new ResizeObserver(() => force((n) => n + 1))
    ro.observe(el)
    return () => ro.disconnect()
  }, [containerRef])

  function onPointerDown(e: PointerEvent<HTMLDivElement>) {
    const self = boxRef.current?.getBoundingClientRect()
    const box = containerRef.current?.getBoundingClientRect()
    if (!self || !box) return
    e.currentTarget.setPointerCapture(e.pointerId)
    drag.current = { dx: e.clientX - self.left, dy: e.clientY - self.top, moved: false }
    setPos({ x: self.left - box.left, y: self.top - box.top })
  }

  function onPointerMove(e: PointerEvent<HTMLDivElement>) {
    const d = drag.current
    const box = containerRef.current?.getBoundingClientRect()
    const self = boxRef.current?.getBoundingClientRect()
    if (!d || !box || !self) return
    d.moved = true
    const x = Math.min(Math.max(0, e.clientX - box.left - d.dx), box.width - self.width)
    const y = Math.min(Math.max(0, e.clientY - box.top - d.dy), box.height - self.height)
    setPos({ x, y })
  }

  function onPointerUp() {
    const box = containerRef.current?.getBoundingClientRect()
    const self = boxRef.current?.getBoundingClientRect()
    drag.current = null
    if (!box || !self) return setPos(null)
    // Ближайший угол по центру окна
    const cx = self.left + self.width / 2 - box.left
    const cy = self.top + self.height / 2 - box.top
    setCorner(`${cy < box.height / 2 ? 't' : 'b'}${cx < box.width / 2 ? 'l' : 'r'}` as Corner)
    setPos(null)
  }

  const p = pos ?? cornerPos(corner)
  const hasVideo = !!video && isTrackReference(video) && !video.publication.isMuted && !!video.publication.track
  const name = participant.name || t('call.room.you')

  return (
    <div
      ref={boxRef}
      role="img"
      aria-label={t('call.room.self')}
      title={t('call.room.self')}
      data-testid="local-pip"
      data-corner={corner}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      className={`absolute z-20 cursor-grab touch-none select-none overflow-hidden rounded-2xl bg-[#211d3d] shadow-[0_8px_30px_rgba(0,0,0,0.45)] ring-1 ring-white/15 active:cursor-grabbing ${
        pos ? '' : 'transition-[left,top] duration-200'
      } ${speaking ? 'ring-2 ring-emerald-400' : ''} ${sizeClass}`}
      style={{ left: p.x, top: p.y }}
    >
      {hasVideo ? (
        <VideoTrack trackRef={video} className="h-full w-full object-cover" style={{ transform: 'scaleX(-1)' }} data-testid="local-video" />
      ) : (
        <div className="grid h-full w-full place-items-center">
          <span className="grid h-10 w-10 place-items-center rounded-full bg-brand-600 text-lg font-bold text-white">{name.trim().charAt(0).toUpperCase()}</span>
        </div>
      )}
    </div>
  )
}
