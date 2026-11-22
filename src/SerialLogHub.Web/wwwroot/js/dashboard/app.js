(function () {
    "use strict";

    const { escapeHtml, saveDataset, getAllDatasets, getDatasetById, parseCsv, detectAnomalies } = window.SlhStorage;
    const { plotGraph, updateTelemetryStats, renderAnalyticsCharts, destroyAll } = window.SlhCharts;

    let originalData = [];
    let filteredData = [];
    let currentPage = 1;
    let activeTab = "all";
    let lastPortListKey = "";
    let lastWlbKey = "";
    let lastWlbAppliedAt = 0;
    const pageSize = 250;

    const anomalyConfig = {
        maxTemp: 40.0,
        tempRate: 3.0,
        humRate: 15.0
    };

    function isPlaceholderPort(port) {
        if (!port) return true;
        const p = port.trim();
        return !p || p.startsWith("No COM") || p.startsWith("Error:");
    }

    function applyFilters() {
        let rows = originalData.slice();

        if (activeTab === "anomalies") {
            rows = rows.filter(r => r.temperatureAnomaly || r.humidityAnomaly);
        }

        const searchEl = document.getElementById("indexSearch");
        const q = searchEl ? searchEl.value.trim() : "";
        if (q) {
            rows = rows.filter(r => String(r.id).includes(q));
        }

        const timeToggle = document.getElementById("toggleTimeRange");
        if (timeToggle && timeToggle.checked) {
            const s = document.getElementById("startDateTime").value;
            const e = document.getElementById("endDateTime").value;
            if (s && e) {
                const startMs = new Date(s).getTime();
                const endMs = new Date(e).getTime();
                if (!isNaN(startMs) && !isNaN(endMs)) {
                    rows = rows.filter(r => {
                        const ts = new Date(`${r.date}T${r.clock}`).getTime();
                        return ts >= startMs && ts <= endMs;
                    });
                }
            }
        }

        filteredData = rows;
        currentPage = 1;
        renderTable();
    }

    function renderTable() {
        const tbody = document.getElementById("datatbody");
        tbody.innerHTML = "";

        if (!filteredData.length) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:#94a3b8;padding:24px;">No matching readings.</td></tr>';
            document.getElementById("pageInfo").textContent = "Page 0 / 0";
            return;
        }

        const totalPages = Math.max(1, Math.ceil(filteredData.length / pageSize));
        if (currentPage > totalPages) currentPage = totalPages;

        const slice = filteredData.slice((currentPage - 1) * pageSize, currentPage * pageSize);
        const frag = document.createDocumentFragment();

        slice.forEach(r => {
            const tr = document.createElement("tr");
            const tip = escapeHtml(r.anomalyExplanation || "Normal operating envelope");
            const method = escapeHtml(r.anomalyMethod && r.anomalyMethod !== "None" ? ` (${r.anomalyMethod})` : "");
            let statusHtml = `<span class="slh-badge slh-badge-ok" title="${tip}">Normal</span>`;
            if (r.temperatureAnomaly && r.humidityAnomaly) {
                tr.className = "row-anomaly-both";
                statusHtml = `<span class="slh-badge slh-badge-danger" title="${tip}">Dual Spike${method}</span>`;
            } else if (r.temperatureAnomaly) {
                tr.className = "row-anomaly-temp";
                statusHtml = `<span class="slh-badge slh-badge-danger" title="${tip}">Temp Anomaly${method}</span>`;
            } else if (r.humidityAnomaly) {
                tr.className = "row-anomaly-hum";
                statusHtml = `<span class="slh-badge slh-badge-warn" title="${tip}">Hum Anomaly${method}</span>`;
            }

            tr.innerHTML = `<td>${r.id}</td><td>${r.temperature.toFixed(2)}</td><td>${r.humidity.toFixed(2)}</td><td>${escapeHtml(r.date)}</td><td>${escapeHtml(r.clock)}</td><td>${statusHtml}</td>`;
            frag.appendChild(tr);
        });

        tbody.appendChild(frag);
        document.getElementById("pageInfo").textContent = `Page ${currentPage} / ${totalPages}`;
    }

    function updateAnomalyDisplayAndCharts() {
        if (!originalData.length) return;
        detectAnomalies(originalData, anomalyConfig);
        const anomalies = originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly).length;
        document.getElementById("kpiAnomalyCount").textContent = String(anomalies);
        renderAnalyticsCharts(originalData);
        applyFilters();
    }

    function applyDataset(csvContent, sourceLabel, fileName, isDemo) {
        const parsed = parseCsv(csvContent);
        if (!parsed.rows.length) {
            toastr.warning("No valid DataLogger rows found in dataset.");
            return false;
        }

        originalData = detectAnomalies(parsed.rows, anomalyConfig);
        const anomalies = originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly).length;
        document.getElementById("kpiTotalRows").textContent = String(originalData.length);
        document.getElementById("kpiAnomalyCount").textContent = String(anomalies);

        const badge = document.getElementById("datasetSourceBadge");
        if (badge) {
            badge.style.display = isDemo ? "inline-flex" : "none";
        }

        plotGraph(parsed.chart);
        updateTelemetryStats(originalData);
        renderAnalyticsCharts(originalData);
        applyFilters();
        saveDataset(csvContent, sourceLabel, parsed.chart, fileName).then(renderHistoryTable);
        return true;
    }

    function applySerialDumpOnce(rawCsv, fileName, toastText) {
        const digest = `${rawCsv.length}:${rawCsv.slice(0, 64)}:${rawCsv.slice(-64)}`;
        const now = Date.now();
        if (digest === lastWlbKey && (now - lastWlbAppliedAt) < 4000) {
            return;
        }
        lastWlbKey = digest;
        lastWlbAppliedAt = now;
        if (applyDataset(rawCsv, "Serial Port", fileName, false)) {
            toastr.success(toastText);
        }
    }

    async function renderHistoryTable() {
        const entries = await getAllDatasets();
        const tbody = document.getElementById("csvTablebody");
        if (!tbody) return;
        tbody.innerHTML = "";
        if (!entries.length) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;color:#94a3b8;padding:18px;">No saved datasets in browser history yet.</td></tr>';
            return;
        }

        entries.sort((a, b) => b.id - a.id);
        entries.slice(0, 20).forEach(entry => {
            const meta = JSON.parse(entry.data);
            const tr = document.createElement("tr");
            tr.innerHTML =
                `<td>${entry.id}</td>` +
                `<td>${escapeHtml(meta.fileName)}</td>` +
                `<td>${Number(meta.rowCount) || 0}</td>` +
                `<td>${escapeHtml(meta.uploadDate)}</td>` +
                `<td>${escapeHtml(meta.situation)}</td>` +
                `<td><button type="button" class="slh-btn slh-btn-outline slh-btn-sm" data-load-id="${entry.id}">Load</button></td>`;
            tbody.appendChild(tr);
        });

        tbody.querySelectorAll("[data-load-id]").forEach(btn => {
            btn.addEventListener("click", async () => {
                const record = await getDatasetById(btn.dataset.loadId);
                if (!record) return;
                const meta = JSON.parse(record.data);
                const parsed = parseCsv(meta.csvContent);
                originalData = detectAnomalies(parsed.rows, anomalyConfig);
                document.getElementById("kpiTotalRows").textContent = String(originalData.length);
                document.getElementById("kpiAnomalyCount").textContent = String(originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly).length);
                plotGraph(parsed.chart);
                updateTelemetryStats(originalData);
                renderAnalyticsCharts(originalData);
                applyFilters();
                toastr.info(`Loaded dataset #${record.id} (${meta.fileName}).`);
            });
        });
    }

    async function refreshCommandHistory() {
        const tbody = document.getElementById("commandHistoryBody");
        if (!tbody) return;
        try {
            const res = await fetch("/Home/CommandHistory?take=25");
            if (!res.ok) return;
            const items = await res.json();
            if (!Array.isArray(items) || !items.length) {
                tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;color:#94a3b8;padding:18px;">No commands recorded yet.</td></tr>';
                return;
            }
            const frag = document.createDocumentFragment();
            items.forEach(item => {
                const tr = document.createElement("tr");
                const isErr = Boolean(item.isError);
                const badgeClass = isErr ? "slh-badge slh-badge-danger" : "slh-badge slh-badge-ok";
                const ts = item.timestamp ? new Date(item.timestamp).toISOString().replace("T", " ").slice(0, 19) : "--";
                const respText = item.responsePreview || "(empty)";
                tr.innerHTML =
                    `<td><code>${escapeHtml(item.port || "--")}</code></td>` +
                    `<td><code>${escapeHtml(item.message || "")}</code></td>` +
                    `<td><span class="${badgeClass}" style="max-width:220px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:inline-block;" title="${escapeHtml(respText)}">${escapeHtml(respText)}</span></td>` +
                    `<td style="white-space:nowrap;color:#64748b;">${escapeHtml(ts)}</td>`;
                frag.appendChild(tr);
            });
            tbody.innerHTML = "";
            tbody.appendChild(frag);
        } catch {
            // ignore transient audit poll error
        }
    }

    async function refreshServerDevices() {
        const selA = document.getElementById("serverDeviceSelect");
        const selB = document.getElementById("compareDeviceSelect");
        if (!selA || !selB) return;
        try {
            const res = await fetch("/Home/ServerDevices");
            if (!res.ok) return;
            const devices = await res.json();
            if (!Array.isArray(devices) || !devices.length) {
                selA.innerHTML = '<option value="">No server devices stored yet</option>';
                selB.innerHTML = '<option value="">No comparison device available</option>';
                return;
            }
            selA.innerHTML = "";
            selB.innerHTML = "";
            devices.forEach((d, idx) => {
                const label = `${d.deviceName} [${d.portName}] (${d.totalRecords} rows, ${d.anomalyRecords} anom.)`;
                const optA = document.createElement("option");
                optA.value = String(d.id);
                optA.textContent = label;
                selA.appendChild(optA);

                const optB = document.createElement("option");
                optB.value = String(d.id);
                optB.textContent = label;
                if (idx === 1) optB.selected = true;
                selB.appendChild(optB);
            });
        } catch {
            // ignore transient poll error
        }
    }

    async function loadServerTelemetryHistory() {
        const selA = document.getElementById("serverDeviceSelect");
        const deviceId = selA ? selA.value : "";
        if (!deviceId) {
            toastr.warning("Select a persistent server device first.");
            return;
        }
        try {
            const res = await fetch(`/Home/ServerTelemetryHistory?deviceId=${encodeURIComponent(deviceId)}`);
            if (!res.ok) { toastr.error("Failed to load server telemetry history."); return; }
            const data = await res.json();
            const records = data?.records || [];
            if (!records.length) {
                toastr.info("No historical records found for this device.");
                return;
            }
            const lines = ["CMD,OK,LSD,"];
            records.forEach(r => {
                const dt = new Date(r.recordedAt);
                lines.push(`CMD,OK,${Number(r.temperature).toFixed(2)},${Number(r.humidity).toFixed(2)},${dt.getUTCFullYear()},${String(dt.getUTCMonth() + 1).padStart(2, "0")},${String(dt.getUTCDate()).padStart(2, "0")},${String(dt.getUTCHours()).padStart(2, "0")},${String(dt.getUTCMinutes()).padStart(2, "0")},${String(dt.getUTCSeconds()).padStart(2, "0")}`);
            });
            const isSim = (records[0].portName || "").includes("Virtual") || (records[0].portName || "").includes("COM9");
            applyDataset(lines.join("\n"), "SQLite Server Archive", `Server_Device_${deviceId}.csv`, isSim);
            toastr.success(`Loaded ${records.length} persistent server telemetry records.`);
        } catch {
            toastr.error("Error loading server telemetry.");
        }
    }

    async function compareServerDevices() {
        const selA = document.getElementById("serverDeviceSelect");
        const selB = document.getElementById("compareDeviceSelect");
        const panel = document.getElementById("deviceComparisonPanel");
        if (!selA?.value || !selB?.value || !panel) return;
        try {
            const res = await fetch(`/Home/CompareDevices?deviceA=${encodeURIComponent(selA.value)}&deviceB=${encodeURIComponent(selB.value)}`);
            if (!res.ok) { toastr.error("Unable to compare selected devices."); return; }
            const cmp = await res.json();
            const a = cmp.deviceA;
            const b = cmp.deviceB;
            if (!a || !b) return;
            panel.innerHTML =
                `<div style="display:grid;grid-template-columns:1fr 1fr;gap:16px;">` +
                `<div><strong>${escapeHtml(a.deviceName)}</strong> (<code>${escapeHtml(a.portName)}</code>)<br/>` +
                `Samples: <strong>${a.sampleCount}</strong> | Anomalies: <strong>${a.anomalyCount} (${a.anomalyRatePercent}%)</strong><br/>` +
                `Temp Min/Avg/Max: <strong>${a.minTemp} / ${a.avgTemp} / ${a.maxTemp} °C</strong> | Hum Avg: <strong>${a.avgHum} %</strong></div>` +
                `<div><strong>${escapeHtml(b.deviceName)}</strong> (<code>${escapeHtml(b.portName)}</code>)<br/>` +
                `Samples: <strong>${b.sampleCount}</strong> | Anomalies: <strong>${b.anomalyCount} (${b.anomalyRatePercent}%)</strong><br/>` +
                `Temp Min/Avg/Max: <strong>${b.minTemp} / ${b.avgTemp} / ${b.maxTemp} °C</strong> | Hum Avg: <strong>${b.avgHum} %</strong></div>` +
                `</div>`;
            panel.style.display = "block";
        } catch {
            toastr.error("Device comparison failed.");
        }
    }

    function appendTerminal(msg) {
        const log = document.getElementById("serialTerminalLog");
        if (!log) return;
        const ts = new Date().toTimeString().slice(0, 8);
        log.textContent = `[${ts}] ${msg}\n` + log.textContent;
    }

    function setConnectedUi(connected, port, baud) {
        const portEl = document.getElementById("selectedPort");
        const baudEl = document.getElementById("selectedBaudRate");
        const msgEl = document.getElementById("messageToSend");
        const connectBtn = document.getElementById("connectBtn");
        const disconnectBtn = document.getElementById("disconnectBtn");
        const sendBtn = document.getElementById("sendButton");
        const wlbBtn = document.getElementById("quickReadWlbBtn");
        const diagBtn = document.getElementById("deviceDiagnosticsBtn");
        const diagStrip = document.getElementById("deviceDiagStrip");
        const kpiPort = document.getElementById("kpiActivePort");

        if (connected) {
            if (port) {
                let found = false;
                for (let i = 0; i < portEl.options.length; i++) {
                    if (portEl.options[i].value === port) { found = true; break; }
                }
                if (!found) {
                    const opt = document.createElement("option");
                    opt.value = port;
                    opt.textContent = port;
                    portEl.appendChild(opt);
                }
                portEl.value = port;
            }
            if (baud) {
                baudEl.value = String(baud);
            }
            portEl.disabled = true;
            baudEl.disabled = true;
            msgEl.disabled = false;
            connectBtn.style.display = "none";
            disconnectBtn.style.display = "inline-flex";
            sendBtn.style.display = "inline-flex";
            wlbBtn.style.display = "inline-flex";
            if (diagBtn) diagBtn.style.display = "inline-flex";
            if (kpiPort) kpiPort.textContent = `${port} @ ${baud}`;
        } else {
            portEl.disabled = false;
            baudEl.disabled = false;
            msgEl.disabled = true;
            msgEl.value = "";
            connectBtn.style.display = "inline-flex";
            disconnectBtn.style.display = "none";
            sendBtn.style.display = "none";
            wlbBtn.style.display = "none";
            if (diagBtn) diagBtn.style.display = "none";
            if (diagStrip) diagStrip.style.display = "none";
            if (kpiPort) kpiPort.textContent = "Not Connected";
        }
    }

    async function refreshPorts() {
        try {
            const res = await fetch("/Home/GetAvailablePorts");
            if (!res.ok) return;
            const ports = await res.json();
            if (!Array.isArray(ports)) return;

            const portKey = ports.join("||");
            if (portKey === lastPortListKey) return;
            lastPortListKey = portKey;

            const select = document.getElementById("selectedPort");
            const conn = window.SerialLogHub.getConnectionCookie();
            const prev = (conn && conn.Connected && conn.currentPort) ? conn.currentPort : select.value;

            select.innerHTML = "";
            ports.forEach(p => {
                const opt = document.createElement("option");
                opt.value = p;
                opt.textContent = p;
                select.appendChild(opt);
            });

            if (prev && ports.includes(prev)) {
                select.value = prev;
            } else if (conn && conn.Connected && conn.currentPort && ports.length > 0 && !isPlaceholderPort(ports[0])) {
                window.SerialLogHub.clearConnectionCookie();
                setConnectedUi(false);
                appendTerminal(`WARN: Active port ${conn.currentPort} is no longer available.`);
                toastr.warning(`Port ${conn.currentPort} was disconnected.`);
            }
        } catch {
            // ignore transient poll error
        }
    }

    async function sendCommand(message) {
        const conn = window.SerialLogHub.getConnectionCookie();
        if (!conn || !conn.currentPort) {
            toastr.warning("Please connect to a COM port first.");
            return;
        }

        appendTerminal(`TX -> ${message}`);
        window.SerialLogHub.showLoading(`Sending ${message}...`);
        try {
            const res = await $.ajax({
                type: "POST",
                url: "/Home/Send",
                contentType: "application/json",
                data: JSON.stringify({
                    MessageToSend: message,
                    SelectedPort: conn.currentPort,
                    SelectedBaudRate: conn.currentBaud
                })
            });
            window.SerialLogHub.hideLoading();
            if (res.status === "success") {
                appendTerminal(`RX <- ${res.responseMessage || "(empty)"}`);
                if (res.responseMessage && res.responseMessage.includes("CMD,OK,")) {
                    const parsed = parseCsv(res.responseMessage);
                    if (parsed.rows.length > 0) {
                        applySerialDumpOnce(res.responseMessage, `${conn.currentPort}_log.csv`, `Loaded ${parsed.rows.length} readings from device.`);
                    }
                }
            } else {
                appendTerminal(`ERROR: ${res.message || "Device returned an error."}`);
                toastr.error(res.message || "Command failed.");
            }
            refreshCommandHistory();
        } catch {
            window.SerialLogHub.hideLoading();
            toastr.error("Failed to send command.");
        }
    }

    async function runDeviceDiagnostics() {
        const conn = window.SerialLogHub.getConnectionCookie();
        if (!conn || !conn.currentPort) {
            toastr.warning("Please connect to a COM port first.");
            return;
        }

        const strip = document.getElementById("deviceDiagStrip");
        const labelEl = document.getElementById("diagLabel");
        const battEl = document.getElementById("diagBattery");
        const clockEl = document.getElementById("diagClock");

        window.SerialLogHub.showLoading("Running device diagnostics (WLW / WLJ / WLH0)...");
        appendTerminal("DIAG -> Querying Label (WLW), Battery (WLJ), RTC Clock (WLH0)...");
        const payload = { SelectedPort: conn.currentPort, SelectedBaudRate: conn.currentBaud };
        const extractDiag = (res, prefix) => {
            if (!res || res.status !== "success") return res?.message || "Error";
            const raw = typeof res.responseMessage === "string"
                ? res.responseMessage
                : (res?.responseMessage?.response || res?.response || "");
            if (!raw) return "--";
            return prefix && raw.startsWith(prefix) ? raw.slice(prefix.length).trim() : raw;
        };

        try {
            const commRes = await $.get("/Settings/GetComm", payload);
            const battRes = await $.get("/Settings/GetBattery", payload);
            const timeRes = await $.post("/Settings/GetCalibrationTime", payload);

            const labelText = extractDiag(commRes, "CMD,OK,COMM,");
            const battRaw = extractDiag(battRes, "CMD,OK,BAT,");
            const battText = battRes?.status === "success" && !battRaw.endsWith("%") ? `${battRaw}%` : battRaw;
            const clockText = extractDiag(timeRes, "CMD,OK,TIME,");

            if (labelEl) labelEl.textContent = labelText;
            if (battEl) battEl.textContent = battText;
            if (clockEl) clockEl.textContent = clockText;
            if (strip) strip.style.display = "block";

            appendTerminal(`DIAG <- Label: ${labelText} | Battery: ${battText} | RTC: ${clockText}`);
            toastr.success("Device diagnostics completed.");
            window.SerialLogHub.refreshBattery();
            refreshCommandHistory();
        } catch {
            appendTerminal("DIAG ERROR: Failed to query device diagnostics.");
            toastr.error("Device diagnostics failed.");
        } finally {
            window.SerialLogHub.hideLoading();
        }
    }

    window.onSerialDataDumpReceived = function (rawCsv) {
        if (rawCsv && rawCsv.includes("CMD,OK,")) {
            applySerialDumpOnce(rawCsv, "SerialData.csv", "Telemetry stream received from Desktop COM Bridge.");
        }
    };

    document.addEventListener("DOMContentLoaded", function () {
        renderHistoryTable();
        refreshCommandHistory();
        refreshServerDevices();

        const loadServerBtn = document.getElementById("loadServerTelemetryBtn");
        if (loadServerBtn) loadServerBtn.addEventListener("click", loadServerTelemetryHistory);

        const compareBtn = document.getElementById("compareDevicesBtn");
        if (compareBtn) compareBtn.addEventListener("click", compareServerDevices);

        const existingConn = window.SerialLogHub.getConnectionCookie();
        if (existingConn && existingConn.Connected) {
            setConnectedUi(true, existingConn.currentPort, existingConn.currentBaud);
        }

        setInterval(refreshPorts, 4000);
        document.getElementById("refreshPortsBtn").addEventListener("click", () => {
            lastPortListKey = "";
            refreshPorts();
        });

        const refreshHistoryBtn = document.getElementById("refreshHistoryBtn");
        if (refreshHistoryBtn) {
            refreshHistoryBtn.addEventListener("click", refreshCommandHistory);
        }

        const diagBtn = document.getElementById("deviceDiagnosticsBtn");
        if (diagBtn) {
            diagBtn.addEventListener("click", runDeviceDiagnostics);
        }

        document.getElementById("fileInput").addEventListener("change", function () {
            if (this.files.length > 0) {
                document.getElementById("fileNameDisplay").textContent = this.files[0].name;
            }
        });

        document.getElementById("loadCsvBtn").addEventListener("click", function () {
            const input = document.getElementById("fileInput");
            if (!input.files.length) {
                toastr.warning("Please select a CSV file first.");
                return;
            }
            const file = input.files[0];
            const reader = new FileReader();
            reader.onload = (e) => applyDataset(e.target.result, "CSV Upload", file.name, false);
            reader.readAsText(file);
        });

        const demoBtn = document.getElementById("loadDemoCsvBtn");
        if (demoBtn) {
            demoBtn.addEventListener("click", async function () {
                const res = await fetch("/Home/DemoSampleCsv");
                if (!res.ok) { toastr.error("Demo sample data unavailable."); return; }
                const csv = await res.text();
                applyDataset(csv, "Demo Sample", "sample_datalogger_demo.csv", true);
                toastr.success("Loaded synthetic demo telemetry (120 readings, 2 injected anomalies).");
            });
        }

        document.getElementById("resetGraphBtn").addEventListener("click", function () {
            originalData = [];
            filteredData = [];
            destroyAll();
            document.getElementById("graphContainer").innerHTML = "";
            document.getElementById("kpiTotalRows").textContent = "0";
            document.getElementById("kpiAnomalyCount").textContent = "0";
            document.getElementById("datasetSourceBadge").style.display = "none";
            updateTelemetryStats([]);
            renderTable();
        });

        document.getElementById("exportExcelBtn").addEventListener("click", async function () {
            if (!filteredData.length) { toastr.warning("No data to export."); return; }
            const wb = new ExcelJS.Workbook();
            const ws = wb.addWorksheet("Telemetry");
            ws.addRow(["ID", "Temperature (°C)", "Humidity (%)", "Date", "Time", "Anomaly"]);
            filteredData.forEach(r => ws.addRow([
                r.id, r.temperature, r.humidity, r.date, r.clock,
                (r.temperatureAnomaly || r.humidityAnomaly) ? "YES" : "NO"
            ]));
            const buf = await wb.xlsx.writeBuffer();
            saveAs(new Blob([buf], { type: "application/octet-stream" }), "serialloghub_export.xlsx");
            toastr.success("Exported Excel file.");
        });

        const toggleThresholds = document.getElementById("toggleThresholdControls");
        if (toggleThresholds) {
            toggleThresholds.addEventListener("change", function () {
                document.getElementById("thresholdControlsContainer").style.display = this.checked ? "flex" : "none";
            });
        }

        const applyThresholdsBtn = document.getElementById("applyThresholdsBtn");
        if (applyThresholdsBtn) {
            applyThresholdsBtn.addEventListener("click", function () {
                const maxTemp = parseFloat(document.getElementById("cfgMaxTemp").value);
                const tempRate = parseFloat(document.getElementById("cfgTempRate").value);
                const humRate = parseFloat(document.getElementById("cfgHumRate").value);

                if (isNaN(maxTemp) || isNaN(tempRate) || isNaN(humRate) || tempRate <= 0 || humRate <= 0) {
                    toastr.warning("Enter valid numeric thresholds (rates must be > 0).");
                    return;
                }

                anomalyConfig.maxTemp = maxTemp;
                anomalyConfig.tempRate = tempRate;
                anomalyConfig.humRate = humRate;

                if (!originalData.length) {
                    toastr.info("Anomaly thresholds updated for the next dataset.");
                    return;
                }

                updateAnomalyDisplayAndCharts();
                toastr.success(`Recalculated anomalies (Max Temp > ${maxTemp}°C, ΔT > ${tempRate}°C/10s, ΔH > ${humRate}%/10s).`);
            });
        }

        document.getElementById("btnAllData").addEventListener("click", function () {
            activeTab = "all";
            this.classList.add("active");
            document.getElementById("btnFaultyData").classList.remove("active");
            applyFilters();
        });

        document.getElementById("btnFaultyData").addEventListener("click", function () {
            activeTab = "anomalies";
            this.classList.add("active");
            document.getElementById("btnAllData").classList.remove("active");
            applyFilters();
        });

        document.getElementById("indexSearch").addEventListener("input", applyFilters);

        document.getElementById("toggleTimeRange").addEventListener("change", function () {
            document.getElementById("timeRangeContainer").style.display = this.checked ? "flex" : "none";
            applyFilters();
        });

        document.getElementById("timeFilterBtn").addEventListener("click", function () {
            const s = document.getElementById("startDateTime").value;
            const e = document.getElementById("endDateTime").value;
            if (!s || !e) { toastr.warning("Select both start and end timestamps."); return; }
            applyFilters();
        });

        document.getElementById("prevPage").addEventListener("click", () => {
            if (currentPage > 1) { currentPage--; renderTable(); }
        });
        document.getElementById("nextPage").addEventListener("click", () => {
            const total = Math.ceil(filteredData.length / pageSize);
            if (currentPage < total) { currentPage++; renderTable(); }
        });

        document.getElementById("connectBtn").addEventListener("click", async function () {
            const port = document.getElementById("selectedPort").value;
            const baud = parseInt(document.getElementById("selectedBaudRate").value, 10) || 9600;
            if (isPlaceholderPort(port)) {
                toastr.warning("Select a valid COM port before connecting.");
                return;
            }

            window.SerialLogHub.showLoading(`Testing connection on ${port}...`);
            appendTerminal(`Connecting to ${port} @ ${baud}...`);
            try {
                const res = await $.ajax({
                    type: "POST",
                    url: "/Home/TestConnection",
                    data: { SelectedPort: port, SelectedBaudRate: baud }
                });
                window.SerialLogHub.hideLoading();
                const msg = res?.responseMessage?.response || "";
                const connCookie = window.SerialLogHub.getConnectionCookie();
                if (res.status === "success" && connCookie && connCookie.Connected) {
                    setConnectedUi(true, port, baud);
                    appendTerminal(`CONNECTED <- ${msg}`);
                    toastr.success(`Connected to ${port}.`);
                    window.SerialLogHub.refreshBattery();
                } else {
                    window.SerialLogHub.clearConnectionCookie();
                    setConnectedUi(false);
                    const errText = res.message || msg || "Device did not respond with a valid DataLogger handshake.";
                    appendTerminal(`FAILED: ${errText}`);
                    toastr.error(errText);
                }
                refreshCommandHistory();
            } catch {
                window.SerialLogHub.hideLoading();
                setConnectedUi(false);
                toastr.error("Connection test failed.");
            }
        });

        document.getElementById("disconnectBtn").addEventListener("click", async function () {
            await fetch("/Home/Disconnect");
            window.SerialLogHub.clearConnectionCookie();
            setConnectedUi(false);
            appendTerminal("Disconnected.");
            toastr.info("Disconnected from device.");
        });

        document.getElementById("serialPortForm").addEventListener("submit", function (e) {
            e.preventDefault();
            const cmd = document.getElementById("messageToSend").value.trim();
            if (cmd) sendCommand(cmd);
        });

        document.getElementById("quickReadWlbBtn").addEventListener("click", () => sendCommand("WLB"));
    });
})();
