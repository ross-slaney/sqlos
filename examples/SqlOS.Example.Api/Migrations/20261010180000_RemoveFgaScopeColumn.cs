using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFgaScopeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FgaScope",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "FgaScope",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "FgaScope",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "FgaScope",
                table: "Chains");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "FgaScope",
                table: "Workspaces",
                type: "varbinary(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "FgaScope",
                table: "Locations",
                type: "varbinary(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "FgaScope",
                table: "InventoryItems",
                type: "varbinary(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "FgaScope",
                table: "Chains",
                type: "varbinary(512)",
                maxLength: 512,
                nullable: true);
        }
    }
}
