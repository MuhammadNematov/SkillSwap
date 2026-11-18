using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExchangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeacherId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMin = table.Column<int>(type: "integer", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProposedById = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousStartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledById = table.Column<Guid>(type: "uuid", nullable: true),
                    NoShowUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LateJoinMinutes = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedByA = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmedByB = table.Column<bool>(type: "boolean", nullable: false),
                    UnconfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reminder24Sent = table.Column<bool>(type: "boolean", nullable: false),
                    Reminder15Sent = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.CheckConstraint("CK_Sessions_Duration", "\"DurationMin\" > 0");
                    table.CheckConstraint("CK_Sessions_Times", "\"EndAt\" > \"StartAt\"");
                    table.ForeignKey(
                        name: "FK_Sessions_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_ProposedById",
                        column: x => x.ProposedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Sessions_Users_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExchangeId_StartAt",
                table: "Sessions",
                columns: new[] { "ExchangeId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ProposedById",
                table: "Sessions",
                column: "ProposedById");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status_StartAt",
                table: "Sessions",
                columns: new[] { "Status", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TeacherId",
                table: "Sessions",
                column: "TeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
