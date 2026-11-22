# ADR-002: Deterministic Statistical Anomaly Detection Engine Over Opaque ML

- **Status:** Accepted (2026-11-22)
- **Context:** Industrial DataLogger operators require explainable, deterministic anomaly alerts that catch both sudden spikes and gradual sensor drift without unpredictable black-box model behavior.
- **Decision:** Implement a 6-method deterministic statistical engine (`AnomalyDetectionEngine`): `InvalidReading`, `StaticThreshold`, `SuddenSpike` (10s normalized rate), `RollingZScore` (12-sample moving average + standard deviation with $\sigma \ge 0.25$ guard), `SensorDrift`, and `MissingDataGap`.
- **Consequences:** Every anomaly carries an exact `DetectionMethod`, `Severity`, `MeasuredValue`, `ExpectedRange`, and human-readable `Explanation` verified by unit tests.
