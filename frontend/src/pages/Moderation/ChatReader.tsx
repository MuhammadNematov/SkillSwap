import { useInfiniteQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getCaseChat } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Spinner from '../../components/Spinner'
import { authorName, ChatMessageRow } from './parts'

// Переписка обмена для модератора: только пока у него есть доступ по этому обращению (бэкенд пишет каждое чтение в аудит)
export default function ChatReader({ caseId }: { caseId: string }) {
  const { t } = useTranslation()
  const q = useInfiniteQuery({
    queryKey: ['modChat', caseId],
    queryFn: ({ pageParam }) => getCaseChat({ id: caseId, before: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextBefore,
  })

  if (q.isLoading) return <Spinner />
  if (q.error) return <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, q.error)}</p>
  const pages = q.data?.pages ?? []
  const people = pages.length ? [pages[0].userA, pages[0].userB] : []
  // внутри страницы сообщения идут по возрастанию, а сами страницы — от новых к старым: старые страницы ставим выше
  const items = pages.slice().reverse().flatMap((p) => p.items)

  return (
    <div className="space-y-3">
      {q.hasNextPage && (
        <button className="btn-secondary w-full" disabled={q.isFetchingNextPage} onClick={() => q.fetchNextPage()}>
          {q.isFetchingNextPage ? t('common.loading') : t('moderation.chat.older')}
        </button>
      )}
      {items.length === 0 ? (
        <p className="text-sm text-slate-500">{t('moderation.chat.empty')}</p>
      ) : (
        <ul className="space-y-2">
          {items.map((m) => (
            <ChatMessageRow key={m.id} m={m} author={authorName(m.senderId, people, t('moderation.chat.system'))} />
          ))}
        </ul>
      )}
    </div>
  )
}
