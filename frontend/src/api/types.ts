// Типы пишем вручную. Когда бэкенд выложит Swagger — сверим и поправим.

export type Role = 'User' | 'Moderator' | 'Admin' | 'SuperAdmin'

export type UiLanguage = 'tg' | 'ru' | 'en'

export interface CurrentUser {
  id: string
  email: string
  name: string
  role: Role
  uiLanguage: UiLanguage
  avatarUrl?: string | null
  // false — анкета (онбординг) ещё не заполнена: нужен минимум 1 навык в «Умею» и 1 в «Хочу изучать»
  skillsComplete: boolean
  twoFactorEnabled?: boolean
}

export interface AuthSession {
  accessToken: string
  user: CurrentUser
}

// ───────── Навыки, подбор, профиль ─────────

export type SkillLevel = 'Beginner' | 'Intermediate' | 'Advanced'

// Полное совпадение: я умею то, что хочет партнёр, и наоборот. Частичное — только в одну сторону.
export type MatchType = 'full' | 'partial'

export interface Skill {
  id: string
  name: string
  category: string
}

export interface UserSkill {
  skillId: string
  name: string
  level: SkillLevel
  description?: string
}

export interface UserSummary {
  id: string
  name: string
  avatarUrl?: string | null
  ratingAvg: number
  ratingCount: number // меньше 3 отзывов — показываем «Новичок»
  languages: string[] // коды языков занятий (ISO 639-1)
  timeZone: string // IANA, например Asia/Dushanbe
}

export interface MatchItem {
  user: UserSummary
  matchType: MatchType
  teaches: UserSkill[] // навыки партнёра, которые я хочу изучать
}

export interface MatchFilters {
  q?: string
  skill?: string
  lang?: string
  level?: SkillLevel
  minRating?: number
}

export interface Review {
  id: string
  authorName: string
  score: number // среднее по четырём критериям
  text: string
  createdAt: string
  isFullExchange: boolean
}

export interface UserProfile extends UserSummary {
  bio?: string | null
  completedExchanges: number
  teach: UserSkill[]
  learn: UserSkill[]
  reviews: Review[]
  isMe: boolean
}

export interface ExchangeRequestInput {
  toUserId: string
  fromTeachesSkillId?: string // при частичном совпадении свой навык можно предложить в тексте
  toTeachesSkillId: string
  durationDays: 30 | 60
  message: string
}

// ───────── Заявки, обмены, уроки, чат ─────────

export type ExchangeStatus = 'Active' | 'Completed' | 'Aborted' | 'Frozen'
export type RequestStatus = 'Pending' | 'Accepted' | 'Declined' | 'Withdrawn' | 'Expired'
export type SessionStatus =
  | 'Proposed'
  | 'Scheduled'
  | 'Declined'
  | 'Expired'
  | 'Cancelled'
  | 'LateCancelled'
  | 'InProgress'
  | 'Missed'
  | 'NoShow'
  | 'Completed'
  | 'Unconfirmed'
  | 'NotCounted'
export type MessageType = 'Text' | 'Voice' | 'File' | 'SessionCard' | 'System'

// Заявка с точки зрения текущего пользователя
export interface ExchangeRequest {
  id: string
  direction: 'in' | 'out'
  partner: UserSummary
  mySkill?: string // чему учу я (может быть не указано — «в сообщении»)
  partnerSkill: string // чему учит партнёр
  durationDays: 30 | 60
  message: string
  status: RequestStatus
  createdAt: string
  expiresAt: string
  exchangeId?: string | null // после принятия
  needsPartnerSkill?: boolean // отправитель не указал навык: при принятии выбираем из его «Умею»
}

export type ChatMode = 'ReadWrite' | 'ReadOnly' | 'Archived'

export interface Exchange {
  id: string
  partner: UserSummary
  mySkill: string
  partnerSkill: string
  startDate: string
  endDate: string
  status: ExchangeStatus
  isExtended: boolean
  myLessons: number // сколько уроков провёл я как преподаватель
  partnerLessons: number
  completedByMe: boolean
  completedByPartner: boolean
  abortReason?: string
  abortedBy?: 'me' | 'partner' | null // null у закрытого системой (блокировка, модератор)
  closedAt?: string | null
  frozenAt?: string | null
  myExtensionAccepted?: boolean
  partnerExtensionAccepted?: boolean
  showBalanceReminder?: boolean // разница уроков больше 3
  unreadMessages?: number
  chat?: { mode: ChatMode; readOnlyReason?: string | null; readableUntil?: string | null }
  // Состояние отзыва по этому обмену (поле уточним по Swagger)
  review?: { canWrite: boolean; submitted: boolean; deadlineAt?: string }
}

