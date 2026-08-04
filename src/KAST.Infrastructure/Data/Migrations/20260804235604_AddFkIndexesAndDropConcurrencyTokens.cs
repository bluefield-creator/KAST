using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KAST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFkIndexesAndDropConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ServerInstances_InstallPath",
                table: "ServerInstances",
                column: "InstallPath");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstances_ProcessId",
                table: "ServerInstances",
                column: "ProcessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ServerInstances_InstallPath",
                table: "ServerInstances");

            migrationBuilder.DropIndex(
                name: "IX_ServerInstances_ProcessId",
                table: "ServerInstances");
        }
    }
}
