import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { getModCases, takeCase } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Icon from '../../components/Icon'
import StatCard from '../../components/staff/StatCard'
import { useModCounts } from '../../hooks/useStaffCounts'

// «Обзор» модератора: сколько свободных обращений, сколько в работе и апелляций на проверке, и кнопка «Взять следующее»
export default function ModOverview() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const c = useModCounts()
  // Первое обращение очереди (сервер отдаёт её в порядке срочности) берётся в работу, и открывается его карточка
  const next = useMutation({
    mutationFn: async () => {
      const queue = await getModCases('Queue')
      if (queue.length === 0) return null
      return takeCase(queue[0].id)
    },
    onSuccess: (taken) => {
      qc.invalidateQueries({ queryKey: ['staffCount'] })
      qc.invalidateQueries({ queryKey: ['modCases'] })
      if (taken) navigate(`/moderation/cases/${taken.id}`)
    },
  })
  const queueEmpty = !c.queue.isLoading && !c.queue.isError && c.queue.data === 0

  return (
    <div className="space-y-4">
      <p className="text-sm text-ink-soft">{t('staffUi.mod.intro')}</p>
      <div className="grid grid-cols-1 gap-3 min-[560px]:grid-cols-2 xl:grid-cols-3">
        <StatCard to="/moderation/queue" icon="inbox" label={t('staffUi.mod.queue')} hint={t('staffUi.mod.queueHint')} query={c.queue} />
        <StatCard to="/moderation/mine" icon="folder" label={t('staffUi.mod.mine')} hint={t('staffUi.mod.mineHint')} query={c.mine} />
        <StatCard to="/moderation/appeals" icon="appeal" label={t('staffUi.mod.appeals')} hint={t('staffUi.mod.appealsHint')} query={c.appeals} urgent />
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <button className="btn-primary h-11" disabled={next.isPending || queueEmpty} onClick={() => next.mutate()}>
          <Icon name="arrowRight" className="h-4 w-4" />
          {next.isPending ? t('common.loading') : t('staffUi.mod.takeNext')}
        </button>
        {queueEmpty && <span className="text-sm text-ink-soft">{t('staffUi.mod.queueEmpty')}</span>}
        {next.data === null && <span className="text-sm text-ink-soft">{t('staffUi.mod.queueEmpty')}</span>}
      </div>
      {next.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, next.error)}</p>}
    </div>
  )
}
