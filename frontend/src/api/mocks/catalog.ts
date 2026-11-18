import type { CatalogSkill, LanguageOption, LocalizedName, SkillCategory } from '../types'

// Тестовый каталог. Удалим, когда подключим настоящий бэкенд.
const n = (ru: string, en = ru, tg = ru): LocalizedName => ({ ru, en, tg })

export const MOCK_CATEGORIES: SkillCategory[] = [
  { id: 'it', names: n('IT', 'IT', 'IT') },
  { id: 'lang', names: n('Иностранные языки', 'Foreign languages', 'Забонҳои хориҷӣ') },
  { id: 'design', names: n('Дизайн', 'Design', 'Дизайн') },
]

const sk = (id: string, categoryId: string, names: LocalizedName): CatalogSkill => ({ id, categoryId, names })

export const MOCK_SKILLS: CatalogSkill[] = [
  sk('csharp', 'it', n('C#')),
  sk('python', 'it', n('Python')),
  sk('react', 'it', n('React')),
  sk('typescript', 'it', n('TypeScript')),
  sk('sql', 'it', n('SQL')),
  sk('english', 'lang', n('Английский язык', 'English', 'Забони англисӣ')),
  sk('german', 'lang', n('Немецкий язык', 'German', 'Забони олмонӣ')),
  sk('turkish', 'lang', n('Турецкий язык', 'Turkish', 'Забони туркӣ')),
  sk('uiux', 'design', n('UI/UX дизайн', 'UI/UX design', 'Тарроҳии UI/UX')),
  sk('figma', 'design', n('Figma')),
  sk('photoshop', 'design', n('Photoshop')),
]

export const MOCK_LANGUAGES: LanguageOption[] = [
  { code: 'tg', names: n('Таджикский', 'Tajik', 'Тоҷикӣ') },
  { code: 'ru', names: n('Русский', 'Russian', 'Русӣ') },
  { code: 'en', names: n('Английский', 'English', 'Англисӣ') },
]
