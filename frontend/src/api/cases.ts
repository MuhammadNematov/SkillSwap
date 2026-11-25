import { api } from './client'
import { USE_MOCKS } from './config'
import { ApiError, toApiError } from './errors'
import { db } from './mocks/exchanges'

// Обращения (жалобы и споры по отзывам): подача и ход дела у участников, очередь модератора, материалы и решения админа.
// Формы ответов совпадают с DTO бэкенда (camelCase), поэтому адаптеры не нужны.

export type CaseType = 'ReviewDispute' | 'Conduct' | 'NoShowAbuse'
export type CaseCategory =
  | 'ReviewUnfair'
  | 'ReviewAbusive'
  | 'ReviewFalse'
  | 'Insults'
  | 'Threats'
  | 'Harassment'
  | 'Spam'
  | 'Extortion'
  | 'SystematicEvasion'
  | 'BadFaithLessons'
export type CaseStatus = 'Open' | 'Withdrawn' | 'InProgress' | 'WaitingResponse' | 'Resolved' | 'Appealed' | 'AppealReview' | 'Closed'
export type CaseDecision = 'ReviewKept' | 'ReviewHidden' | 'Warning' | 'ExchangeEnded' | 'FrozenBanRequested' | 'NoViolation'
export type CasePriority = 'Normal' | 'High'
export type AppealOutcome = 'Upheld' | 'Overturned' | 'Changed'
export type BanRequestStatus = 'Pending' | 'Approved' | 'Rejected'

// Категории по типам обращения (раздел 8.2 ТЗ)
export const CATEGORIES: Record<CaseType, CaseCategory[]> = {
  ReviewDispute: ['ReviewUnfair', 'ReviewAbusive', 'ReviewFalse'],
  Conduct: ['Insults', 'Threats', 'Harassment', 'Spam', 'Extortion'],
  NoShowAbuse: ['SystematicEvasion', 'BadFaithLessons'],
}
// Допустимые решения по типам (раздел 8.3 ТЗ)
export const DECISIONS: Record<CaseType, CaseDecision[]> = {
  ReviewDispute: ['ReviewKept', 'ReviewHidden'],
  Conduct: ['Warning', 'ExchangeEnded', 'FrozenBanRequested', 'NoViolation'],
  NoShowAbuse: ['Warning', 'ExchangeEnded', 'FrozenBanRequested', 'NoViolation'],
}
// К этим решениям можно добавить предупреждение виновному
export const CAN_ALSO_WARN: CaseDecision[] = ['ReviewHidden', 'ExchangeEnded', 'FrozenBanRequested']

export interface CasePerson {
  id: string
  name: string
  avatarUrl: string | null
  isDeleted: boolean
}

export interface CaseAppealInfo {
  appellantId: string
  reason: string
  filedAt: string
  reviewInProgress: boolean
  outcome: AppealOutcome | null
  finalDecision: CaseDecision | null
  decidedAt: string | null
}

// Обращение глазами участника
export interface CaseItem {
  id: string
  type: CaseType
  category: CaseCategory
  status: CaseStatus
  priority: CasePriority
  exchangeId: string
  reviewId: string | null
  openedBy: CasePerson
  against: CasePerson
  myRole: 'Opener' | 'Against'
  description: string
  fromCall: boolean
  createdAt: string
  evidenceMessageIds: string[]
  decision: CaseDecision | null
  alsoWarned: boolean
  decisionReason: string | null
  resolvedAt: string | null
  responseDeadlineAt: string | null
  canWithdraw: boolean
  pendingQuestions: number
  canAppeal: boolean
  appeal: CaseAppealInfo | null
}

export interface CaseList<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

export interface OpenCaseInput {
  type: CaseType
  category: CaseCategory
  exchangeId: string
  reviewId?: string | null
  description: string
  messageIds?: string[]
  fromCall?: boolean
}

export interface Explanation {
  id: string
  kind: 'Question' | 'Answer'
  senderId: string
  recipientId: string
  text: string
  inReplyToId: string | null
  answered: boolean
  createdAt: string
}
export interface ExplanationThread {
  caseId: string
  status: CaseStatus
  responseDeadlineAt: string | null
  items: Explanation[]
}

