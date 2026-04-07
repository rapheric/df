using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBA.DCL.Migrations
{
    public partial class AddUserPasswordLockout : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPasswordLocked",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordLockedAt",
                table: "Users",
                type: "datetime(6)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsPasswordLocked",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordLockedAt",
                table: "Users");
        }
    }
}