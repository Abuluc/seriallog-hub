# Phase Five — Deterministic Statistical Anomaly Detection Engine

**Service:** `SerialLogHub.Api.Services.AnomalyDetectionEngine` & `wwwroot/js/dashboard/storage.js`  
**Document Date:** 2026-11-22

---

## 1. Design Philosophy

Rather than introducing opaque machine-learning dependencies, SerialLog Hub implements a **deterministic, explainable statistical anomaly detection engine** (`AnomalyDetectionEngine`) that evaluates time-series temperature and humidity readings in $O(N)$ time with bounded rolling memory ($O(W)$ where $W = 12$).

Every evaluated sample produces an `AnomalyEvaluationResult` containing:
- `DetectionMethod` (`None`, `InvalidReading`, `StaticThreshold`, `SuddenSpike`, `RollingZScore`, `SensorDrift`, `MissingDataGap`, or combined e.g. `SuddenSpike+RollingZScore`)
- `MeasuredValue` (`"31.80 °C / 48.50 %"`)
- `ExpectedRange` (`"Temp [-10.0..40.0 °C], Hum [0..100 %]"`)
- `Severity` (`Normal`, `Low`, `Medium`, `High`, `Critical`)
- `Timestamp` (UTC)
- `Explanation` (human-readable engineering explanation)
- `RollingMeanTemp`, `RollingStdDevTemp`, `ZScoreTemp`

---

## 2. Implemented Detection Methods

### 1. Invalid Sensor Reading Detection (`InvalidReading` — Severity: `Critical`)
Flags physically impossible transducer outputs or corrupted frames:
- `NaN` or `Infinity`
- Temperature `< -50.0 °C` or `> 125.0 °C`
- Relative Humidity `< 0.0 %` or `> 100.0 %`
- Invalid readings are excluded from polluting the rolling Z-Score baseline.

### 2. Configurable Static Threshold Detection (`StaticThreshold` — Severity: `High`)
Flags readings outside the operator-configured envelope:
- Default Temperature range: `[-10.0 °C .. 40.0 °C]` (adjustable live in the Web Dashboard)
- Default Humidity range: `[0.0 % .. 100.0 %]`

### 3. Sudden Spike / Rate-of-Change Detection (`SuddenSpike` — Severity: `High`)
Computes time-normalized 10-second rate of change between consecutive samples:
$$\Delta T_{10\text{s}} = \frac{|T_i - T_{i-1}| \times 10}{\Delta t}, \quad \Delta H_{10\text{s}} = \frac{|H_i - H_{i-1}| \times 10}{\Delta t}$$
- Flags when $\Delta T_{10\text{s}} > \text{MaxTempRatePer10s}$ (default `3.0 °C/10s`) or $\Delta H_{10\text{s}} > \text{MaxHumRatePer10s}$ (default `15.0 %/10s`).

### 4. Rolling Window Moving Average & Standard Deviation Z-Score (`RollingZScore` — Severity: `Medium`)
Maintains a rolling window of the prior $W = 12$ valid samples and computes rolling mean $\mu$ and standard deviation $\sigma$:
$$Z = \frac{|T_i - \mu|}{\max(\sigma, 0.25)}$$
- **False-Positive Guard:** Clamping $\sigma_{\text{eff}} = \max(\sigma, 0.25)$ and requiring $|T_i - \mu| \ge 1.5\text{ °C}$ prevents tiny sensor quantization jitter ($0.05\text{ °C}$) in a flat signal from triggering false Z-Score alarms, while reliably catching statistical outliers (`Z > 3.0`) that remain below the static `40 °C` threshold.

### 5. Monotonic Sensor Drift Detection (`SensorDrift` — Severity: `Medium`)
Tracks consecutive directional temperature changes ($\Delta T > +0.05\text{ °C}$ or $\Delta T < -0.05\text{ °C}$). When a sensor drifts monotonically in the same direction for $\ge 8$ consecutive intervals with cumulative drift $|\sum \Delta T| \ge 2.5\text{ °C}$, it flags a `SensorDrift` anomaly (e.g., failing cooling compressor or door left ajar).

### 6. Missing Data / Sampling Gap Detection (`MissingDataGap` — Severity: `Low`)
Flags timestamp dropouts where $\Delta t > 2.5 \times \text{ExpectedIntervalSeconds}$ (default `20s` interval $\rightarrow$ flags gaps `> 50s`).