// ───────── Служебные формы ─────────
export interface StaffAppeal {
  appellantId: string
  reason: string
  filedAt: string
  adminId: string | null
  reviewerModeratorId: string | null
  reviewerConclusion: string | null
  outcome: AppealOutcome | null
  finalDecision: CaseDecision | null
  decidedAt: string | null
}
export interface ModReview {
  id: string
  authorId: string
  targetId: string
  scoreKnowledge: number
  scoreExplaining: number
  scorePunctuality: number
  scorePoliteness: number
  average: number
  text: string
  status: 'Pending' | 'Published' | 'Hidden' | 'Frozen'
}
export interface ModCase {
  id: string
  type: CaseType
  category: CaseCategory
  status: CaseStatus
  priority: CasePriority
  exchangeId: string
  reviewId: string | null
  openedBy: CasePerson
  against: CasePerson
  description: string
  fromCall: boolean
  createdAt: string
  dueAt: string
  isOverdue: boolean
  moderatorId: string | null
  isMine: boolean
  hasChatAccess: boolean
  evidenceMessageIds: string[]
  review: ModReview | null
  decision: CaseDecision | null
  alsoWarned: boolean
  decisionReason: string | null
  resolvedAt: string | null
  responseDeadlineAt: string | null
  pendingQuestions: number
  appeal: StaffAppeal | null
  isAppealReviewer: boolean
}
export interface ModMessage {
  id: string
  senderId: string | null
  type: 'Text' | 'Voice' | 'File' | 'SessionCard' | 'System'
  text: string | null
  paymentWarning: boolean
  attachment?: { fileName: string; contentType: string; sizeBytes: number; durationSeconds: number | null } | null
  createdAt: string
  editedAt: string | null
  deletedAt: string | null
  readAt: string | null
  isEvidence: boolean
  revisions: { text: string; changedAt: string }[]
}
export interface ModChatPage {
  caseId: string
  exchangeId: string
  userA: CasePerson
  userB: CasePerson
  items: ModMessage[]
  nextBefore: string | null
}
export interface AdminCase {
  id: string
  type: CaseType
  category: CaseCategory
  status: CaseStatus
  priority: CasePriority
  exchangeId: string
  reviewId: string | null
  openedBy: CasePerson
  against: CasePerson
  description: string
  fromCall: boolean
  createdAt: string
  dueAt: string // срок реакции модератора (24 или 72 часа по приоритету)
  isOverdue: boolean // срок прошёл, а обращение не взято
  moderatorId: string | null
  decision: CaseDecision | null
  alsoWarned: boolean
  decisionReason: string | null
  resolvedAt: string | null
  closedAt: string | null
  appeal: StaffAppeal | null
}
export interface Warning {
  id: string
  userId: string
  issuedById: string
  reason: string
  issuedAt: string
  expiresAt: string
  revokedAt: string | null
}
export interface BanRequest {
  id: string
  userId: string
  userName: string
  caseId: string | null
  requestedById: string | null
  reason: string
  status: BanRequestStatus
  decidedById: string | null
  decidedAt: string | null
  createdAt: string
}
export interface AdminCaseDetail {
  case: AdminCase
  review: ModReview | null
  evidence: ModMessage[]
  explanations: Explanation[]
  warnings: Warning[]
  banRequests: BanRequest[]
}

// ───────── Моки: небольшая память для проверки экранов без бэкенда ─────────
const sleep = (ms = 250) => new Promise((r) => setTimeout(r, ms))
const iso = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString()
const HOUR = 3_600_000
const person = (id: string, name: string): CasePerson => ({ id, name, avatarUrl: null, isDeleted: false })
const MOCK_ME = 'me'

