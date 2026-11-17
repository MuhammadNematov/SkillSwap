using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TwoFactorHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TwoFactorLastUsedStep",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TwoFactorRecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwoFactorRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TwoFactorRecoveryCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TwoFactorRecoveryCodes_CodeHash",
                table: "TwoFactorRecoveryCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TwoFactorRecoveryCodes_UserId",
                table: "TwoFactorRecoveryCodes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TwoFactorRecoveryCodes");

            migrationBuilder.DropColumn(
                name: "TwoFactorLastUsedStep",
                table: "Users");
        }
    }
}
