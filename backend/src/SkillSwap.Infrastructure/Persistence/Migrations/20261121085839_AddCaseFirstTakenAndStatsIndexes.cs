using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseFirstTakenAndStatsIndexes : Migration
    {
        /// <summary>Заполнение FirstTakenAt и FirstTakenById у старых обращений из журнала аудита (доступно тестам).</summary>
        public const string BackfillSql = @"
UPDATE ""Cases"" AS c
SET ""FirstTakenAt"" = t.""CreatedAt"", ""FirstTakenById"" = t.""ActorId""
FROM (
    SELECT DISTINCT ON (""TargetId"") ""TargetId"", ""CreatedAt"", ""ActorId""
    FROM ""AuditLogs""
    WHERE ""Action"" = 'CaseTaken' AND ""TargetType"" = 'Case' AND ""TargetId"" IS NOT NULL
    ORDER BY ""TargetId"", ""CreatedAt"", ""Id""
) AS t
WHERE c.""Id"" = t.""TargetId"" AND c.""FirstTakenAt"" IS NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstTakenAt",
                table: "Cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FirstTakenById",
                table: "Cases",
                type: "uuid",
                nullable: true);

            // Старые обращения: первое взятие берётся из журнала аудита (самая ранняя запись CaseTaken по обращению). Журнал только добавляется, поэтому записи на месте.
            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateIndex(
                name: "IX_Users_CreatedAt",
                table: "Users",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Users_LastActiveAt",
                table: "Users",
                column: "LastActiveAt",
                filter: "\"LastActiveAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_StartAt",
                table: "Sessions",
                column: "StartAt");

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_ClosedAt_Status",
                table: "Exchanges",
                columns: new[] { "ClosedAt", "Status" },
                filter: "\"ClosedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_CreatedAt",
                table: "Exchanges",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_ClosedAt",
                table: "Cases",
                column: "ClosedAt",
                filter: "\"ClosedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CreatedAt",
                table: "Cases",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_FirstTakenById_FirstTakenAt",
                table: "Cases",
                columns: new[] { "FirstTakenById", "FirstTakenAt" },
                filter: "\"FirstTakenAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_OverdueMarkedAt",
                table: "Cases",
                column: "OverdueMarkedAt",
                filter: "\"OverdueMarkedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_ResolvedAt_Decision",
                table: "Cases",
                columns: new[] { "ResolvedAt", "Decision" },
                filter: "\"ResolvedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_CreatedAt",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_LastActiveAt",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_StartAt",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Exchanges_ClosedAt_Status",
                table: "Exchanges");

            migrationBuilder.DropIndex(
                name: "IX_Exchanges_CreatedAt",
                table: "Exchanges");

            migrationBuilder.DropIndex(
                name: "IX_Cases_ClosedAt",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_CreatedAt",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_FirstTakenById_FirstTakenAt",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_OverdueMarkedAt",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_ResolvedAt_Decision",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "FirstTakenAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "FirstTakenById",
                table: "Cases");
        }
    }
}