let mockSeq = 100
const mockCases: ModCase[] = [
  {
    id: 'c1',
    type: 'Conduct',
    category: 'Insults',
    status: 'Open',
    priority: 'High',
    exchangeId: 'e1',
    reviewId: null,
    openedBy: person('u2', 'Madina'),
    against: person('u3', 'Rustam'),
    description: 'Во время урока партнёр грубо разговаривал и перебивал, после просьбы остановиться продолжил.',
    fromCall: false,
    createdAt: iso(-5 * HOUR),
    dueAt: iso(19 * HOUR),
    isOverdue: false,
    moderatorId: null,
    isMine: false,
    hasChatAccess: false,
    evidenceMessageIds: [],
    review: null,
    decision: null,
    alsoWarned: false,
    decisionReason: null,
    resolvedAt: null,
    responseDeadlineAt: null,
    pendingQuestions: 0,
    appeal: null,
    isAppealReviewer: false,
  },
  {
    id: 'c2',
    type: 'ReviewDispute',
    category: 'ReviewUnfair',
    status: 'Open',
    priority: 'Normal',
    exchangeId: 'e3',
    reviewId: 'rv1',
    openedBy: person('u4', 'Нигора'),
    against: person('u5', 'Диёрбек'),
    description: 'Отзыв не отражает реальные уроки: все занятия прошли вовремя, а в отзыве написано обратное.',
    fromCall: false,
    createdAt: iso(-30 * HOUR),
    dueAt: iso(40 * HOUR),
    isOverdue: false,
    moderatorId: null,
    isMine: false,
    hasChatAccess: false,
    evidenceMessageIds: [],
    review: {
      id: 'rv1',
      authorId: 'u5',
      targetId: 'u4',
      scoreKnowledge: 2,
      scoreExplaining: 2,
      scorePunctuality: 1,
      scorePoliteness: 3,
      average: 2,
      text: 'Урок начался с опозданием, объяснения были путаными, я не получил того, что ожидал.',
      status: 'Frozen',
    },
    decision: null,
    alsoWarned: false,
    decisionReason: null,
    resolvedAt: null,
    responseDeadlineAt: null,
    pendingQuestions: 0,
    appeal: null,
    isAppealReviewer: false,
  },
]
// Пояснения по обращениям (моки)
const mockExplanations = new Map<string, Explanation[]>()
const mockFrozen = new Set<string>()

const mockUser = (c: ModCase): CaseItem => ({
  id: c.id,
  type: c.type,
  category: c.category,
  status: c.status,
  priority: c.priority,
  exchangeId: c.exchangeId,
  reviewId: c.reviewId,
  openedBy: c.openedBy,
  against: c.against,
  myRole: c.openedBy.id === MOCK_ME ? 'Opener' : 'Against',
  description: c.description,
  fromCall: c.fromCall,
  createdAt: c.createdAt,
  evidenceMessageIds: c.evidenceMessageIds,
  decision: c.decision,
  alsoWarned: c.alsoWarned,
  decisionReason: c.decisionReason,
  resolvedAt: c.resolvedAt,
  responseDeadlineAt: c.responseDeadlineAt,
  canWithdraw: c.openedBy.id === MOCK_ME && c.status === 'Open',
  pendingQuestions: (mockExplanations.get(c.id) ?? []).filter((x) => x.kind === 'Question' && !x.answered).length,
  canAppeal: c.status === 'Resolved' && !c.appeal,
  appeal: c.appeal
    ? { appellantId: c.appeal.appellantId, reason: c.appeal.reason, filedAt: c.appeal.filedAt, reviewInProgress: c.status === 'AppealReview', outcome: c.appeal.outcome, finalDecision: c.appeal.finalDecision, decidedAt: c.appeal.decidedAt }
    : null,
})
const mockAdmin = (c: ModCase): AdminCase => ({
  dueAt: c.dueAt,
  isOverdue: c.status === 'Open' && new Date(c.dueAt).getTime() < Date.now(),
  id: c.id,
  type: c.type,
  category: c.category,
  status: c.status,
  priority: c.priority,
  exchangeId: c.exchangeId,
  reviewId: c.reviewId,
  openedBy: c.openedBy,
  against: c.against,
  description: c.description,
  fromCall: c.fromCall,
  createdAt: c.createdAt,
  moderatorId: c.moderatorId,
  decision: c.decision,
  alsoWarned: c.alsoWarned,
  decisionReason: c.decisionReason,
  resolvedAt: c.resolvedAt,
  closedAt: c.status === 'Closed' ? c.resolvedAt : null,
  appeal: c.appeal,
})
const findMock = (id: string): ModCase => {
  const c = mockCases.find((x) => x.id === id)
  if (!c) throw new ApiError('case_not_found', 404)
  return c
}

