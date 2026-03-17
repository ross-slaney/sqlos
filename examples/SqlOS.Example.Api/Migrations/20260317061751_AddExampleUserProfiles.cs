using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExampleUserProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExampleUserProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SqlOSUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefaultEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OrganizationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OrganizationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReferralSource = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleUserProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleUserProfiles_OrganizationId",
                table: "ExampleUserProfiles",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleUserProfiles_SqlOSUserId",
                table: "ExampleUserProfiles",
                column: "SqlOSUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExampleUserProfiles");
        }
    }
}
