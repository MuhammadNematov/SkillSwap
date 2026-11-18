import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { db } from './mocks/exchanges'
import { ME } from './mocks/people'
import type { Review, ReviewInput } from './types'

const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))

// POST /exchanges/{id}/reviews — слепой отзыв: виден после отзыва партнёра или через 7 дней
export async function createReview({ exchangeId, ...body }: ReviewInput): Promise<void> {
  if (USE_MOCKS) {
    await sleep(400)
    const ex = db.exchanges.find((e) => e.id === exchangeId)
    if (!ex) throw new ApiError('NOT_FOUND', 404)
    if (!ex.review?.canWrite || ex.review.submitted) throw new ApiError('INVALID_STATE_TRANSITION', 409)
    ex.review.submitted = true
    return
  }
  try {
    await api.post(`/exchanges/${exchangeId}/reviews`, body)
  } catch (e) {
    throw toApiError(e)
  }
}

// GET /users/{id}/reviews — опубликованные отзывы о пользователе
export async function getUserReviews(userId: string): Promise<Review[]> {
  if (USE_MOCKS) {
    await sleep()
    return ME.reviews
  }
  try {
    const { data } = await api.get<Review[]>(`/users/${userId}/reviews`)
    return data
  } catch (e) {
    throw toApiError(e)
  }
}