async function http<T>(call: () => Promise<{ data: T }>): Promise<T> {
  try {
    return (await call()).data
  } catch (e) {
    throw toApiError(e)
  }
}

// ───────── Участник: подача и ход обращения ─────────
export async function openCase(input: OpenCaseInput): Promise<CaseItem> {
  if (USE_MOCKS) {
    await sleep(400)
    const ex = db.exchanges.find((e) => e.id === input.exchangeId)
    if (!ex) throw new ApiError('exchange_not_found', 404)
    if (input.description.trim().length < 20) throw new ApiError('case_description_too_short', 400)
    if (mockCases.some((c) => c.openedBy.id === MOCK_ME && c.exchangeId === input.exchangeId && c.type === input.type && !['Withdrawn', 'Closed', 'Resolved'].includes(c.status))) {
      throw new ApiError('case_already_open', 409)
    }
    const c: ModCase = {
      id: `c${++mockSeq}`,
      type: input.type,
      category: input.category,
      status: 'Open',
      priority: input.type === 'Conduct' || input.fromCall ? 'High' : 'Normal',
      exchangeId: input.exchangeId,
      reviewId: input.reviewId ?? null,
      openedBy: person(MOCK_ME, 'Вы'),
      against: person(ex.partner.id, ex.partner.name),
      description: input.description.trim(),
      fromCall: !!input.fromCall,
      createdAt: iso(),
      dueAt: iso(24 * HOUR),
      isOverdue: false,
      moderatorId: null,
      isMine: false,
      hasChatAccess: false,
      evidenceMessageIds: input.messageIds ?? [],
      review: null,
      decision: null,
      alsoWarned: false,
      decisionReason: null,
      resolvedAt: null,
      responseDeadlineAt: null,
      pendingQuestions: 0,
      appeal: null,
      isAppealReviewer: false,
    }
    mockCases.unshift(c)
    if (input.fromCall) ex.status = 'Frozen'
    return mockUser(c)
  }
  return http(() => api.post<CaseItem>('/cases', { ...input, reviewId: input.reviewId ?? null, messageIds: input.messageIds ?? [], fromCall: !!input.fromCall }))
}

export async function getMyCases(role: 'All' | 'Opened' | 'Against' = 'All'): Promise<CaseItem[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockCases
      .map(mockUser)
      .filter((c) => c.openedBy.id === MOCK_ME || c.against.id === MOCK_ME)
      .filter((c) => role === 'All' || (role === 'Opened' ? c.myRole === 'Opener' : c.myRole === 'Against'))
  }
  return (await http(() => api.get<CaseList<CaseItem>>('/me/cases', { params: { role, page: 1, pageSize: 50 } }))).items
}

export async function getCase(id: string): Promise<CaseItem> {
  if (USE_MOCKS) {
    await sleep()
    return mockUser(findMock(id))
  }
  return http(() => api.get<CaseItem>(`/cases/${id}`))
}

export async function withdrawCase(id: string): Promise<CaseItem> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'Open') throw new ApiError('invalid_state_transition', 409)
    c.status = 'Withdrawn'
    return mockUser(c)
  }
  return http(() => api.post<CaseItem>(`/cases/${id}/withdraw`))
}

export async function getMyExplanations(id: string): Promise<ExplanationThread> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    return { caseId: id, status: c.status, responseDeadlineAt: c.responseDeadlineAt, items: mockExplanations.get(id) ?? [] }
  }
  return http(() => api.get<ExplanationThread>(`/cases/${id}/explanations`))
}

