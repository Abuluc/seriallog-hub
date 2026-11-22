using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SerialLogHub.Api.Controllers;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Models;
using SerialLogHub.Api.Entities;
using Xunit;

namespace SerialLogHub.Tests;

public sealed class UserControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly UserController _controller;

    public UserControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _controller = new UserController(_db, NullLogger<UserController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = SerialLogHub.Api.Services.OperatorScopeGuard.CreateAuthenticatedContext("ops@example.com")
            }
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetCommands_ExpiresStaleCommandsOlderThan60Seconds()
    {
        _db.Commands.Add(new CommandEntity
        {
            Email = "ops@example.com",
            SelecetdPort = "COM4",
            BaudRate = 9600,
            Message = "TEST",
            CommandID = "stale-1",
            Timestamp = DateTime.UtcNow.AddSeconds(-90),
            IsProcessed = false,
            Response = "Pending"
        });
        _db.Commands.Add(new CommandEntity
        {
            Email = "ops@example.com",
            SelecetdPort = "COM99",
            BaudRate = 9600,
            Message = "WLJ",
            CommandID = "fresh-2",
            Timestamp = DateTime.UtcNow,
            IsProcessed = false,
            Response = "Pending"
        });
        await _db.SaveChangesAsync();

        var result = await _controller.GetCommands("ops@example.com");
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CommandResponseDto>(ok.Value);
        Assert.Equal("fresh-2", dto.CommandID);

        var stale = await _db.Commands.SingleAsync(c => c.CommandID == "stale-1");
        Assert.True(stale.IsProcessed);
        Assert.StartsWith("Error:", stale.Response);
    }

    [Fact]
    public async Task UpdateCommand_IsIdempotent_DoesNotDuplicateHistory()
    {
        await _controller.SendCommand(new SendCommandRequest
        {
            Email = "ops@example.com",
            SelecetdPort = "COM99",
            BaudRate = 9600,
            Message = "WLJ",
            CommandID = "cmd-dup-1"
        });

        var req = new UpdateCommandRequest
        {
            Email = "ops@example.com",
            CommandID = "cmd-dup-1",
            Response = "CMD,OK,BAT,87"
        };

        var first = await _controller.UpdateCommand(req);
        Assert.IsType<OkObjectResult>(first);

        var second = await _controller.UpdateCommand(new UpdateCommandRequest
        {
            Email = "ops@example.com",
            CommandID = "cmd-dup-1",
            Response = "Error: Late overwrite attempt"
        });
        Assert.IsType<OkObjectResult>(second);

        int historyCount = await _db.CommandHistories.CountAsync(h => h.CommandID == "cmd-dup-1");
        Assert.Equal(1, historyCount);

        var saved = await _db.Commands.SingleAsync(c => c.CommandID == "cmd-dup-1");
        Assert.Equal("CMD,OK,BAT,87", saved.Response);
    }

    [Fact]
    public async Task GetCommandHistory_ReturnsNewestFirstAndFlagsErrors()
    {
        _db.CommandHistories.AddRange(
            new CommandHistoryEntity
            {
                Email = "ops@example.com",
                CommandID = "h1",
                Port = "COM77",
                BaudRate = 9600,
                Message = "TEST",
                Response = "Error: Could not find file 'COM77'.",
                Timestamp = DateTime.UtcNow.AddMinutes(-2)
            },
            new CommandHistoryEntity
            {
                Email = "ops@example.com",
                CommandID = "h2",
                Port = "COM99",
                BaudRate = 9600,
                Message = "WLJ",
                Response = "CMD,OK,BAT,92",
                Timestamp = DateTime.UtcNow
            });
        await _db.SaveChangesAsync();

        var result = await _controller.GetCommandHistory("ops@example.com", 10);
        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value).ToList();

        Assert.Equal(2, list.Count);
        string firstJson = System.Text.Json.JsonSerializer.Serialize(list[0]);
        string secondJson = System.Text.Json.JsonSerializer.Serialize(list[1]);
        Assert.Contains("\"CommandID\":\"h2\"", firstJson);
        Assert.Contains("\"IsError\":false", firstJson);
        Assert.Contains("\"CommandID\":\"h1\"", secondJson);
        Assert.Contains("\"IsError\":true", secondJson);
    }

    [Fact]
    public async Task UpdatePorts_AndGetPorts_RoundTripsDistinctPorts()
    {
        var updateRes = await _controller.UpdatePorts(new UpdatePortsRequest
        {
            UserEmail = "ops@example.com",
            Ports = new List<string> { "COM3 (USB)", "COM3 (USB)", "COM99 (Demo)" }
        });
        Assert.IsType<OkObjectResult>(updateRes);

        var getRes = await _controller.GetPorts("ops@example.com");
        var ok = Assert.IsType<OkObjectResult>(getRes);
        string json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("COM3 (USB)", json);
        Assert.Contains("COM99 (Demo)", json);
    }
}
