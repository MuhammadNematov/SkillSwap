import { useTranslation } from 'react-i18next'
import type { CaseStatus } from '../../api/cases'
import Tag, { type TagTone } from '../../components/staff/Tag'

const TONE: Record<CaseStatus, TagTone> = {
  Open: 'amber',
  InProgress: 'brand',
  WaitingResponse: 'amber',
  Resolved: 'green',
  Appealed: 'amber',
  AppealReview: 'brand',
  Closed: 'gray',
  Withdrawn: 'gray',
}

export function CaseStatusChip({ status }: { status: CaseStatus }) {
  const { t } = useTranslation()
  return <Tag tone={TONE[status] ?? 'gray'}>{t(`cases.status.${status}`)}</Tag>
}