export async function answerExplanation({ id, questionId, text }: { id: string; questionId: string; text: string }): Promise<ExplanationThread> {
  if (USE_MOCKS) {
    await sleep()
    const list = mockExplanations.get(id) ?? []
    const q = list.find((x) => x.id === questionId)
    if (!q) throw new ApiError('question_not_found', 404)
    q.answered = true
    list.push({ id: `x${++mockSeq}`, kind: 'Answer', senderId: MOCK_ME, recipientId: q.senderId, text, inReplyToId: q.id, answered: false, createdAt: iso() })
    const c = findMock(id)
    if (!list.some((x) => x.kind === 'Question' && !x.answered)) {
      c.status = 'InProgress'
      c.responseDeadlineAt = null
    }
    return { caseId: id, status: c.status, responseDeadlineAt: c.responseDeadlineAt, items: list }
  }
  return http(() => api.post<ExplanationThread>(`/cases/${id}/explanations`, { questionId, text }))
}

export async function appealCase({ id, reason }: { id: string; reason: string }): Promise<CaseItem> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'Resolved') throw new ApiError('invalid_state_transition', 409)
    if (reason.trim().length < 20) throw new ApiError('appeal_reason_too_short', 400)
    c.status = 'Appealed'
    c.appeal = { appellantId: MOCK_ME, reason: reason.trim(), filedAt: iso(), adminId: null, reviewerModeratorId: null, reviewerConclusion: null, outcome: null, finalDecision: null, decidedAt: null }
    return mockUser(c)
  }
  return http(() => api.post<CaseItem>(`/cases/${id}/appeal`, { reason }))
}

// ───────── Модератор ─────────
export type ModScope = 'Queue' | 'Mine' | 'Appeals'

// Поиск q выполняет сервер: часть имени или почты участников либо текста обращения, без учёта регистра (до 100 символов)
const mockMatches = (q: string | undefined, ...fields: string[]) => !q?.trim() || fields.some((f) => f.toLowerCase().includes(q.trim().toLowerCase()))

export async function getModCases(scope: ModScope, q?: string): Promise<ModCase[]> {
  if (USE_MOCKS) {
    await sleep()
    const found = (c: ModCase) => mockMatches(q, c.description, c.openedBy.name, c.against.name)
    if (scope === 'Queue') return mockCases.filter((c) => c.status === 'Open' && c.openedBy.id !== MOCK_ME && found(c))
    if (scope === 'Mine') return mockCases.filter((c) => c.isMine && !['Closed', 'Withdrawn'].includes(c.status) && found(c))
    return mockCases.filter((c) => c.isAppealReviewer && found(c))
  }
  return (await http(() => api.get<CaseList<ModCase>>('/moderation/cases', { params: { scope, q: q?.trim() || undefined, page: 1, pageSize: 50 } }))).items
}

export async function getModCase(id: string): Promise<ModCase> {
  if (USE_MOCKS) {
    await sleep()
    return findMock(id)
  }
  return http(() => api.get<ModCase>(`/moderation/cases/${id}`))
}

export async function takeCase(id: string): Promise<ModCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'Open') throw new ApiError('invalid_state_transition', 409)
    c.status = 'InProgress'
    c.isMine = true
    c.moderatorId = 'mod'
    c.hasChatAccess = true
    return c
  }
  return http(() => api.post<ModCase>(`/moderation/cases/${id}/take`))
}

export async function getCaseChat({ id, before }: { id: string; before?: string | null }): Promise<ModChatPage> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (!c.hasChatAccess) throw new ApiError('chat_access_denied', 403)
    const mk = (n: number, from: CasePerson, text: string, evidence = false): ModMessage => ({
      id: `${id}-m${n}`,
      senderId: from.id,
      type: 'Text',
      text,
      paymentWarning: false,
      createdAt: iso(-(10 - n) * HOUR),
      editedAt: null,
      deletedAt: null,
      readAt: null,
      isEvidence: evidence,
      revisions: [],
    })
    return {
      caseId: id,
      exchangeId: c.exchangeId,
      userA: c.openedBy,
      userB: c.against,
      nextBefore: null,
      items: [
        mk(1, c.openedBy, 'Привет! Давай договоримся о времени урока.'),
        mk(2, c.against, 'Хорошо, завтра вечером подойдёт.'),
        mk(3, c.against, 'Ты вообще понимаешь, что говоришь? Слушай, когда с тобой разговаривают.', true),
        { ...mk(4, c.against, ''), text: 'Прости, не хотел обидеть', deletedAt: iso(-2 * HOUR), revisions: [{ text: 'Сам виноват', changedAt: iso(-3 * HOUR) }] },
      ],
    }
  }
  return http(() => api.get<ModChatPage>(`/moderation/cases/${id}/chat`, { params: { before: before ?? undefined, limit: 100 } }))
}

