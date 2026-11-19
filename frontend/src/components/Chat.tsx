import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { getMessages, getSessions, markMessagesRead, sendFile, sendMessage, sendTyping, toMessage } from '../api/exchanges'
import { USE_MOCKS } from '../api/config'
import { getChatHub, startHub } from '../api/realtime'
import type { ChatMessage, Exchange, LessonSession } from '../api/types'
import { useInvalidateAll, useSessionActions } from '../hooks/useSessionActions'
import { formatDateTime, formatTime } from '../utils/dates'
import Icon from './Icon'
import { SessionStatusChip } from './SessionItem'
import SessionProposalModal from './SessionProposalModal'

// Чат обмена. Транспорт — SignalR (/hubs/chat); в режиме моков — заглушки.
// Голосовые сообщения (запись до 2 минут) добавим позже.
export default function Chat({ exchange }: { exchange: Exchange }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const readOnly = exchange.chat ? exchange.chat.mode !== 'ReadWrite' : exchange.status !== 'Active'

  const { data: messages = [] } = useQuery({
    queryKey: ['messages', exchange.id],
    queryFn: () => getMessages(exchange.id),
  })
  const { data: sessions = [] } = useQuery({
    queryKey: ['sessions', exchange.id],
    queryFn: () => getSessions(exchange.id),
  })
  const { confirm, decline } = useSessionActions()

  const [text, setText] = useState('')
  const [proposing, setProposing] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const invalidate = useInvalidateAll()
  const qc = useQueryClient()
  const msgKey = ['messages', exchange.id]
  const upsert = (msg: ChatMessage) =>
    qc.setQueryData<ChatMessage[]>(msgKey, (old = []) =>
      old.some((x) => x.id === msg.id) ? old.map((x) => (x.id === msg.id ? msg : x)) : [...old, msg],
    )
  const send = useMutation({
    mutationFn: sendMessage,
    onSuccess: (msg) => {
      setText('')
      if (USE_MOCKS) invalidate()
      else upsert(msg)
    },
  })
  const [typing, setTyping] = useState(false)
  const lastTypingSent = useRef(0)
  const upload = useMutation({ mutationFn: sendFile, onSuccess: invalidate })

  // События хаба /hubs/chat
  useEffect(() => {
    if (USE_MOCKS) return
    const hub = getChatHub()
    let typingTimer: ReturnType<typeof setTimeout> | undefined
    const mine = (d: { exchangeId: string }) => d.exchangeId === exchange.id

    const onMessage = (dto: Parameters<typeof toMessage>[0]) => {
      if (!mine(dto)) return
      const msg = toMessage(dto)
      upsert(msg)
      if (!msg.senderIsMe && document.visibilityState === 'visible') void markMessagesRead(exchange.id)
    }
    const onDeleted = (d: { exchangeId: string; messageId: string }) => {
      if (!mine(d)) return
      qc.setQueryData<ChatMessage[]>(msgKey, (old = []) =>
        old.map((x) => (x.id === d.messageId ? { ...x, deleted: true, text: undefined } : x)),
      )
    }
    const onRead = (d: { exchangeId: string; readerId: string; readAt: string }) => {
      if (!mine(d)) return
      qc.setQueryData<ChatMessage[]>(msgKey, (old = []) =>
        old.map((x) => (x.senderIsMe && !x.readAt ? { ...x, readAt: d.readAt } : x)),
      )
    }
    const onTyping = (d: { exchangeId: string }) => {
      if (!mine(d)) return
      setTyping(true)
      clearTimeout(typingTimer)
      typingTimer = setTimeout(() => setTyping(false), 4000)
    }
    const onSession = () => qc.invalidateQueries({ queryKey: ['sessions', exchange.id] })
    const onReconnected = () => {
      void hub.invoke('Join', exchange.id).catch(() => undefined)
      qc.invalidateQueries({ queryKey: msgKey })
    }

    hub.on('message', onMessage)
    hub.on('messageEdited', onMessage)
    hub.on('messageDeleted', onDeleted)
    hub.on('messagesRead', onRead)
    hub.on('typing', onTyping)
    hub.on('sessionUpdated', onSession)
    hub.onreconnected(onReconnected)
    void startHub(hub).then((ok) => {
      if (ok) void hub.invoke('Join', exchange.id).catch(() => undefined)
    })
    void markMessagesRead(exchange.id).then(() => qc.invalidateQueries({ queryKey: ['notifications'] })).catch(() => undefined)

    return () => {
      clearTimeout(typingTimer)
      hub.off('message', onMessage)
      hub.off('messageEdited', onMessage)
      hub.off('messageDeleted', onDeleted)
      hub.off('messagesRead', onRead)
      hub.off('typing', onTyping)
      hub.off('sessionUpdated', onSession)
      void hub.invoke('Leave', exchange.id).catch(() => undefined)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [exchange.id])

  // Прокрутка вниз при новых сообщениях
  useEffect(() => {
    const el = listRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages.length, typing])

  function onSubmit(e: FormEvent) {
    e.preventDefault()
    const value = text.trim()
    if (!value || readOnly) return
    send.mutate({ exchangeId: exchange.id, text: value })
  }

  const sessionById = (id?: string): LessonSession | undefined => sessions.find((s) => s.id === id)
  const error = send.error ?? upload.error ?? confirm.error ?? decline.error

  function renderSessionCard(session?: LessonSession) {
    if (!session) return null
    const canAnswer = session.status === 'Proposed' && !session.proposedByMe && !readOnly
    return (
      <div className="w-72 max-w-full rounded-xl border border-ink-line bg-white p-3 text-left text-slate-800 shadow-sm">
        <div className="flex items-center justify-between gap-2">
          <span className="flex items-center gap-1.5 text-xs font-semibold text-brand-700">
            <Icon name="calendar" className="h-4 w-4" />
            {t('chat.sessionCard')}
          </span>
          <SessionStatusChip status={session.status} />
        </div>
        <div className="mt-2 font-semibold">{formatDateTime(session.startAt, lang)}</div>
        {session.topic && <div className="text-sm text-slate-600">{session.topic}</div>}
        <div className="text-xs text-slate-400">
          {t('session.teacher', { name: session.teacherIsMe ? t('session.you') : session.partnerName })}
        </div>
        {canAnswer && (
          <div className="mt-3 flex gap-2">
            <button className="btn-primary flex-1 px-3 py-1.5" disabled={confirm.isPending} onClick={() => confirm.mutate(session.id)}>
              {t('session.accept')}
            </button>
            <button
              className="btn-secondary flex-1 px-3 py-1.5"
              disabled={decline.isPending}
              onClick={() => decline.mutate(session.id, { onSuccess: () => setProposing(true) })}
            >
              {t('session.otherTime')}
            </button>
          </div>
        )}
      </div>
    )
  }

  function renderMessage(m: ChatMessage) {
    if (m.type === 'System') {
      return (
        <div key={m.id} className="my-2 text-center text-xs text-slate-400">
          {m.text}
        </div>
      )
    }

    const mine = m.senderIsMe
    let body
    if (m.type === 'SessionCard') {
      body = renderSessionCard(sessionById(m.sessionId))
    } else if (m.type === 'File') {
      body = (
        <div className={`flex items-center gap-2 rounded-2xl px-4 py-2.5 text-sm ${mine ? 'bg-brand-600 text-white' : 'bg-white text-slate-800'}`}>
          <Icon name="file" className="h-4 w-4 shrink-0" />
          <span className="break-all">{m.fileName}</span>
        </div>
      )
    } else if (m.type === 'Voice') {
      body = (
        <div className={`rounded-2xl px-4 py-2.5 text-sm ${mine ? 'bg-brand-600 text-white' : 'bg-white text-slate-800'}`}>
          {t('chat.voice')} {m.durationSec ? `· ${Math.floor(m.durationSec / 60)}:${String(m.durationSec % 60).padStart(2, '0')}` : ''}
        </div>
      )
    } else {
      body = (
        <div className={`whitespace-pre-wrap break-words rounded-2xl px-4 py-2.5 text-sm ${mine ? 'rounded-br-md bg-brand-600 text-white' : 'rounded-bl-md bg-white text-slate-800'}`}>
          {m.deleted ? <em className="opacity-70">{t('chat.deleted')}</em> : m.text}
        </div>
      )
    }
    if (!body) return null

    return (
      <div key={m.id} className={`flex flex-col ${mine ? 'items-end' : 'items-start'}`}>
        <div className="max-w-[85%]">{body}</div>
        {m.paymentWarning && <p className="mt-1 max-w-[85%] rounded-lg bg-amber-50 px-2.5 py-1.5 text-xs text-amber-800">{t('chat.paymentWarning')}</p>}
        <span className="mt-0.5 px-1 text-[11px] text-slate-400">
          {formatTime(m.createdAt, lang)}
          {mine && m.readAt ? ` · ${t('chat.read')}` : ''}
        </span>
      </div>
    )
  }

  return (
    <div className="card flex h-[560px] flex-col p-0">
      <div ref={listRef} className="flex-1 space-y-2 overflow-y-auto p-4">
        {messages.length === 0 ? (
          <p className="pt-10 text-center text-sm text-slate-400">{t('chat.empty')}</p>
        ) : (
          messages.map(renderMessage)
        )}
        {typing && <p className="px-1 text-xs italic text-slate-400">{t('chat.typing')}</p>}
      </div>

      {error && <p className="border-t border-ink-line px-4 py-2 text-xs text-red-600">{errorMessage(t, error)}</p>}

      {readOnly ? (
        <div className="border-t border-ink-line px-4 py-3 text-center text-sm text-slate-500">{t('exchange.readOnly')}</div>
      ) : (
        <form onSubmit={onSubmit} className="flex items-center gap-2 border-t border-ink-line p-3">
          <input
            ref={fileRef}
            type="file"
            hidden
            accept=".pdf,.docx,.xlsx,.pptx,.zip,.txt,.png,.jpg,.jpeg,.webp"
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) upload.mutate({ exchangeId: exchange.id, file })
              e.target.value = ''
            }}
          />
          <button
            type="button"
            className="rounded-lg p-2 text-slate-500 hover:bg-brand-50"
            aria-label={t('chat.attach')}
            title={t('chat.attach')}
            onClick={() => fileRef.current?.click()}
          >
            <Icon name="paperclip" />
          </button>
          <button
            type="button"
            className="rounded-lg p-2 text-slate-500 hover:bg-brand-50"
            aria-label={t('chat.proposeLesson')}
            title={t('chat.proposeLesson')}
            onClick={() => setProposing(true)}
          >
            <Icon name="calendar" />
          </button>
          <input
            className="input"
            value={text}
            maxLength={2000}
            placeholder={t('chat.placeholder')}
            onChange={(e) => {
              setText(e.target.value)
              if (Date.now() - lastTypingSent.current > 2000) {
                lastTypingSent.current = Date.now()
                sendTyping(exchange.id)
              }
            }}
          />
          <button className="btn-primary px-3" disabled={!text.trim() || send.isPending} aria-label={t('chat.send')}>
            <Icon name="send" />
          </button>
        </form>
      )}

      {proposing && (
        <SessionProposalModal exchangeId={exchange.id} partnerId={exchange.partner.id} partnerName={exchange.partner.name} onClose={() => setProposing(false)} />
      )}
    </div>
  )
}
