using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlOS.Example.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePageOrdersWithNodeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Chains_Name",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_InventoryItems_Price",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_InventoryItems_Sku",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Locations_Name",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SqlOSFgaPage_Workspaces_Name",
                schema: "dbo");

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

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Chains_Name",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Chains_Name", x => new { x.NodeId, x.Name, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_InventoryItems_Price",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_InventoryItems_Price", x => new { x.NodeId, x.Price, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_InventoryItems_Sku",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_InventoryItems_Sku", x => new { x.NodeId, x.Sku, x.RowKey });
                });

            migrationBuilder.CreateTable(
                name: "SqlOSFgaPage_Locations_Name",
                schema: "dbo",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqlOSFgaPage_Locations_Name", x => new { x.NodeId, x.Name, x.RowKey });
                });

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
                name: "IX_SqlOSFgaPage_Chains_Name_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Chains_Name",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_InventoryItems_Price_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_InventoryItems_Price",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_InventoryItems_Sku_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_InventoryItems_Sku",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Locations_Name_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Locations_Name",
                column: "RowKey");

            migrationBuilder.CreateIndex(
                name: "IX_SqlOSFgaPage_Workspaces_Name_RowKey",
                schema: "dbo",
                table: "SqlOSFgaPage_Workspaces_Name",
                column: "RowKey");
        }
    }
}
