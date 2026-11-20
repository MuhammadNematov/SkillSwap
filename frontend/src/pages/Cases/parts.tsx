import { useTranslation } from 'react-i18next'
import type { CaseStatus } from '../../api/cases'

const TONE: Partial<Record<CaseStatus, string>> = {
  Open: 'bg-amber-100/80 text-amber-700',
  InProgress: 'bg-brand-100/80 text-brand-700',
  WaitingResponse: 'bg-amber-100/80 text-amber-700',
  Resolved: 'bg-emerald-100/80 text-emerald-700',
  Appealed: 'bg-brand-100/80 text-brand-700',
  AppealReview: 'bg-brand-100/80 text-brand-700',
}

export function CaseStatusChip({ status }: { status: CaseStatus }) {
  const { t } = useTranslation()
  return <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${TONE[status] ?? 'bg-slate-100/80 text-slate-600'}`}>{t(`cases.status.${status}`)}</span>
}