export interface LessonSession {
  id: string
  exchangeId: string
  partnerName: string
  partnerTimeZone: string
  teacherIsMe: boolean
  startAt: string // UTC, ISO 8601
  durationMin: number // 45
  topic?: string
  status: SessionStatus
  proposedByMe: boolean
  endAt?: string
  confirmedByMe?: boolean // нажал «Урок состоялся»
  confirmDeadlineAt?: string | null
  previousStartAt?: string | null
  noShowIsMe?: boolean | null // неявка: я не пришёл (true), партнёр не пришёл (false), нет неявки (null)
  lateJoinMinutes?: number | null // на сколько минут опоздал второй участник, если урок засчитан
}

export interface ChatMessage {
  id: string
  exchangeId: string
  senderIsMe: boolean
  type: MessageType
  text?: string
  createdAt: string
  sessionId?: string // для карточки слота
  fileName?: string
  durationSec?: number // для голосовых
  attachmentId?: string
  paymentWarning?: boolean // в тексте карта или призыв к оплате: показываем предупреждение
  readAt?: string | null
  deleted?: boolean
}

export interface Conversation {
  exchange: Exchange
  last?: ChatMessage
}

// ───────── Анкета и каталог навыков (формы ответов уточним по Swagger) ─────────

export type LocalizedName = Record<UiLanguage, string>

export interface SkillCategory {
  id: string
  names: LocalizedName
}

export interface CatalogSkill {
  id: string
  categoryId: string
  names: LocalizedName
}

export interface CatalogQuery {
  category?: string
  q?: string
  lang: UiLanguage
  page: number
  pageSize: number
}

export interface CatalogPage {
  items: CatalogSkill[]
  total: number
}

export interface LanguageOption {
  code: string
  names: LocalizedName
}

export interface TeachEntry {
  skillId: string
  level: SkillLevel
  description: string
  names?: LocalizedName // приходят из GET /me/skills
  isActive?: boolean // false: админ выключил навык, предложим убрать
}
export interface LearnEntry {
  skillId: string
  level: SkillLevel
  names?: LocalizedName
  isActive?: boolean
}

// GET/PUT /me/skills (языки и часовой пояс лежат в профиле, PUT /me)
export interface MySkills {
  teach: TeachEntry[]
  learn: LearnEntry[]
  isComplete?: boolean
  isSearchable?: boolean
}

// GET /me
export interface MyProfile {
  id: string
  email: string
  role: Role
  name: string
  bio: string | null
  avatarUrl: string | null
  dateOfBirth: string | null
  uiLanguage: string
  timeZone: string
  isSearchable: boolean
  languages: string[]
  skillsComplete: boolean
  twoFactorEnabled: boolean
  ratingAvg: number | null
  ratingCount: number
  isNewcomer: boolean
}

// PUT /me заменяет поля целиком
export interface ProfileUpdate {
  name: string
  bio: string | null
  uiLanguage: string
  timeZone: string
  languages: string[]
  isSearchable: boolean
}

// ───────── 2FA и сеансы ─────────

export interface TwoFactorSetup {
  sharedKey: string
  authenticatorUri: string
}

export interface AuthSessionInfo {
  id: string
  deviceInfo: string | null
  ipAddress: string | null
  startedAt: string
  lastActiveAt: string
  expiresAt: string
  isCurrent: boolean
}

// ───────── Отзывы и уведомления (ТЗ 8.1, раздел 12) ─────────

export interface ReviewScores {
  knowledge: number // знание предмета
  explaining: number // умение объяснять
  punctuality: number // пунктуальность
  politeness: number // вежливость
}

export interface ReviewInput extends ReviewScores {
  exchangeId: string
  text: string // 20–1000 символов
}

export type NotificationType =
  | 'RequestReceived'
  | 'RequestAccepted'
  | 'RequestDeclined'
  | 'SessionProposed'
  | 'SessionConfirmed'
  | 'SessionReminder'
  | 'ReviewAvailable'
  | 'ReviewPublished'
  | 'CaseTaken'
  | string // неизвестные типы показываем общим текстом

export interface AppNotification {
  id: string
  type: NotificationType
  payload: Record<string, string | number | undefined> // partnerName, exchangeId, startAt…
  isRead: boolean
  createdAt: string
}
