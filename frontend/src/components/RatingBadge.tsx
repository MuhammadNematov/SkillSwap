import { useTranslation } from 'react-i18next'
import Icon from './Icon'

// По ТЗ (8.1): профиль меньше чем с 3 отзывами показывается как «Новичок»
export default function RatingBadge({ ratingAvg, ratingCount }: { ratingAvg: number; ratingCount: number }) {
  const { t } = useTranslation()

  if (ratingCount < 3) {
    return (
      <span className="inline-block rounded-full bg-slate-100/80 px-2.5 py-0.5 text-xs font-medium text-slate-600">
        {t('matches.newbie')}
      </span>
    )
  }
  return (
    <span className="inline-flex items-center gap-1 text-sm font-medium text-amber-600">
      <Icon name="star" className="h-4 w-4" />
      {ratingAvg.toFixed(1)}
      <span className="text-xs font-normal text-slate-400">({ratingCount})</span>
    </span>
  )
}
