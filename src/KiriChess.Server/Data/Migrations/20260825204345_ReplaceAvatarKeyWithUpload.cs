using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAvatarKeyWithUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarKey",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<byte[]>(
                name: "AvatarImage",
                table: "AspNetUsers",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvatarUpdatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarImage",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AvatarUpdatedAt",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "AvatarKey",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }
    }
}
