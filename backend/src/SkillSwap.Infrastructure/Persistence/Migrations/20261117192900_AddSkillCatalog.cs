using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkillCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NameTg = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameRu = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    NameTg = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameRu = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Synonyms = table.Column<string[]>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_SkillCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SkillCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "SkillCategories",
                columns: new[] { "Id", "CreatedAt", "IsActive", "NameEn", "NameRu", "NameTg", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "IT", "IT", "Технологияҳои иттилоотӣ", 1, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Foreign languages", "Иностранные языки", "Забонҳои хориҷӣ", 2, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Design", "Дизайн", "Дизайн", 3, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "Skills",
                columns: new[] { "Id", "CategoryId", "CreatedAt", "IsActive", "NameEn", "NameRu", "NameTg", "Synonyms", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("50000000-0000-0000-0000-000000000001"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Python", "Python", "Python", new[] { "питон", "пайтон" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000002"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "JavaScript", "JavaScript", "JavaScript", new[] { "js", "джаваскрипт", "жс" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000003"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "C#", "C#", "C#", new[] { "си шарп", "сишарп", "csharp", "c sharp" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000004"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Java", "Java", "Java", new[] { "джава" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000005"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "SQL and databases", "SQL и базы данных", "SQL ва пойгоҳи маълумот", new[] { "sql", "бд", "database", "postgres", "mysql" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000006"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "HTML and CSS", "HTML и CSS", "HTML ва CSS", new[] { "html", "css", "верстка", "вёрстка" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000007"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Git", "Git", "Git", new[] { "гит", "version control" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000008"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "React", "React", "React", new[] { "реакт" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000009"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Data analysis", "Анализ данных", "Таҳлили маълумот", new[] { "excel", "pandas", "аналитика" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000a"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Machine learning basics", "Основы машинного обучения", "Асосҳои омӯзиши мошинӣ", new[] { "ml", "ии", "ai", "нейросети" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000b"), new Guid("c0000000-0000-0000-0000-000000000001"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Software testing", "Тестирование ПО", "Санҷиши барномаҳо", new[] { "qa", "тестировщик" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000c"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "English", "Английский язык", "Забони англисӣ", new[] { "english", "англ" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000d"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Russian", "Русский язык", "Забони русӣ", new[] { "russian", "русӣ" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000e"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Tajik", "Таджикский язык", "Забони тоҷикӣ", new[] { "tajik", "тоҷикӣ" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000000f"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "German", "Немецкий язык", "Забони олмонӣ", new[] { "deutsch", "german" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000010"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "French", "Французский язык", "Забони фаронсавӣ", new[] { "français", "francais" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000011"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Chinese", "Китайский язык", "Забони хитоӣ", new[] { "mandarin", "中文" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000012"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Arabic", "Арабский язык", "Забони арабӣ", new[] { "arabic" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000013"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Turkish", "Турецкий язык", "Забони туркӣ", new[] { "türkçe", "turkce" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000014"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Persian", "Персидский язык", "Забони форсӣ", new[] { "farsi", "фарси" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000015"), new Guid("c0000000-0000-0000-0000-000000000002"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Spanish", "Испанский язык", "Забони испанӣ", new[] { "español", "espanol" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000016"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "UI/UX design", "UI/UX-дизайн", "Дизайни UI/UX", new[] { "ui", "ux", "интерфейсы" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000017"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Graphic design", "Графический дизайн", "Дизайни графикӣ", new[] { "graphic" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000018"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Figma", "Figma", "Figma", new[] { "фигма" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-000000000019"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Adobe Photoshop", "Adobe Photoshop", "Adobe Photoshop", new[] { "фотошоп", "photoshop" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000001a"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Adobe Illustrator", "Adobe Illustrator", "Adobe Illustrator", new[] { "иллюстратор", "illustrator" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000001b"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Video editing", "Видеомонтаж", "Таҳрири видео", new[] { "монтаж", "premiere", "davinci" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000001c"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "3D modeling", "3D-моделирование", "Моделсозии 3D", new[] { "blender", "блендер" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("50000000-0000-0000-0000-00000000001d"), new Guid("c0000000-0000-0000-0000-000000000003"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), true, "Typography", "Типографика", "Типографика", new[] { "шрифты", "fonts" }, new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillCategories_NameEn",
                table: "SkillCategories",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkillCategories_SortOrder",
                table: "SkillCategories",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_CategoryId_NameEn",
                table: "Skills",
                columns: new[] { "CategoryId", "NameEn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_Synonyms",
                table: "Skills",
                column: "Synonyms")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "SkillCategories");
        }
    }
}
