using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacePageOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Workspaces_Name",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Workspaces_Name", x => new { x.NodeId, x.Name, x.RowKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Workspaces_Name_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Workspaces_Name",
                column: "RowKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Workspaces_Name",
                schema: "dbo");
        }
    }
}
