using System.Text.Json;
using SerialLogHub.Desktop.Forms;
using SerialLogHub.Desktop.Models;
using SerialLogHub.Desktop.Services;

namespace SerialLogHub.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        BridgeConfiguration config = LoadConfiguration();

        if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
        {
            return RunHeadlessAsync(config, args).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();

        using var apiClient = new BridgeApiClient(config);
        using var loginForm = new LoginForm(config, apiClient);

        if (loginForm.ShowDialog() != DialogResult.OK)
        {
            return 0;
        }

        var engine = new BridgeEngine(
            apiClient,
            new RealSerialTransport(),
            new SimulatedSerialTransport(config.VirtualDemoPortName));

        Application.Run(new MainForm(config, engine, loginForm.AuthenticatedEmail));
        return 0;
    }

    private static async Task<int> RunHeadlessAsync(BridgeConfiguration config, string[] args)
    {
        string email = GetArgValue(args, "--email") ?? config.DemoEmail;
        using var apiClient = new BridgeApiClient(config);
        var engine = new BridgeEngine(
            apiClient,
            new RealSerialTransport(),
            new SimulatedSerialTransport(config.VirtualDemoPortName))
        {
            IncludeVirtualDemoDevice = true
        };

        engine.LogReceived += msg => Console.WriteLine($"[Bridge] {msg}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await engine.SyncPortsAsync(email, forcePublish: true, cts.Token);
        await engine.SendHeartbeatAsync(email, cts.Token);

        while (!cts.Token.IsCancellationRequested)
        {
            await engine.PollAndExecuteNextCommandAsync(email, cts.Token);
            await engine.SendHeartbeatAsync(email, cts.Token);
            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return 0;
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static BridgeConfiguration LoadConfiguration()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath))
        {
            return new BridgeConfiguration();
        }

        string json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<BridgeConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new BridgeConfiguration();
    }
}
