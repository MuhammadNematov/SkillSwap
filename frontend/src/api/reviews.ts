import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { db } from './mocks/exchanges'
import { findPerson, ME } from './mocks/people'
import type { ExchangeReviews, MyReview, Review, ReviewInput, ReviewScores, ReviewsPage, ReviewStatus } from './types'

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))

// ───────── DTO бэкенда ─────────
interface AuthorDto {
  id: string
  name: string
  avatarUrl: string | null
  isDeleted: boolean
}
interface ScoresDto {
  scoreKnowledge: number
  scoreExplaining: number
  scorePunctuality: number
  scorePoliteness: number
}
interface ReviewDto extends ScoresDto {
  id: string
  exchangeId: string
  author: AuthorDto
  average: number
  text: string
  isFullExchange: boolean
  status: ReviewStatus
  createdAt: string
  publishedAt: string | null
  publishAt: string | null
}
interface PublicReviewDto extends ScoresDto {
  id: string
  author: AuthorDto
  average: number
  text: string
  isFullExchange: boolean
  publishedAt: string
}
interface ExchangeReviewsDto {
  exchangeId: string
  canWrite: boolean
  unavailableReason: string | null
  isFullExchange: boolean
  oneSided: boolean
  deadlineAt: string | null
  canEdit: boolean
  myReview: ReviewDto | null
  reviewAboutMe: ReviewDto | null
}
interface UserReviewsDto {
  items: PublicReviewDto[]
  page: number
  totalCount: number
  ratingAvg: number | null
  ratingCount: number
  isNewcomer: boolean
}

const scoresOf = (d: ScoresDto): ReviewScores => ({
  knowledge: d.scoreKnowledge,
  explaining: d.scoreExplaining,
  punctuality: d.scorePunctuality,
  politeness: d.scorePoliteness,
})

function toPublic(d: ReviewDto | PublicReviewDto): Review {
  return {
    id: d.id,
    authorName: d.author.name,
    authorAvatarUrl: d.author.avatarUrl,
    authorDeleted: d.author.isDeleted,
    score: Number(d.average),
    scores: scoresOf(d),
    text: d.text,
    createdAt: 'publishedAt' in d && d.publishedAt ? d.publishedAt : (d as ReviewDto).createdAt,
    isFullExchange: d.isFullExchange,
  }
}

function toMine(d: ReviewDto): MyReview {
  return {
    id: d.id,
    scores: scoresOf(d),
    score: Number(d.average),
    text: d.text,
    status: d.status,
    isFullExchange: d.isFullExchange,
    createdAt: d.createdAt,
    publishedAt: d.publishedAt,
    publishAt: d.publishAt,
  }
}

// ───────── Моки: отзывы по обменам в памяти ─────────
const mockMine = new Map<string, MyReview>()

function mockState(exchangeId: string): ExchangeReviews {
  const ex = db.exchanges.find((e) => e.id === exchangeId)
  if (!ex) throw new ApiError('exchange_not_found', 404)
  const mine = mockMine.get(exchangeId) ?? null
  const closed = ex.status === 'Completed' || ex.status === 'Aborted'
  return {
    exchangeId,
    canWrite: closed && !!ex.review?.canWrite && !mine,
    unavailableReason: !closed ? 'exchange_not_closed' : mine ? 'review_already_exists' : null,
    isFullExchange: ex.status === 'Completed',
    oneSided: false,
    deadlineAt: ex.review?.deadlineAt ?? null,
    canEdit: !!mine && mine.status === 'Pending',
    myReview: mine,
    reviewAboutMe: null,
  }
}

const mockAverage = (s: ReviewScores) => (s.knowledge + s.explaining + s.punctuality + s.politeness) / 4

// GET /exchanges/{id}/reviews: что я могу и что есть по обмену
export async function getExchangeReviews(exchangeId: string): Promise<ExchangeReviews> {
  if (USE_MOCKS) {
    await sleep()
    return mockState(exchangeId)
  }
  try {
    const { data: d } = await api.get<ExchangeReviewsDto>(`/exchanges/${exchangeId}/reviews`)
    return {
      exchangeId: d.exchangeId,
      canWrite: d.canWrite,
      unavailableReason: d.unavailableReason,
      isFullExchange: d.isFullExchange,
      oneSided: d.oneSided,
      deadlineAt: d.deadlineAt,
      canEdit: d.canEdit,
      myReview: d.myReview ? toMine(d.myReview) : null,
      reviewAboutMe: d.reviewAboutMe ? toPublic(d.reviewAboutMe) : null,
    }
  } catch (e) {
    throw toApiError(e)
  }
}

export interface SaveReviewInput extends ReviewInput {
  update?: boolean // PUT вместо POST: правка своего ещё не опубликованного отзыва
}

// POST (создать) или PUT (изменить) /exchanges/{id}/reviews. Отзыв слепой: виден после отзыва партнёра или через 7 дней.
export async function saveReview({ exchangeId, update, knowledge, explaining, punctuality, politeness, text }: SaveReviewInput): Promise<void> {
  if (USE_MOCKS) {
    await sleep(400)
    const state = mockState(exchangeId)
    if (update ? !state.canEdit : !state.canWrite) throw new ApiError('invalid_state_transition', 409)
    const scores = { knowledge, explaining, punctuality, politeness }
    const prev = mockMine.get(exchangeId)
    mockMine.set(exchangeId, {
      id: prev?.id ?? `rv-${exchangeId}`,
      scores,
      score: mockAverage(scores),
      text,
      status: 'Pending',
      isFullExchange: state.isFullExchange,
      createdAt: prev?.createdAt ?? new Date().toISOString(),
      publishAt: prev?.publishAt ?? new Date(Date.now() + 7 * 86_400_000).toISOString(),
    })
    return
  }
  const body = { scoreKnowledge: knowledge, scoreExplaining: explaining, scorePunctuality: punctuality, scorePoliteness: politeness, text }
  try {
    if (update) await api.put(`/exchanges/${exchangeId}/reviews`, body)
    else await api.post(`/exchanges/${exchangeId}/reviews`, body)
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /users/{id}/reviews: опубликованные отзывы о человеке, его рейтинг и «Новичок»
export async function getUserReviews(userId: string, page = 1): Promise<ReviewsPage> {
  if (USE_MOCKS) {
    await sleep()
    const p = findPerson(userId) ?? ME
    return { items: p.reviews, page: 1, totalCount: p.reviews.length, ratingAvg: p.ratingAvg, ratingCount: p.ratingCount, isNewcomer: p.ratingCount < 3 }
  }
  try {
    const { data } = await api.get<UserReviewsDto>(`/users/${userId}/reviews`, { params: { page, pageSize: 20 } })
    return {
      items: data.items.map(toPublic),
      page: data.page,
      totalCount: data.totalCount,
      ratingAvg: data.ratingAvg,
      ratingCount: data.ratingCount,
      isNewcomer: data.isNewcomer,
    }
  } catch (e) {
    throw toApiError(e)
  }
}
