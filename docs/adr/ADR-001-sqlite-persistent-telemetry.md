# ADR-001: Dual-Tier SQLite + IndexedDB Telemetry Storage

- **Status:** Accepted (2026-11-22)
- **Context:** Operators need persistent server-side historical telemetry, date-range filtering, and multi-device comparison across browsers, while retaining instant zero-latency local dataset switching.
- **Decision:** Keep SQLite (`Devices` and `TelemetryRecords` tables with `(UserEmail, RecordedAt)` and `(DeviceId, RecordedAt)` B-tree indexes) as the primary server persistence tier, and retain browser `IndexedDB` (`serialLogHubDB`) as a local cache for offline CSV exploration.
- **Consequences:** Zero external database infrastructure required for Render/Docker deployments; `WLB` dumps are automatically persisted server-side on completion.
