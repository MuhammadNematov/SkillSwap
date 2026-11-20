import { useInfiniteQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getUserReviews } from '../api/reviews'
import { formatDateTime } from '../utils/dates'
import Avatar from './Avatar'
import Icon from './Icon'
import Spinner from './Spinner'

// Опубликованные отзывы о человеке (профиль и «Обо мне») с постраничной подгрузкой
export default function ReviewList({ userId }: { userId: string }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const q = useInfiniteQuery({
    queryKey: ['userReviews', userId],
    queryFn: ({ pageParam }) => getUserReviews(userId, pageParam),
    initialPageParam: 1,
    getNextPageParam: (last, all) => (all.flatMap((p) => p.items).length < last.totalCount ? last.page + 1 : undefined),
  })

  if (q.isLoading) return <Spinner />
  const items = q.data?.pages.flatMap((p) => p.items) ?? []
  if (items.length === 0) return <div className="card text-sm text-slate-500">{t('profile.noReviews')}</div>

  return (
    <div className="space-y-3">
      {items.map((r) => (
        <article key={r.id} className="card">
          <div className="flex items-center gap-3">
            <Avatar name={r.authorDeleted ? '?' : r.authorName} src={r.authorDeleted ? null : r.authorAvatarUrl} className="h-10 w-10" />
            <div className="min-w-0 flex-1">
              <div className="font-semibold text-slate-900">{r.authorDeleted ? t('profile.deletedUser') : r.authorName}</div>
              <div className="text-xs text-slate-400">{formatDateTime(r.createdAt, lang)}</div>
            </div>
            {r.isFullExchange && (
              <span className="rounded-full bg-emerald-100/80 px-2.5 py-0.5 text-xs font-medium text-emerald-700">{t('profile.fullExchange')}</span>
            )}
            <span className="flex items-center gap-1 text-sm font-medium text-amber-600">
              <Icon name="star" className="h-4 w-4" />
              {r.score.toFixed(1)}
            </span>
          </div>
          <p className="mt-3 whitespace-pre-wrap break-words text-sm text-slate-700">{r.text}</p>
        </article>
      ))}
      {q.hasNextPage && (
        <button className="btn-secondary w-full" disabled={q.isFetchingNextPage} onClick={() => q.fetchNextPage()}>
          {t('common.loadMore')}
        </button>
      )}
    </div>
  )
}
