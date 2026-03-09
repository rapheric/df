using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBA.DCL.Migrations
{
    public partial class AddDeferralDocumentDays : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DaysSought",
                table: "DeferralDocuments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextDocumentDueDate",
                table: "DeferralDocuments",
                type: "datetime2",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DaysSought",
                table: "DeferralDocuments");

            migrationBuilder.DropColumn(
                name: "NextDocumentDueDate",
                table: "DeferralDocuments");
        }
    }
}
