using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromTeachesSkillId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToTeachesSkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationDays = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRequests", x => x.Id);
                    table.CheckConstraint("CK_ExchangeRequests_Duration", "\"DurationDays\" IN (30, 60)");
                    table.CheckConstraint("CK_ExchangeRequests_NotSelf", "\"FromUserId\" <> \"ToUserId\"");
                    table.ForeignKey(
                        name: "FK_ExchangeRequests_Skills_FromTeachesSkillId",
                        column: x => x.FromTeachesSkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeRequests_Skills_ToTeachesSkillId",
                        column: x => x.ToTeachesSkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeRequests_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExchangeRequests_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Key", "CreatedAt", "Description", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { "Requests.DeclineCooldownDays", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "Антиспам: через сколько дней после отказа можно снова отправить заявку тому же человеку (раздел 5.3 ТЗ).", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "30" },
                    { "Requests.MaxActiveExchanges", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "Антиспам: не более стольких активных обменов одновременно (раздел 5.3 ТЗ).", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "3" },
                    { "Requests.MaxPendingOutgoing", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "Антиспам: не более стольких ожидающих исходящих заявок (раздел 5.3 ТЗ).", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "5" },
                    { "Requests.MaxPerDay", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "Антиспам: не более стольких новых заявок за сутки (раздел 5.3 ТЗ).", new DateTime(2026, 11, 19, 0, 0, 0, 0, DateTimeKind.Utc), "10" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRequests_FromTeachesSkillId",
                table: "ExchangeRequests",
                column: "FromTeachesSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRequests_FromUserId_Status_CreatedAt",
                table: "ExchangeRequests",
                columns: new[] { "FromUserId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRequests_ToTeachesSkillId",
                table: "ExchangeRequests",
                column: "ToTeachesSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRequests_ToUserId_Status_CreatedAt",
                table: "ExchangeRequests",
                columns: new[] { "ToUserId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRequests");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "Requests.DeclineCooldownDays");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "Requests.MaxActiveExchanges");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "Requests.MaxPendingOutgoing");

            migrationBuilder.DeleteData(
                table: "SystemSettings",
                keyColumn: "Key",
                keyValue: "Requests.MaxPerDay");
        }
    }
}
