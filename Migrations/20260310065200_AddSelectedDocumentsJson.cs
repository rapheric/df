using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBA.DCL.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedDocumentsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedDocumentsJson",
                table: "Deferrals",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedDocumentsJson",
                table: "Deferrals");
        }
    }
}