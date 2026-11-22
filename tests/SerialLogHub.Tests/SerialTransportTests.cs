using SerialLogHub.Desktop.Services;
using Xunit;

namespace SerialLogHub.Tests;

public class SerialTransportTests
{
    [Fact]
    public async Task SimulatedTransport_RespondsToCoreDataLoggerProtocolCommands()
    {
        var simulator = new SimulatedSerialTransport("COM99 (Virtual DataLogger - Demo)");
        string port = simulator.VirtualPortName;

        string handshake = await simulator.ExecuteCommandAsync(port, 9600, "TEST");
        Assert.Contains("CMD,OK,LSS", handshake);

        string battery = await simulator.ExecuteCommandAsync(port, 9600, "WLJ");
        Assert.StartsWith("CMD,OK,BAT,", battery);

        await simulator.ExecuteCommandAsync(port, 9600, "WLG, COLD_VAULT_B2");
        string comment = await simulator.ExecuteCommandAsync(port, 9600, "WLW");
        Assert.Contains("COLD_VAULT_B2", comment);

        string clock = await simulator.ExecuteCommandAsync(port, 9600, "WLH0");
        Assert.StartsWith("CMD,OK,TIME,", clock);

        bool finishedCallbackInvoked = false;
        string dump = await simulator.ExecuteCommandAsync(port, 9600, "WLB", (_, finished, _) =>
        {
            if (finished) finishedCallbackInvoked = true;
            return Task.CompletedTask;
        });

        Assert.True(finishedCallbackInvoked);
        Assert.Contains("CMD,OK,LSD,", dump);
        int dataRows = dump.Split('\n').Count(l => l.StartsWith("CMD,OK,2"));
        Assert.True(dataRows >= 50);
    }

    [Theory]
    [InlineData("COM3 (USB-SERIAL CH340)", "COM3")]
    [InlineData("COM4(USB-Device)", "COM4")]
    [InlineData("USB-SERIAL CH340 (COM7)", "COM7")]
    [InlineData("COM99 (Virtual DataLogger - Demo)", "COM99")]
    [InlineData("com12", "COM12")]
    public void ExtractRawPortName_ParsesWmiDisplayStrings(string display, string expected)
    {
        Assert.Equal(expected, RealSerialTransport.ExtractRawPortName(display));
    }

    [Fact]
    public void SimulatedTransport_OnlyHandlesConfiguredVirtualPort()
    {
        var simulator = new SimulatedSerialTransport("COM99 (Virtual DataLogger - Demo)");
        Assert.True(simulator.IsVirtualPort("COM99 (Virtual DataLogger - Demo)"));
        Assert.True(simulator.IsVirtualPort("COM99"));
        Assert.False(simulator.IsVirtualPort("COM4 (Standard Serial over Bluetooth link)"));
        Assert.False(simulator.IsVirtualPort("No COM ports detected"));
    }
}
