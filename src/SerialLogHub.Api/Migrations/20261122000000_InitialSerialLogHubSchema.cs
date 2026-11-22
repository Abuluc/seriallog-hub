using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SerialLogHub.Api.Data;

#nullable disable

namespace SerialLogHub.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261122000000_InitialSerialLogHubSchema")]
public partial class InitialSerialLogHubSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ApiKeys",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Key = table.Column<string>(type: "TEXT", nullable: false),
                OwnerEmail = table.Column<string>(type: "TEXT", nullable: false),
                ClientType = table.Column<string>(type: "TEXT", nullable: false),
                IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ApiKeys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CommandHistories",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                Port = table.Column<string>(type: "TEXT", nullable: false),
                BaudRate = table.Column<int>(type: "INTEGER", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                CommandID = table.Column<string>(type: "TEXT", nullable: false),
                IsProcessed = table.Column<bool>(type: "INTEGER", nullable: false),
                Response = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CommandHistories", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Commands",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                SelecetdPort = table.Column<string>(type: "TEXT", nullable: false),
                BaudRate = table.Column<int>(type: "INTEGER", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: false),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                Response = table.Column<string>(type: "TEXT", nullable: false),
                CommandID = table.Column<string>(type: "TEXT", nullable: false),
                IsProcessed = table.Column<bool>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                DispatchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ErrorCategory = table.Column<string>(type: "TEXT", nullable: true),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Commands", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserEmail = table.Column<string>(type: "TEXT", nullable: false),
                DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                PortName = table.Column<string>(type: "TEXT", nullable: false),
                SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Devices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Username = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                HashedPassword = table.Column<string>(type: "TEXT", nullable: false),
                IsEmailVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                VerificationToken = table.Column<string>(type: "TEXT", nullable: false),
                ResetPasswordToken = table.Column<string>(type: "TEXT", nullable: false),
                TokenExpiryTime = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UsersPort",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserEmail = table.Column<string>(type: "TEXT", nullable: false),
                Ports = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsersPort", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "TelemetryRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                UserEmail = table.Column<string>(type: "TEXT", nullable: false),
                PortName = table.Column<string>(type: "TEXT", nullable: false),
                Temperature = table.Column<double>(type: "REAL", nullable: false),
                Humidity = table.Column<double>(type: "REAL", nullable: false),
                RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IngestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsAnomaly = table.Column<bool>(type: "INTEGER", nullable: false),
                AnomalySeverity = table.Column<string>(type: "TEXT", nullable: false),
                AnomalyMethod = table.Column<string>(type: "TEXT", nullable: false),
                AnomalyExplanation = table.Column<string>(type: "TEXT", nullable: false),
                RawPayload = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TelemetryRecords", x => x.Id);
                table.ForeignKey(
                    name: "FK_TelemetryRecords_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ApiKeys_Key",
            table: "ApiKeys",
            column: "Key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CommandHistories_Email_CommandID",
            table: "CommandHistories",
            columns: new[] { "Email", "CommandID" });

        migrationBuilder.CreateIndex(
            name: "IX_Commands_Email_CommandID",
            table: "Commands",
            columns: new[] { "Email", "CommandID" });

        migrationBuilder.CreateIndex(
            name: "IX_Commands_Email_Status_IsProcessed",
            table: "Commands",
            columns: new[] { "Email", "Status", "IsProcessed" });

        migrationBuilder.CreateIndex(
            name: "IX_Devices_UserEmail_PortName",
            table: "Devices",
            columns: new[] { "UserEmail", "PortName" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TelemetryRecords_DeviceId_RecordedAt",
            table: "TelemetryRecords",
            columns: new[] { "DeviceId", "RecordedAt" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TelemetryRecords_UserEmail_RecordedAt",
            table: "TelemetryRecords",
            columns: new[] { "UserEmail", "RecordedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Users_Email",
            table: "Users",
            column: "Email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UsersPort_UserEmail",
            table: "UsersPort",
            column: "UserEmail",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ApiKeys");
        migrationBuilder.DropTable(name: "CommandHistories");
        migrationBuilder.DropTable(name: "Commands");
        migrationBuilder.DropTable(name: "TelemetryRecords");
        migrationBuilder.DropTable(name: "Users");
        migrationBuilder.DropTable(name: "UsersPort");
        migrationBuilder.DropTable(name: "Devices");
    }
}
