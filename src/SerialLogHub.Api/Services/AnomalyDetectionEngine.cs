using System.Globalization;

namespace SerialLogHub.Api.Services;

public sealed class AnomalyDetectionConfig
{
    public double MinTempThreshold { get; set; } = -10.0;
    public double MaxTempThreshold { get; set; } = 40.0;
    public double MinHumThreshold { get; set; } = 0.0;
    public double MaxHumThreshold { get; set; } = 100.0;
    public double MaxTempRatePer10s { get; set; } = 3.0;
    public double MaxHumRatePer10s { get; set; } = 15.0;
    public int RollingWindowSize { get; set; } = 12;
    public double ZScoreThreshold { get; set; } = 3.0;
    public int DriftWindowSize { get; set; } = 8;
    public double DriftThresholdTemp { get; set; } = 2.5;
    public double ExpectedIntervalSeconds { get; set; } = 20.0;
}

public sealed class TelemetryInputSample
{
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? RawPayload { get; set; }
}

public sealed class AnomalyEvaluationResult
{
    public int SampleIndex { get; set; }
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsAnomaly { get; set; }
    public string DetectionMethod { get; set; } = "None";
    public string MeasuredValue { get; set; } = string.Empty;
    public string ExpectedRange { get; set; } = string.Empty;
    public string Severity { get; set; } = "Normal";
    public string Explanation { get; set; } = "Within normal operating envelope.";
    public double RollingMeanTemp { get; set; }
    public double RollingStdDevTemp { get; set; }
    public double ZScoreTemp { get; set; }
}

/// <summary>
/// Deterministic statistical anomaly detection engine supporting:
/// 1. Invalid Sensor Reading Detection (NaN/out-of-physics bounds)
/// 2. Configurable Static Threshold Detection
/// 3. Sudden Spike / Rate-of-Change Detection (normalized per 10s)
/// 4. Rolling Window Moving Average + Standard Deviation (Z-Score)
/// 5. Monotonic Sensor Drift Detection
/// 6. Missing Data / Sampling Gap Detection
/// </summary>
public sealed class AnomalyDetectionEngine
{
    public IReadOnlyList<AnomalyEvaluationResult> EvaluateSeries(
        IReadOnlyList<TelemetryInputSample> samples,
        AnomalyDetectionConfig? config = null)
    {
        config ??= new AnomalyDetectionConfig();
        var results = new List<AnomalyEvaluationResult>(samples.Count);
        var rollingTemps = new Queue<double>();
        int monotonicDriftSteps = 0;
        double cumulativeDrift = 0.0;
        int lastDriftDirection = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var cur = samples[i];
            var eval = new AnomalyEvaluationResult
            {
                SampleIndex = i + 1,
                Temperature = cur.Temperature,
                Humidity = cur.Humidity,
                Timestamp = cur.RecordedAt,
                MeasuredValue = FormattableString.Invariant($"{cur.Temperature:F2} °C / {cur.Humidity:F2} %"),
                ExpectedRange = FormattableString.Invariant(
                    $"Temp [{config.MinTempThreshold:F1}..{config.MaxTempThreshold:F1} °C], Hum [{config.MinHumThreshold:F0}..{config.MaxHumThreshold:F0} %]")
            };

            var methods = new List<string>();
            var explanations = new List<string>();
            int maxSeverityRank = 0;

            void Flag(string method, string severity, int rank, string explanation)
            {
                eval.IsAnomaly = true;
                if (!methods.Contains(method)) methods.Add(method);
                explanations.Add(explanation);
                if (rank > maxSeverityRank)
                {
                    maxSeverityRank = rank;
                    eval.Severity = severity;
                }
            }

            // 1. Invalid Sensor Reading Detection
            bool invalidPhysical =
                double.IsNaN(cur.Temperature) || double.IsInfinity(cur.Temperature) ||
                double.IsNaN(cur.Humidity) || double.IsInfinity(cur.Humidity) ||
                cur.Temperature < -50.0 || cur.Temperature > 125.0 ||
                cur.Humidity < 0.0 || cur.Humidity > 100.0;

            if (invalidPhysical)
            {
                Flag("InvalidReading", "Critical", 4,
                    FormattableString.Invariant($"Sensor reading ({cur.Temperature:F2} °C, {cur.Humidity:F2} %) is outside valid physical transducer bounds."));
            }

            // 2. Static Threshold Detection
            if (!invalidPhysical)
            {
                if (cur.Temperature > config.MaxTempThreshold || cur.Temperature < config.MinTempThreshold)
                {
                    Flag("StaticThreshold", "High", 3,
                        FormattableString.Invariant($"Temperature {cur.Temperature:F2} °C breached threshold [{config.MinTempThreshold:F1}..{config.MaxTempThreshold:F1} °C]."));
                }
                if (cur.Humidity > config.MaxHumThreshold || cur.Humidity < config.MinHumThreshold)
                {
                    Flag("StaticThreshold", "High", 3,
                        FormattableString.Invariant($"Humidity {cur.Humidity:F2} % breached threshold [{config.MinHumThreshold:F0}..{config.MaxHumThreshold:F0} %]."));
                }
            }

            // 3.Rolling Window Moving Average & Z-Score Detection (computed against prior baseline window)
            if (rollingTemps.Count >= Math.Max(4, config.RollingWindowSize / 2) && !invalidPhysical)
            {
                double mean = rollingTemps.Average();
                double variance = rollingTemps.Select(v => (v - mean) * (v - mean)).Sum() / rollingTemps.Count;
                double stdDev = Math.Sqrt(variance);
                double effectiveStd = Math.Max(stdDev, 0.25); // Guard against zero-variance noise false positives
                double zScore = Math.Abs(cur.Temperature - mean) / effectiveStd;

                eval.RollingMeanTemp = Math.Round(mean, 3);
                eval.RollingStdDevTemp = Math.Round(stdDev, 3);
                eval.ZScoreTemp = Math.Round(zScore, 2);

                if (zScore > config.ZScoreThreshold && Math.Abs(cur.Temperature - mean) >= 1.5)
                {
                    Flag("RollingZScore", "Medium", 2,
                        FormattableString.Invariant($"Statistical outlier: Z-Score {zScore:F2} exceeds {config.ZScoreThreshold:F1} (rolling mean {mean:F2} °C, σ={stdDev:F2})."));
                }
            }

            // 4. Rate-of-Change (Sudden Spike), Sensor Drift, and Missing Data Gap Detection
            if (i > 0 && !invalidPhysical)
            {
                var prev = samples[i - 1];
                double dt = (cur.RecordedAt - prev.RecordedAt).TotalSeconds;
                double dTemp = cur.Temperature - prev.Temperature;
                double absTemp = Math.Abs(dTemp);
                double absHum = Math.Abs(cur.Humidity - prev.Humidity);

                double normTemp = dt > 0 ? (absTemp * 10.0) / dt : absTemp;
                double normHum = dt > 0 ? (absHum * 10.0) / dt : absHum;

                if (normTemp > config.MaxTempRatePer10s)
                {
                    Flag("SuddenSpike", "High", 3,
                        FormattableString.Invariant($"Temperature spike ΔT={normTemp:F2} °C/10s exceeds limit {config.MaxTempRatePer10s:F2} °C/10s."));
                }

                if (normHum > config.MaxHumRatePer10s)
                {
                    Flag("SuddenSpike", "High", 3,
                        FormattableString.Invariant($"Humidity spike ΔH={normHum:F2} %/10s exceeds limit {config.MaxHumRatePer10s:F2} %/10s."));
                }

                // Missing Data Gap
                if (config.ExpectedIntervalSeconds > 0 && dt > config.ExpectedIntervalSeconds * 2.5)
                {
                    Flag("MissingDataGap", "Low", 1,
                        FormattableString.Invariant($"Sampling gap of {dt:F0}s detected (expected ~{config.ExpectedIntervalSeconds:F0}s interval)."));
                }

                // Monotonic Sensor Drift
                int dir = dTemp > 0.05 ? 1 : (dTemp < -0.05 ? -1 : 0);
                if (dir != 0 && (dir == lastDriftDirection || lastDriftDirection == 0))
                {
                    monotonicDriftSteps++;
                    cumulativeDrift += dTemp;
                    lastDriftDirection = dir;

                    if (monotonicDriftSteps >= config.DriftWindowSize &&
                        Math.Abs(cumulativeDrift) >= config.DriftThresholdTemp)
                    {
                        Flag("SensorDrift", "Medium", 2,
                            FormattableString.Invariant($"Sustained sensor drift of {cumulativeDrift:+0.00;-0.00} °C over {monotonicDriftSteps} consecutive intervals."));
                    }
                }
                else if (dir != 0)
                {
                    monotonicDriftSteps = 1;
                    cumulativeDrift = dTemp;
                    lastDriftDirection = dir;
                }
            }

            // Update rolling baseline with non-invalid readings
            if (!invalidPhysical)
            {
                rollingTemps.Enqueue(cur.Temperature);
                while (rollingTemps.Count > Math.Max(4, config.RollingWindowSize))
                {
                    rollingTemps.Dequeue();
                }
            }

            if (eval.IsAnomaly)
            {
                eval.DetectionMethod = string.Join("+", methods);
                eval.Explanation = string.Join(" ", explanations);
            }

            results.Add(eval);
        }

