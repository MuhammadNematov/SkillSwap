import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { getJoinToken } from '../../api/calls'
import { getSessionExchangeId } from '../../api/exchanges'
import { errorMessage, toApiError } from '../../api/errors'
import { formatDateTime } from '../../utils/dates'
import CallRoom from '../../components/CallRoom'
import CaseModal from '../../components/CaseModal'
import PreJoin, { type JoinPrefs } from '../../components/PreJoin'
import Spinner from '../../components/Spinner'

const FIVE_MIN = 5 * 60_000

function fmt(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
}

// Страница видеоурока: берём токен у бэкенда и открываем комнату.
// За 5 минут до планового окончания показываем «осталось 5 минут», комната закрывается в roomClosesAt.
export default function Call() {
  const { t, i18n } = useTranslation()
  const { sessionId = '' } = useParams()
  const navigate = useNavigate()
  const [now, setNow] = useState(() => Date.now())

  const [exited, setExited] = useState(false)
  const [prefs, setPrefs] = useState<JoinPrefs | null>(null)
  const [entering, setEntering] = useState(false)
  const [complaining, setComplaining] = useState(false)
  const exchangeId = useQuery({ queryKey: ['sessionExchange', sessionId], queryFn: () => getSessionExchangeId(sessionId), staleTime: Infinity })
  const { data, error, isLoading, isFetching, refetch } = useQuery({
    queryKey: ['joinToken', sessionId],
    queryFn: () => getJoinToken(sessionId),
    retry: false,
    refetchOnWindowFocus: false,
    staleTime: Infinity, // токен одноразовый по смыслу: не перезапрашиваем сам
  })

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])

  // Токен короткий: если пока смотрели превью он истёк, берём новый
  const enter = async (p: JoinPrefs) => {
    setEntering(true)
    try {
      const expired = data?.expiresAt ? new Date(data.expiresAt).getTime() - Date.now() < 15_000 : false
      if (expired) {
        const r = await refetch()
        if (!r.data) return
      }
      setPrefs(p)
      setExited(false)
    } finally {
      setEntering(false)
    }
  }

  if (isLoading) return <Spinner />

  if (error || !data) {
    const apiErr = error ? toApiError(error) : null
    const opensAt = apiErr?.code === 'join_window_not_open' && typeof apiErr.data.opensAt === 'string' ? apiErr.data.opensAt : null
    return (
      <div className="mx-auto max-w-lg space-y-4 py-10 text-center">
        <h1 className="text-2xl font-extrabold text-ink">{t('call.title')}</h1>
        <p className="rounded-2xl bg-red-50 px-4 py-3 text-sm text-red-700">{errorMessage(t, error)}</p>
        {opensAt && <p className="text-sm text-ink-soft">{t('call.opensAt', { time: formatDateTime(opensAt, i18n.resolvedLanguage ?? 'ru') })}</p>}
        <Link to="/app/calendar" className="btn-secondary">
          {t('call.toCalendar')}
        </Link>
      </div>
    )
  }

  const left = new Date(data.plannedEndAt).getTime() - now
  const closesIn = new Date(data.roomClosesAt).getTime() - now
  const closed = closesIn <= 0

  const inRoom = !closed && !exited && !!prefs

  return (
    <div className={`flex flex-col gap-4 ${inRoom ? '' : 'min-h-[calc(100vh-9rem)]'}`}>
      {!inRoom && (
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-extrabold text-ink">{t('call.title')}</h1>
        <div className="flex items-center gap-2">
          {exchangeId.data && !closed && (
            <button className="btn-secondary px-3 py-1.5" onClick={() => setComplaining(true)}>
              {t('cases.complainCall')}
            </button>
          )}
          <span className="rounded-full bg-brand-100 px-4 py-1.5 text-sm font-bold text-brand-700">
            {left > 0 ? t('call.timeLeft', { time: fmt(left) }) : t('call.overtime')}
          </span>
        </div>
      </div>

      )}
      {!inRoom && left > 0 && left <= FIVE_MIN && (
        <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm font-semibold text-amber-800">{t('call.fiveMinutes')}</div>
      )}
      {!inRoom && left <= 0 && !closed && (
        <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">{t('call.closesIn', { time: fmt(closesIn) })}</div>
      )}

      {closed ? (
        <div className="card space-y-3 text-center text-sm text-ink-soft">
          <p>{t('call.closed')}</p>
          {exchangeId.data && (
            <Link to={`/app/exchanges/${exchangeId.data}`} className="btn-primary">
              {t('call.toExchange')}
            </Link>
          )}
        </div>
      ) : exited ? (
        // Вышли из комнаты или связь оборвалась: можно вернуться, для этого берём новый токен (пока окно входа открыто)
        <div className="card mx-auto w-full max-w-md space-y-4 text-center">
          <p className="text-sm text-ink-soft">{t('call.exited')}</p>
          <div className="flex flex-wrap justify-center gap-3">
            <button
              className="btn-primary"
              disabled={isFetching}
              onClick={async () => {
                const r = await refetch()
                if (r.data) setExited(false)
              }}
            >
              {isFetching ? t('common.loading') : t('call.rejoin')}
            </button>
            <button className="btn-secondary" onClick={() => navigate('/app/calendar')}>
              {t('call.toCalendar')}
            </button>
          </div>
        </div>
      ) : !prefs ? (
        <PreJoin busy={entering} onJoin={enter} />
      ) : (
        <CallRoom
          data={data}
          prefs={prefs}
          chrome={{
            timer: left > 0 ? fmt(left) : t('call.overtime'),
            overtime: left <= 0,
            warning: left > 0 && left <= FIVE_MIN ? t('call.fiveMinutes') : left <= 0 ? t('call.closesIn', { time: fmt(closesIn) }) : null,
            onComplain: exchangeId.data ? () => setComplaining(true) : undefined,
          }}
          onLeave={() => setExited(true)}
          onRetry={() => refetch()}
        />
      )}
      {complaining && exchangeId.data && (
        <CaseModal exchangeId={exchangeId.data} partnerName="" fixed={{ type: 'Conduct' }} fromCall onClose={() => setComplaining(false)} />
      )}
    </div>
  )
}
