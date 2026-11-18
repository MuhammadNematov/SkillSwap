import { api } from './client'
import { USE_MOCKS } from './config'
import { toApiError } from './errors'
import { SKILLS } from './mocks/people'
import type { Skill } from './types'

const sleep = (ms = 200) => new Promise((r) => setTimeout(r, ms))

// GET /skills — каталог «Категория → Навык»
export async function getSkills(): Promise<Skill[]> {
  if (USE_MOCKS) {
    await sleep()
    return SKILLS
  }
  try {
    const { data } = await api.get<Skill[]>('/skills')
    return data
  } catch (e) {
    throw toApiError(e)
  }
}
