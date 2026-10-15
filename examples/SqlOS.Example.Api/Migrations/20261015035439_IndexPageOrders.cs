using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class IndexPageOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Chains_Id",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Chains_Id", x => new { x.NodeId, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_InventoryItems_Id",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_InventoryItems_Id", x => new { x.NodeId, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Locations_Id",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Locations_Id", x => new { x.NodeId, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Workspaces_Id",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Workspaces_Id", x => new { x.NodeId, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Workspaces_OrganizationId",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OrganizationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Workspaces_OrganizationId", x => new { x.NodeId, x.OrganizationId, x.RowKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Name_Id",
                table: "Workspaces",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Name_Id",
                table: "Locations",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_Name_Id",
                table: "InventoryItems",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_Price_Id",
                table: "InventoryItems",
                columns: new[] { "Price", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_Sku_Id",
                table: "InventoryItems",
                columns: new[] { "Sku", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Chains_Name_Id",
                table: "Chains",
                columns: new[] { "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Chains_Id_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Chains_Id",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_InventoryItems_Id_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_InventoryItems_Id",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Locations_Id_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Locations_Id",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Workspaces_Id_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Workspaces_Id",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Workspaces_OrganizationId_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Workspaces_OrganizationId",
                column: "RowKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Chains_Id",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_InventoryItems_Id",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Locations_Id",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Workspaces_Id",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Workspaces_OrganizationId",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_Name_Id",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Name_Id",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_Name_Id",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_Price_Id",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_Sku_Id",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_Chains_Name_Id",
                table: "Chains");
        }
    }
}
