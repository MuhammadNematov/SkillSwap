namespace SkillSwap.Infrastructure.Persistence.Seed;

/// <summary>
/// Стартовый каталог (раздел 5.2 ТЗ): 3 категории и небольшой набор навыков, чтобы подбор можно было проверять сразу.
/// Дальше каталог ведёт админ (/admin/skills), без изменения кода. Записи создаются миграцией один раз и дальше принадлежат админу.
/// Названия на таджикском написаны без вычитки носителем (раздел 16.2 ТЗ): перед пилотом их нужно проверить.
/// </summary>
public static class SkillCatalogSeed
{
    private static readonly DateTime SeedTime = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    public static Guid CategoryId(int n) => new($"c0000000-0000-0000-0000-{n:x12}");
    public static Guid SkillId(int n) => new($"50000000-0000-0000-0000-{n:x12}");

    public static readonly Guid It = CategoryId(1);
    public static readonly Guid Languages = CategoryId(2);
    public static readonly Guid Design = CategoryId(3);

    public static object[] Categories =>
    [
        Category(1, "Технологияҳои иттилоотӣ", "IT", "IT"),
        Category(2, "Забонҳои хориҷӣ", "Иностранные языки", "Foreign languages"),
        Category(3, "Дизайн", "Дизайн", "Design"),
    ];

    public static object[] Skills
    {
        get
        {
            var list = new List<object>();
            var n = 0;
            void Add(Guid category, string tg, string ru, string en, params string[] synonyms) =>
                list.Add(new
                {
                    Id = SkillId(++n),
                    CategoryId = category,
                    NameTg = tg,
                    NameRu = ru,
                    NameEn = en,
                    Synonyms = synonyms,
                    IsActive = true,
                    CreatedAt = SeedTime,
                    UpdatedAt = SeedTime,
                });

            // IT
            Add(It, "Python", "Python", "Python", "питон", "пайтон");
            Add(It, "JavaScript", "JavaScript", "JavaScript", "js", "джаваскрипт", "жс");
            Add(It, "C#", "C#", "C#", "си шарп", "сишарп", "csharp", "c sharp");
            Add(It, "Java", "Java", "Java", "джава");
            Add(It, "SQL ва пойгоҳи маълумот", "SQL и базы данных", "SQL and databases", "sql", "бд", "database", "postgres", "mysql");
            Add(It, "HTML ва CSS", "HTML и CSS", "HTML and CSS", "html", "css", "верстка", "вёрстка");
            Add(It, "Git", "Git", "Git", "гит", "version control");
            Add(It, "React", "React", "React", "реакт");
            Add(It, "Таҳлили маълумот", "Анализ данных", "Data analysis", "excel", "pandas", "аналитика");
            Add(It, "Асосҳои омӯзиши мошинӣ", "Основы машинного обучения", "Machine learning basics", "ml", "ии", "ai", "нейросети");
            Add(It, "Санҷиши барномаҳо", "Тестирование ПО", "Software testing", "qa", "тестировщик");

            // Иностранные языки
            Add(Languages, "Забони англисӣ", "Английский язык", "English", "english", "англ");
            Add(Languages, "Забони русӣ", "Русский язык", "Russian", "russian", "русӣ");
            Add(Languages, "Забони тоҷикӣ", "Таджикский язык", "Tajik", "tajik", "тоҷикӣ");
            Add(Languages, "Забони олмонӣ", "Немецкий язык", "German", "deutsch", "german");
            Add(Languages, "Забони фаронсавӣ", "Французский язык", "French", "français", "francais");
            Add(Languages, "Забони хитоӣ", "Китайский язык", "Chinese", "mandarin", "中文");
            Add(Languages, "Забони арабӣ", "Арабский язык", "Arabic", "arabic");
            Add(Languages, "Забони туркӣ", "Турецкий язык", "Turkish", "türkçe", "turkce");
            Add(Languages, "Забони форсӣ", "Персидский язык", "Persian", "farsi", "фарси");
            Add(Languages, "Забони испанӣ", "Испанский язык", "Spanish", "español", "espanol");

            // Дизайн
            Add(Design, "Дизайни UI/UX", "UI/UX-дизайн", "UI/UX design", "ui", "ux", "интерфейсы");
            Add(Design, "Дизайни графикӣ", "Графический дизайн", "Graphic design", "graphic");
            Add(Design, "Figma", "Figma", "Figma", "фигма");
            Add(Design, "Adobe Photoshop", "Adobe Photoshop", "Adobe Photoshop", "фотошоп", "photoshop");
            Add(Design, "Adobe Illustrator", "Adobe Illustrator", "Adobe Illustrator", "иллюстратор", "illustrator");
            Add(Design, "Таҳрири видео", "Видеомонтаж", "Video editing", "монтаж", "premiere", "davinci");
            Add(Design, "Моделсозии 3D", "3D-моделирование", "3D modeling", "blender", "блендер");
            Add(Design, "Типографика", "Типографика", "Typography", "шрифты", "fonts");

            return [.. list];
        }
    }

    private static object Category(int n, string tg, string ru, string en) => new
    {
        Id = CategoryId(n),
        NameTg = tg,
        NameRu = ru,
        NameEn = en,
        SortOrder = n,
        IsActive = true,
        CreatedAt = SeedTime,
        UpdatedAt = SeedTime,
    };
}
