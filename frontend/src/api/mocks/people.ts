import type { MatchType, Review, Skill, UserProfile, UserSkill } from '../types'

// ───────── Тестовые данные для подбора и профилей. Удалим, когда бэкенд будет готов. ─────────

export const SKILLS: Skill[] = [
  { id: 'csharp', name: 'C#', category: 'IT' },
  { id: 'python', name: 'Python', category: 'IT' },
  { id: 'react', name: 'React', category: 'IT' },
  { id: 'typescript', name: 'TypeScript', category: 'IT' },
  { id: 'sql', name: 'SQL', category: 'IT' },
  { id: 'english', name: 'English', category: 'Languages' },
  { id: 'german', name: 'Deutsch', category: 'Languages' },
  { id: 'uiux', name: 'UI/UX', category: 'Design' },
  { id: 'figma', name: 'Figma', category: 'Design' },
  { id: 'photoshop', name: 'Photoshop', category: 'Design' },
]

const skillName = (id: string) => SKILLS.find((s) => s.id === id)?.name ?? id

const sk = (skillId: string, level: UserSkill['level'], description?: string): UserSkill => ({
  skillId,
  name: skillName(skillId),
  level,
  description,
})

const review = (id: string, authorName: string, score: number, text: string, isFullExchange = false): Review => ({
  id,
  authorName,
  score,
  text,
  createdAt: '2026-10-20T10:00:00Z',
  isFullExchange,
})

export const ME: UserProfile = {
  id: 'me-mock',
  name: 'Абубакр',
  avatarUrl: null,
  ratingAvg: 4.8,
  ratingCount: 24,
  languages: ['ru', 'tg', 'en'],
  timeZone: 'Asia/Dushanbe',
  bio: 'Full-stack разработчик. Люблю технологии и хочу учить других.',
  completedExchanges: 6,
  isMe: true,
  teach: [
    sk('csharp', 'Advanced', 'ASP.NET Core, Clean Architecture'),
    sk('python', 'Intermediate'),
    sk('sql', 'Intermediate'),
  ],
  learn: [sk('english', 'Beginner'), sk('uiux', 'Beginner'), sk('figma', 'Beginner')],
  reviews: [
    review('r1', 'Rustam', 5, 'Всё объясняет спокойно и по делу, уроки получились полезными.', true),
    review('r2', 'Madina', 4.5, 'Приходит подготовленным, всегда вовремя.'),
  ],
}

export interface MockPerson extends UserProfile {
  matchType: MatchType
}

function person(
  id: string,
  name: string,
  matchType: MatchType,
  ratingAvg: number,
  ratingCount: number,
  languages: string[],
  timeZone: string,
  teach: UserSkill[],
  learn: UserSkill[],
  completedExchanges: number,
  bio: string,
): MockPerson {
  return {
    id,
    name,
    matchType,
    ratingAvg,
    ratingCount,
    languages,
    timeZone,
    teach,
    learn,
    completedExchanges,
    bio,
    avatarUrl: null,
    isMe: false,
    reviews:
      ratingCount > 0
        ? [
            review(`${id}-r1`, 'Диёрбек', Math.min(5, ratingAvg + 0.1), 'Понятные объяснения, уроки проходят в хорошем темпе.', true),
            review(`${id}-r2`, 'Нигора', Math.max(1, ratingAvg - 0.3), 'Хороший партнёр, договариваться о времени легко.'),
          ]
        : [],
  }
}

export const PEOPLE: MockPerson[] = [
  person('u1', 'Sabrina', 'full', 4.8, 97, ['ru', 'en', 'tg'], 'Asia/Tashkent',
    [sk('english', 'Advanced', 'Разговорный и деловой английский'), sk('uiux', 'Intermediate')],
    [sk('csharp', 'Beginner')], 11, 'Преподаю английский, учусь программировать.'),
  person('u2', 'Rustam', 'full', 4.9, 128, ['ru', 'tg'], 'Asia/Dushanbe',
    [sk('figma', 'Advanced', 'Интерфейсы и прототипы'), sk('uiux', 'Advanced')],
    [sk('python', 'Beginner')], 24, 'Дизайнер интерфейсов. Хочу научиться автоматизировать работу на Python.'),
  person('u3', 'Madina', 'full', 4.6, 41, ['ru', 'en'], 'Europe/Moscow',
    [sk('english', 'Intermediate')],
    [sk('sql', 'Beginner')], 5, 'Аналитик, хочу уверенно писать запросы к базам данных.'),
  person('u7', 'Farrukh', 'full', 4.7, 76, ['ru', 'tg', 'en'], 'Asia/Dushanbe',
    [sk('figma', 'Intermediate'), sk('photoshop', 'Advanced')],
    [sk('python', 'Intermediate')], 9, 'Графический дизайнер, перехожу в разработку.'),
  person('u4', 'Timur', 'partial', 4.7, 33, ['ru', 'en'], 'Europe/Istanbul',
    [sk('english', 'Advanced')],
    [sk('react', 'Intermediate')], 7, 'Живу в Стамбуле, преподаю английский.'),
  person('u5', 'Dilnoza', 'partial', 5, 2, ['tg', 'ru'], 'Asia/Dushanbe',
    [sk('uiux', 'Beginner'), sk('figma', 'Intermediate')],
    [sk('german', 'Beginner')], 1, 'Недавно на платформе.'),
  person('u6', 'Alisher', 'partial', 4.4, 18, ['ru', 'tg'], 'Asia/Almaty',
    [sk('english', 'Intermediate')],
    [sk('typescript', 'Intermediate')], 3, 'Учу английский с детства, хочу освоить TypeScript.'),
]

export const findPerson = (id: string) => PEOPLE.find((p) => p.id === id)