export async function askQuestion({ id, recipient, text }: { id: string; recipient: 'Opener' | 'Against' | 'Both'; text: string }): Promise<ExplanationThread> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'InProgress') throw new ApiError('invalid_state_transition', 409)
    const list = mockExplanations.get(id) ?? []
    const targets = recipient === 'Both' ? [c.openedBy.id, c.against.id] : [recipient === 'Opener' ? c.openedBy.id : c.against.id]
    for (const to of targets) list.push({ id: `x${++mockSeq}`, kind: 'Question', senderId: 'mod', recipientId: to, text, inReplyToId: null, answered: false, createdAt: iso() })
    mockExplanations.set(id, list)
    c.status = 'WaitingResponse'
    c.responseDeadlineAt = iso(48 * HOUR)
    c.pendingQuestions = targets.length
    return { caseId: id, status: c.status, responseDeadlineAt: c.responseDeadlineAt, items: list }
  }
  return http(() => api.post<ExplanationThread>(`/moderation/cases/${id}/questions`, { recipient, text }))
}

export async function getModExplanations(id: string): Promise<ExplanationThread> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    return { caseId: id, status: c.status, responseDeadlineAt: c.responseDeadlineAt, items: mockExplanations.get(id) ?? [] }
  }
  return http(() => api.get<ExplanationThread>(`/moderation/cases/${id}/explanations`))
}

export async function resolveCase({ id, decision, alsoWarned, reason }: { id: string; decision: CaseDecision; alsoWarned: boolean; reason: string }): Promise<ModCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status === 'WaitingResponse') throw new ApiError('waiting_for_explanations', 409)
    if (c.status !== 'InProgress') throw new ApiError('invalid_state_transition', 409)
    if (reason.trim().length < 20) throw new ApiError('case_reason_too_short', 400)
    c.status = 'Resolved'
    c.decision = decision
    c.alsoWarned = alsoWarned
    c.decisionReason = reason.trim()
    c.resolvedAt = iso()
    c.hasChatAccess = false
    return c
  }
  return http(() => api.post<ModCase>(`/moderation/cases/${id}/resolve`, { decision, alsoWarned, reason }))
}

export async function setExchangeFrozen({ id, frozen }: { id: string; frozen: boolean }): Promise<{ exchangeId: string; status: string }> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (frozen) mockFrozen.add(id)
    else mockFrozen.delete(id)
    return { exchangeId: c.exchangeId, status: frozen ? 'Frozen' : 'Active' }
  }
  return http(() => api.post(`/moderation/cases/${id}/${frozen ? 'freeze-exchange' : 'unfreeze-exchange'}`))
}

export async function stopWaiting({ id, reason }: { id: string; reason: string }): Promise<ModCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'WaitingResponse') throw new ApiError('invalid_state_transition', 409)
    c.status = 'InProgress'
    c.responseDeadlineAt = null
    return c
  }
  return http(() => api.post<ModCase>(`/moderation/cases/${id}/stop-waiting`, { reason }))
}

export async function sendAppealConclusion({ id, conclusion }: { id: string; conclusion: string }): Promise<ModCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.appeal) c.appeal.reviewerConclusion = conclusion
    c.status = 'Appealed'
    c.hasChatAccess = false
    return c
  }
  return http(() => api.post<ModCase>(`/moderation/cases/${id}/appeal-conclusion`, { conclusion }))
}