        return results;
    }

    /// <summary>
    /// Parses standard DataLogger CSV / WLB output lines ("CMD,OK,temp,hum,YYYY,MM,DD,HH,mm,ss")
    /// into structured TelemetryInputSample objects.
    /// </summary>
    public List<TelemetryInputSample> ParseDataLoggerPayload(string rawCsvOrWlb)
    {
        var samples = new List<TelemetryInputSample>();
        if (string.IsNullOrWhiteSpace(rawCsvOrWlb))
        {
            return samples;
        }

        var lines = rawCsvOrWlb.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("CMD,OK,", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CMD,OK,LSD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cols = line.Split(',');
            if (cols.Length < 10)
            {
                continue;
            }

            if (!double.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double temp) ||
                !double.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double hum))
            {
                continue;
            }

            if (int.TryParse(cols[4].Trim(), out int year) &&
                int.TryParse(cols[5].Trim(), out int month) &&
                int.TryParse(cols[6].Trim(), out int day) &&
                int.TryParse(cols[7].Trim(), out int hour) &&
                int.TryParse(cols[8].Trim(), out int minute) &&
                int.TryParse(cols[9].Trim(), out int second))
            {
                DateTime recordedUtc;
                try
                {
                    recordedUtc = new DateTime(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 31),
                        Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59), DateTimeKind.Utc);
                }
                catch
                {
                    recordedUtc = DateTime.UtcNow;
                }

                samples.Add(new TelemetryInputSample
                {
                    Temperature = temp,
                    Humidity = hum,
                    RecordedAt = recordedUtc,
                    RawPayload = line
                });
            }
        }

        return samples;
    }
}
