import { useTranslation } from 'react-i18next'
import StatCard from '../../components/staff/StatCard'
import { useAdminCounts } from '../../hooks/useStaffCounts'

// «Обзор» админа и суперадмина: сколько работы ждёт по каждому разделу. Числа берутся из тех же списков, что и сами разделы (totalCount).
export default function AdminOverview() {
  const { t } = useTranslation()
  const c = useAdminCounts()
  return (
    <div className="space-y-4">
      <p className="text-sm text-ink-soft">{t('staffUi.admin.intro')}</p>
      <div className="grid grid-cols-1 gap-3 min-[560px]:grid-cols-2 xl:grid-cols-3">
        <StatCard to="/admin/cases?status=Open" icon="scale" label={t('staffUi.admin.cases')} hint={t('staffUi.admin.casesHint')} query={c.openCases} urgent />
        <StatCard to="/admin/bans" icon="ban" label={t('staffUi.admin.bans')} hint={t('staffUi.admin.bansHint')} query={c.bans} urgent />
        <StatCard to="/admin/suggestions" icon="lightbulb" label={t('staffUi.admin.suggestions')} hint={t('staffUi.admin.suggestionsHint')} query={c.suggestions} />
        <StatCard to="/admin/cases?status=Appealed" icon="appeal" label={t('staffUi.admin.appeals')} hint={t('staffUi.admin.appealsHint')} query={c.appeals} urgent />
        <StatCard to="/admin/users?status=Blocked" icon="users" label={t('staffUi.admin.blocked')} hint={t('staffUi.admin.blockedHint')} query={c.blocked} />
      </div>
    </div>
  )
}