// ───────── Админ ─────────
export async function getAdminCases(status?: CaseStatus | '', type?: CaseType | '', q?: string): Promise<AdminCase[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockCases.map(mockAdmin).filter((c) => (!status || c.status === status) && (!type || c.type === type) && mockMatches(q, c.description, c.openedBy.name, c.against.name))
  }
  return (await http(() => api.get<CaseList<AdminCase>>('/admin/cases', { params: { status: status || undefined, type: type || undefined, q: q?.trim() || undefined, page: 1, pageSize: 50 } }))).items
}

export async function getAdminCase(id: string): Promise<AdminCaseDetail> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    return { case: mockAdmin(c), review: c.review, evidence: [], explanations: mockExplanations.get(id) ?? [], warnings: [], banRequests: [] }
  }
  return http(() => api.get<AdminCaseDetail>(`/admin/cases/${id}`))
}

export async function getModerators(): Promise<{ id: string; name: string }[]> {
  if (USE_MOCKS) return [{ id: 'mod2', name: 'Модератор Алишер' }, { id: 'mod3', name: 'Модератор Зарина' }]
  return http(() => api.get<{ id: string; name: string }[]>('/admin/moderators'))
}

export async function assignReviewer({ id, moderatorId }: { id: string; moderatorId: string }): Promise<AdminCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'Appealed') throw new ApiError('invalid_state_transition', 409)
    c.status = 'AppealReview'
    if (c.appeal) c.appeal.reviewerModeratorId = moderatorId
    return mockAdmin(c)
  }
  return http(() => api.post<AdminCase>(`/admin/cases/${id}/appeal/reviewer`, { moderatorId }))
}

export interface DecideAppealInput {
  id: string
  outcome: AppealOutcome
  newDecision?: CaseDecision | null
  alsoWarned?: boolean
  reason?: string
}
export async function decideAppeal({ id, outcome, newDecision, alsoWarned, reason }: DecideAppealInput): Promise<AdminCase> {
  if (USE_MOCKS) {
    await sleep()
    const c = findMock(id)
    if (c.status !== 'Appealed') throw new ApiError('invalid_state_transition', 409)
    c.status = 'Closed'
    if (c.appeal) {
      c.appeal.outcome = outcome
      c.appeal.finalDecision = outcome === 'Changed' ? (newDecision ?? null) : c.decision
      c.appeal.decidedAt = iso()
    }
    return mockAdmin(c)
  }
  return http(() => api.post<AdminCase>(`/admin/cases/${id}/appeal/decision`, { outcome, newDecision: outcome === 'Changed' ? newDecision : null, alsoWarned: !!alsoWarned, reason: reason || null }))
}

const mockBans: BanRequest[] = [
  { id: 'b1', userId: 'u3', userName: 'Rustam', caseId: 'c1', requestedById: 'mod', reason: 'Три предупреждения за 12 месяцев', status: 'Pending', decidedById: null, decidedAt: null, createdAt: iso(-20 * HOUR) },
]
export async function getBanRequests(status?: BanRequestStatus, q?: string): Promise<BanRequest[]> {
  if (USE_MOCKS) {
    await sleep()
    return mockBans.filter((b) => (!status || b.status === status) && mockMatches(q, b.userName, b.reason))
  }
  return (await http(() => api.get<CaseList<BanRequest>>('/admin/ban-requests', { params: { status, q: q?.trim() || undefined, page: 1, pageSize: 50 } }))).items
}

export async function decideBan({ id, approve }: { id: string; approve: boolean }): Promise<BanRequest> {
  if (USE_MOCKS) {
    await sleep()
    const b = mockBans.find((x) => x.id === id)
    if (!b) throw new ApiError('ban_request_not_found', 404)
    if (b.status !== 'Pending') throw new ApiError('invalid_state_transition', 409)
    b.status = approve ? 'Approved' : 'Rejected'
    b.decidedAt = iso()
    return { ...b }
  }
  return http(() => api.post<BanRequest>(`/admin/ban-requests/${id}/${approve ? 'approve' : 'reject'}`))
}
