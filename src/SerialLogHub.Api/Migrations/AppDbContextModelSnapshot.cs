using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SerialLogHub.Api.Data;

#nullable disable

namespace SerialLogHub.Api.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.5");

        modelBuilder.Entity("SerialLogHub.Api.Entities.ApiKeyEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("ClientType").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("ExpiresAt").HasColumnType("TEXT");
            b.Property<bool>("IsRevoked").HasColumnType("INTEGER");
            b.Property<string>("Key").IsRequired().HasColumnType("TEXT");
            b.Property<string>("OwnerEmail").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Key").IsUnique();
            b.ToTable("ApiKeys");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.CommandEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int>("AttemptCount").HasColumnType("INTEGER");
            b.Property<int>("BaudRate").HasColumnType("INTEGER");
            b.Property<string>("CommandID").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
            b.Property<DateTime?>("DispatchedAt").HasColumnType("TEXT");
            b.Property<string>("Email").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ErrorCategory").HasColumnType("TEXT");
            b.Property<bool>("IsProcessed").HasColumnType("INTEGER");
            b.Property<string>("Message").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Response").IsRequired().HasColumnType("TEXT");
            b.Property<string>("SelecetdPort").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Status").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("Timestamp").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Email", "CommandID");
            b.HasIndex("Email", "Status", "IsProcessed");
            b.ToTable("Commands");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.CommandHistoryEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int>("BaudRate").HasColumnType("INTEGER");
            b.Property<string>("CommandID").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Email").IsRequired().HasColumnType("TEXT");
            b.Property<bool>("IsProcessed").HasColumnType("INTEGER");
            b.Property<string>("Message").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Port").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Response").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("Timestamp").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Email", "CommandID");
            b.ToTable("CommandHistories");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.DeviceEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("DeviceName").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("LastSeenUtc").HasColumnType("TEXT");
            b.Property<string>("PortName").IsRequired().HasColumnType("TEXT");
            b.Property<string>("SerialNumber").IsRequired().HasColumnType("TEXT");
            b.Property<string>("UserEmail").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("UserEmail", "PortName").IsUnique();
            b.ToTable("Devices");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.TelemetryRecordEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("AnomalyExplanation").IsRequired().HasColumnType("TEXT");
            b.Property<string>("AnomalyMethod").IsRequired().HasColumnType("TEXT");
            b.Property<string>("AnomalySeverity").IsRequired().HasColumnType("TEXT");
            b.Property<int>("DeviceId").HasColumnType("INTEGER");
            b.Property<double>("Humidity").HasColumnType("REAL");
            b.Property<DateTime>("IngestedAt").HasColumnType("TEXT");
            b.Property<bool>("IsAnomaly").HasColumnType("INTEGER");
            b.Property<string>("PortName").IsRequired().HasColumnType("TEXT");
            b.Property<string>("RawPayload").HasColumnType("TEXT");
            b.Property<DateTime>("RecordedAt").HasColumnType("TEXT");
            b.Property<double>("Temperature").HasColumnType("REAL");
            b.Property<string>("UserEmail").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("DeviceId", "RecordedAt").IsUnique();
            b.HasIndex("UserEmail", "RecordedAt");
            b.ToTable("TelemetryRecords");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.UserEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("Email").IsRequired().HasColumnType("TEXT");
            b.Property<string>("HashedPassword").IsRequired().HasColumnType("TEXT");
            b.Property<bool>("IsEmailVerified").HasColumnType("INTEGER");
            b.Property<string>("ResetPasswordToken").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("TokenExpiryTime").HasColumnType("TEXT");
            b.Property<string>("Username").IsRequired().HasColumnType("TEXT");
            b.Property<string>("VerificationToken").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Email").IsUnique();
            b.ToTable("Users");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.UserPortEntity", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("Ports").IsRequired().HasColumnType("TEXT");
            b.Property<string>("UserEmail").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("UserEmail").IsUnique();
            b.ToTable("UsersPort");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.TelemetryRecordEntity", b =>
        {
            b.HasOne("SerialLogHub.Api.Entities.DeviceEntity", "Device")
                .WithMany("TelemetryRecords")
                .HasForeignKey("DeviceId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Device");
        });

        modelBuilder.Entity("SerialLogHub.Api.Entities.DeviceEntity", b =>
        {
            b.Navigation("TelemetryRecords");
        });
#pragma warning restore 612, 618
    }
}
