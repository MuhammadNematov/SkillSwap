using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExchanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exchanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserBId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillAId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillBId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsExtended = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompletedByA = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedByB = table.Column<bool>(type: "boolean", nullable: false),
                    ExtensionAcceptedByA = table.Column<bool>(type: "boolean", nullable: false),
                    ExtensionAcceptedByB = table.Column<bool>(type: "boolean", nullable: false),
                    FrozenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AbortedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AbortReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exchanges", x => x.Id);
                    table.CheckConstraint("CK_Exchanges_Dates", "\"EndDate\" > \"StartDate\"");
                    table.CheckConstraint("CK_Exchanges_DifferentUsers", "\"UserAId\" <> \"UserBId\"");
                    table.ForeignKey(
                        name: "FK_Exchanges_ExchangeRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ExchangeRequests",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Exchanges_Skills_SkillAId",
                        column: x => x.SkillAId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Exchanges_Skills_SkillBId",
                        column: x => x.SkillBId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Exchanges_Users_UserAId",
                        column: x => x.UserAId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Exchanges_Users_UserBId",
                        column: x => x.UserBId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_RequestId",
                table: "Exchanges",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_SkillAId",
                table: "Exchanges",
                column: "SkillAId");

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_SkillBId",
                table: "Exchanges",
                column: "SkillBId");

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_Status_EndDate",
                table: "Exchanges",
                columns: new[] { "Status", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_UserAId_Status",
                table: "Exchanges",
                columns: new[] { "UserAId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_UserBId_Status",
                table: "Exchanges",
                columns: new[] { "UserBId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Exchanges");
        }
    }
}
