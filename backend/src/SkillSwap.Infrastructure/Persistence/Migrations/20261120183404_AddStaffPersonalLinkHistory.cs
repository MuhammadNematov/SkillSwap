using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffPersonalLinkHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PersonalUnlinkRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalUnlinkRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalUnlinkRequests_Users_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonalUnlinkRequests_Users_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffPersonalLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonalUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UnlinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnlinkedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPersonalLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPersonalLinks_Users_PersonalUserId",
                        column: x => x.PersonalUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPersonalLinks_Users_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPersonalLinks_Users_UnlinkedById",
                        column: x => x.UnlinkedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalUnlinkRequests_DecidedById",
                table: "PersonalUnlinkRequests",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalUnlinkRequests_Pending_Staff",
                table: "PersonalUnlinkRequests",
                column: "StaffId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalUnlinkRequests_Status_CreatedAt",
                table: "PersonalUnlinkRequests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffPersonalLinks_Active_Personal",
                table: "StaffPersonalLinks",
                column: "PersonalUserId",
                unique: true,
                filter: "\"UnlinkedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPersonalLinks_Active_Staff",
                table: "StaffPersonalLinks",
                column: "StaffId",
                unique: true,
                filter: "\"UnlinkedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffPersonalLinks_UnlinkedById",
                table: "StaffPersonalLinks",
                column: "UnlinkedById");
            // Привязки, сделанные до появления истории (шаг 2 этапа 7), переносим в историю: конфликт интересов по ним не должен потеряться.
            migrationBuilder.Sql(
                """
                INSERT INTO "StaffPersonalLinks" ("Id", "CreatedAt", "UpdatedAt", "StaffId", "PersonalUserId", "LinkedAt", "UnlinkedAt", "UnlinkedById")
                SELECT gen_random_uuid(), now(), now(), "Id", "LinkedUserId", now(), NULL, NULL
                FROM "Users" WHERE "LinkedUserId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalUnlinkRequests");

            migrationBuilder.DropTable(
                name: "StaffPersonalLinks");
        }
    }
}
