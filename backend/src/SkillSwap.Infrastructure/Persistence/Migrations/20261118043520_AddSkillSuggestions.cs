using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkillSwap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkillSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolvedSkillId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkillSuggestions_SkillCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SkillCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SkillSuggestions_Skills_ResolvedSkillId",
                        column: x => x.ResolvedSkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SkillSuggestions_Users_ResolvedById",
                        column: x => x.ResolvedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SkillSuggestions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_CategoryId",
                table: "SkillSuggestions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_ResolvedById",
                table: "SkillSuggestions",
                column: "ResolvedById");

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_ResolvedSkillId",
                table: "SkillSuggestions",
                column: "ResolvedSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_Status_CreatedAt",
                table: "SkillSuggestions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_UserId_CategoryId_NormalizedName",
                table: "SkillSuggestions",
                columns: new[] { "UserId", "CategoryId", "NormalizedName" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_SkillSuggestions_UserId_Status",
                table: "SkillSuggestions",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillSuggestions");
        }
    }
}
