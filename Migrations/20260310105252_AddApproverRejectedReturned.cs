using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBA.DCL.Migrations
{
    /// <inheritdoc />
    public partial class AddApproverRejectedReturned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Rejected",
                table: "Approvers",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "Approvers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Returned",
                table: "Approvers",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnedAt",
                table: "Approvers",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Rejected",
                table: "Approvers");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "Approvers");

            migrationBuilder.DropColumn(
                name: "Returned",
                table: "Approvers");

            migrationBuilder.DropColumn(
                name: "ReturnedAt",
                table: "Approvers");
        }
    }
}