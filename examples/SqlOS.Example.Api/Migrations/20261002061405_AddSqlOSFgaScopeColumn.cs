using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlOSFgaScopeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "SqlOSFgaScope",
                table: "Workspaces",
                type: "varbinary(512)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SqlOSFgaScope",
                table: "Locations",
                type: "varbinary(512)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SqlOSFgaScope",
                table: "InventoryItems",
                type: "varbinary(512)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SqlOSFgaScope",
                table: "Chains",
                type: "varbinary(512)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_SqlOSFgaResourceId",
                table: "Workspaces",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_SqlOSFgaResourceId",
                table: "Locations",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_SqlOSFgaResourceId",
                table: "InventoryItems",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Chains_SqlOSFgaResourceId",
                table: "Chains",
                column: "ResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workspaces_SqlOSFgaResourceId",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Locations_SqlOSFgaResourceId",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_SqlOSFgaResourceId",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_Chains_SqlOSFgaResourceId",
                table: "Chains");

            migrationBuilder.DropColumn(
                name: "SqlOSFgaScope",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SqlOSFgaScope",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SqlOSFgaScope",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "SqlOSFgaScope",
                table: "Chains");
        }
    }
}
