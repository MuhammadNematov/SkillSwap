import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { getMessages, getSessions, sendFile, sendMessage } from '../api/exchanges'
import type { ChatMessage, Exchange, LessonSession } from '../api/types'
import { useInvalidateAll, useSessionActions } from '../hooks/useSessionActions'
import { formatDateTime, formatTime } from '../utils/dates'
import Icon from './Icon'
import { SessionStatusChip } from './SessionItem'
import SessionProposalModal from './SessionProposalModal'

// Чат обмена. Сейчас работает на заглушках; по ТЗ транспорт — SignalR (/hubs/chat), подключим вместе с бэкендом.
// Голосовые сообщения (запись до 2 минут) и «печатает…» добавим позже.
export default function Chat({ exchange }: { exchange: Exchange }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const readOnly = exchange.status !== 'Active'

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
  const send = useMutation({
    mutationFn: sendMessage,
    onSuccess: () => {
      setText('')
      invalidate()
    },
  })
  const upload = useMutation({ mutationFn: sendFile, onSuccess: invalidate })

  // Прокрутка вниз при новых сообщениях
  useEffect(() => {
    const el = listRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages.length])

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
      <div className="w-72 max-w-full rounded-xl border border-white/70 bg-white/80 p-3 text-left text-slate-800 shadow-sm">
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
        <div className={`flex items-center gap-2 rounded-2xl px-4 py-2.5 text-sm ${mine ? 'bg-brand-600 text-white' : 'bg-white/80 text-slate-800'}`}>
          <Icon name="file" className="h-4 w-4 shrink-0" />
          <span className="break-all">{m.fileName}</span>
        </div>
      )
    } else if (m.type === 'Voice') {
      body = (
        <div className={`rounded-2xl px-4 py-2.5 text-sm ${mine ? 'bg-brand-600 text-white' : 'bg-white/80 text-slate-800'}`}>
          {t('chat.voice')} {m.durationSec ? `· ${Math.floor(m.durationSec / 60)}:${String(m.durationSec % 60).padStart(2, '0')}` : ''}
        </div>
      )
    } else {
      body = (
        <div className={`whitespace-pre-wrap break-words rounded-2xl px-4 py-2.5 text-sm ${mine ? 'rounded-br-md bg-brand-600 text-white' : 'rounded-bl-md bg-white/80 text-slate-800'}`}>
          {m.text}
        </div>
      )
    }
    if (!body) return null

    return (
      <div key={m.id} className={`flex flex-col ${mine ? 'items-end' : 'items-start'}`}>
        <div className="max-w-[85%]">{body}</div>
        <span className="mt-0.5 px-1 text-[11px] text-slate-400">{formatTime(m.createdAt, lang)}</span>
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
      </div>

      {error && <p className="border-t border-white/60 px-4 py-2 text-xs text-red-600">{errorMessage(t, error)}</p>}

      {readOnly ? (
        <div className="border-t border-white/60 px-4 py-3 text-center text-sm text-slate-500">{t('exchange.readOnly')}</div>
      ) : (
        <form onSubmit={onSubmit} className="flex items-center gap-2 border-t border-white/60 p-3">
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
            className="rounded-lg p-2 text-slate-500 hover:bg-white/60"
            aria-label={t('chat.attach')}
            title={t('chat.attach')}
            onClick={() => fileRef.current?.click()}
          >
            <Icon name="paperclip" />
          </button>
          <button
            type="button"
            className="rounded-lg p-2 text-slate-500 hover:bg-white/60"
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
            onChange={(e) => setText(e.target.value)}
          />
          <button className="btn-primary px-3" disabled={!text.trim() || send.isPending} aria-label={t('chat.send')}>
            <Icon name="send" />
          </button>
        </form>
      )}

      {proposing && (
        <SessionProposalModal exchangeId={exchange.id} partnerName={exchange.partner.name} onClose={() => setProposing(false)} />
      )}
    </div>
  )
}
