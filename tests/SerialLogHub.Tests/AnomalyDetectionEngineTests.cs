using SerialLogHub.Api.Services;
using Xunit;

namespace SerialLogHub.Tests;

public sealed class AnomalyDetectionEngineTests
{
    private readonly AnomalyDetectionEngine _engine = new();

    [Fact]
    public void EvaluateSeries_DetectsInvalidPhysicalSensorReadings()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>
        {
            new() { Temperature = 22.0, Humidity = 50.0, RecordedAt = start },
            new() { Temperature = 22.5, Humidity = 115.0, RecordedAt = start.AddSeconds(20) },
            new() { Temperature = -85.0, Humidity = 48.0, RecordedAt = start.AddSeconds(40) }
        };

        var results = _engine.EvaluateSeries(samples);

        Assert.False(results[0].IsAnomaly);
        Assert.True(results[1].IsAnomaly);
        Assert.Contains("InvalidReading", results[1].DetectionMethod);
        Assert.Equal("Critical", results[1].Severity);

        Assert.True(results[2].IsAnomaly);
        Assert.Contains("InvalidReading", results[2].DetectionMethod);
        Assert.Equal("Critical", results[2].Severity);
    }

    [Fact]
    public void EvaluateSeries_DetectsStaticThresholdBreach()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>
        {
            new() { Temperature = 25.0, Humidity = 50.0, RecordedAt = start },
            new() { Temperature = 42.5, Humidity = 50.0, RecordedAt = start.AddSeconds(200) }
        };

        var results = _engine.EvaluateSeries(samples, new AnomalyDetectionConfig { MaxTempThreshold = 40.0 });

        Assert.True(results[1].IsAnomaly);
        Assert.Contains("StaticThreshold", results[1].DetectionMethod);
        Assert.Equal("High", results[1].Severity);
    }

    [Fact]
    public void EvaluateSeries_DetectsSuddenSpikeRateOfChange()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>
        {
            new() { Temperature = 22.0, Humidity = 50.0, RecordedAt = start },
            new() { Temperature = 29.5, Humidity = 50.0, RecordedAt = start.AddSeconds(10) }
        };

        var results = _engine.EvaluateSeries(samples);

        Assert.True(results[1].IsAnomaly);
        Assert.Contains("SuddenSpike", results[1].DetectionMethod);
    }

    [Fact]
    public void EvaluateSeries_DetectsRollingZScoreOutlierWithinStaticThresholds()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>();

        // 12 stable baseline readings around 21.0 °C (well below 40 °C static threshold)
        for (int i = 0; i < 12; i++)
        {
            double jitter = (i % 2 == 0) ? 0.1 : -0.1;
            samples.Add(new TelemetryInputSample
            {
                Temperature = 21.0 + jitter,
                Humidity = 50.0,
                RecordedAt = start.AddSeconds(i * 60)
            });
        }

        // Reading #13 jumps to 24.5 °C over 60s (rate is 0.58 °C/10s < 3.0 spike limit, temp < 40 °C static limit, but Z-Score > 3.0!)
        samples.Add(new TelemetryInputSample
        {
            Temperature = 24.5,
            Humidity = 50.0,
            RecordedAt = start.AddSeconds(12 * 60)
        });

        var config = new AnomalyDetectionConfig
        {
            MaxTempThreshold = 40.0,
            MaxTempRatePer10s = 3.0,
            ZScoreThreshold = 3.0,
            ExpectedIntervalSeconds = 60.0
        };

        var results = _engine.EvaluateSeries(samples, config);
        var outlier = results[^1];

        Assert.True(outlier.IsAnomaly);
        Assert.Contains("RollingZScore", outlier.DetectionMethod);
        Assert.True(outlier.ZScoreTemp > 3.0);
    }

    [Fact]
    public void EvaluateSeries_DetectsMonotonicSensorDrift()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>();

        // 10 consecutive +0.35 °C steps every 20s (cumulative +3.15 °C monotonic drift)
        for (int i = 0; i < 10; i++)
        {
            samples.Add(new TelemetryInputSample
            {
                Temperature = 20.0 + (i * 0.35),
                Humidity = 50.0,
                RecordedAt = start.AddSeconds(i * 20)
            });
        }

        var results = _engine.EvaluateSeries(samples, new AnomalyDetectionConfig
        {
            DriftWindowSize = 8,
            DriftThresholdTemp = 2.5
        });

        Assert.Contains(results, r => r.IsAnomaly && r.DetectionMethod.Contains("SensorDrift"));
    }

    [Fact]
    public void EvaluateSeries_DetectsMissingDataSamplingGap()
    {
        var start = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var samples = new List<TelemetryInputSample>
        {
            new() { Temperature = 22.0, Humidity = 50.0, RecordedAt = start },
            new() { Temperature = 22.1, Humidity = 50.0, RecordedAt = start.AddSeconds(120) }
        };

        var results = _engine.EvaluateSeries(samples, new AnomalyDetectionConfig { ExpectedIntervalSeconds = 20.0 });

        Assert.True(results[1].IsAnomaly);
        Assert.Contains("MissingDataGap", results[1].DetectionMethod);
        Assert.Equal("Low", results[1].Severity);
    }

    [Fact]
    public void ParseDataLoggerPayload_HandlesMalformedLinesMissingFieldsAndInvalidNumbers()
    {
        string raw = string.Join("\n", new[]
        {
            "CMD,OK,LSD,",
            "GARBAGE,LINE,WITHOUT,HEADER",
            "CMD,OK,22.40,48.10,2026,11", // missing time columns
            "CMD,OK,NOT_A_NUM,48.10,2026,11,22,12,00,00",
            "CMD,OK,22.45,48.50,2026,11,22,12,00,20",
            "CMD,OK,22.60,48.80,2026,11,22,12,00,40"
        });

        var parsed = _engine.ParseDataLoggerPayload(raw);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(22.45, parsed[0].Temperature);
        Assert.Equal(22.60, parsed[1].Temperature);
    }
}
