(function () {
    "use strict";

    let csvDB = null;
    let plotInstance = null;
    let originalData = [];
    let filteredData = [];
    let currentPage = 1;
    const pageSize = 250;

    function initIndexedDB() {
        return new Promise((resolve, reject) => {
            if (csvDB) { resolve(csvDB); return; }
            const req = indexedDB.open("serialLogHubDB", 1);
            req.onupgradeneeded = (e) => {
                const db = e.target.result;
                if (!db.objectStoreNames.contains("csvDataStore")) {
                    db.createObjectStore("csvDataStore", { keyPath: "id", autoIncrement: true });
                }
            };
            req.onsuccess = (e) => { csvDB = e.target.result; resolve(csvDB); };
            req.onerror = (e) => reject(e);
        });
    }

    async function saveDataset(content, sourceType, parsedChart, fileName) {
        await initIndexedDB();
        const now = new Date();
        const payload = {
            fileName: fileName || (sourceType === "Serial Port" ? "SerialData.csv" : "Dataset.csv"),
            rowCount: parsedChart.temperatureData.length,
            uploadDate: now.toLocaleDateString(),
            uploadTime: now.toLocaleTimeString(),
            csvContent: content,
            situation: sourceType,
            parsedData: parsedChart
        };

        return new Promise((resolve, reject) => {
            const tx = csvDB.transaction("csvDataStore", "readwrite");
            const req = tx.objectStore("csvDataStore").add({ data: JSON.stringify(payload) });
            req.onsuccess = (e) => resolve(e.target.result);
            req.onerror = (e) => reject(e);
        });
    }

    async function getAllDatasets() {
        await initIndexedDB();
        return new Promise((resolve, reject) => {
            const tx = csvDB.transaction("csvDataStore", "readonly");
            const req = tx.objectStore("csvDataStore").getAll();
            req.onsuccess = () => resolve(req.result || []);
            req.onerror = (e) => reject(e);
        });
    }

    async function getDatasetById(id) {
        await initIndexedDB();
        return new Promise((resolve, reject) => {
            const tx = csvDB.transaction("csvDataStore", "readonly");
            const req = tx.objectStore("csvDataStore").get(Number(id));
            req.onsuccess = () => resolve(req.result);
            req.onerror = (e) => reject(e);
        });
    }

    function parseCsv(csv) {
        const lines = csv.split(/\r?\n/);
        const temperatureData = [];
        const humidityData = [];
        const timeLabels = [];
        const rows = [];
        let id = 1;

        for (let i = 1; i < lines.length; i++) {
            const line = lines[i].trim();
            if (!line) continue;
            const cols = line.split(",");
            if (cols.length < 10) continue;

            const temp = parseFloat(cols[2]);
            const hum = parseFloat(cols[3]);
            if (isNaN(temp) || isNaN(hum)) continue;

            const year = cols[4].trim();
            const month = cols[5].trim().padStart(2, "0");
            const day = cols[6].trim().padStart(2, "0");
            const hour = cols[7].trim().padStart(2, "0");
            const minute = cols[8].trim().padStart(2, "0");
            const second = cols[9].trim().padStart(2, "0");

            const isoDate = `${year}-${month}-${day}`;
            const clock = `${hour}:${minute}:${second}`;
            const label = `${isoDate} ${clock}`;

            timeLabels.push(label);
            temperatureData.push(temp);
            humidityData.push(hum);
            rows.push({ id: id++, temperature: temp, humidity: hum, date: isoDate, clock: clock });
        }

        return { chart: { timeLabels, temperatureData, humidityData }, rows };
    }

    /**
     * Combined anomaly detector:
     * 1. Rate-of-change spike detection (from WhiloggerWEB)
     * 2. Absolute threshold & out-of-range detection (ported from WebPortApp-main)
     */
    function detectAnomalies(rows) {
        const tempRateMax = 3.0;
        const humRateMax = 15.0;
        const tempMaxThreshold = 40.0;

        for (let i = 0; i < rows.length; i++) {
            const cur = rows[i];
            cur.temperatureAnomaly = cur.temperature > tempMaxThreshold;
            cur.humidityAnomaly = cur.humidity < 0 || cur.humidity > 100;

            if (i > 0) {
                const prev = rows[i - 1];
                const tCur = new Date(`${cur.date}T${cur.clock}`).getTime();
                const tPrev = new Date(`${prev.date}T${prev.clock}`).getTime();
                const dt = (tCur - tPrev) / 1000;

                const dTemp = Math.abs(cur.temperature - prev.temperature);
                const dHum = Math.abs(cur.humidity - prev.humidity);
                const normTemp = dt > 0 ? (dTemp * 10) / dt : dTemp;
                const normHum = dt > 0 ? (dHum * 10) / dt : dHum;

                if (normTemp > tempRateMax) cur.temperatureAnomaly = true;
                if (normHum > humRateMax) cur.humidityAnomaly = true;
            }
        }
        return rows;
    }

    function tooltipPlugin(chartData) {
        let tooltipEl = null;
        return {
            hooks: {
                init: (u) => {
                    tooltipEl = document.createElement("div");
                    tooltipEl.className = "uplot-tooltip";
                    u.over.appendChild(tooltipEl);
                    u.over.addEventListener("mouseleave", () => { tooltipEl.style.display = "none"; });
                },
                setCursor: (u) => {
                    const idx = u.cursor.idx;
                    if (idx == null || !chartData.timeLabels[idx]) {
                        tooltipEl.style.display = "none";
                        return;
                    }
                    const xPos = u.valToPos(u.data[0][idx], "x");
                    const yPos = Math.min(u.valToPos(u.data[1][idx], "y"), u.valToPos(u.data[2][idx], "y"));
                    tooltipEl.innerHTML =
                        `<div><strong>Time:</strong> ${chartData.timeLabels[idx]}</div>` +
                        `<div><strong>Temp:</strong> ${chartData.temperatureData[idx]} °C</div>` +
                        `<div><strong>Humidity:</strong> ${chartData.humidityData[idx]} %</div>`;
                    tooltipEl.style.left = Math.max(8, xPos + 12) + "px";
                    tooltipEl.style.top = Math.max(8, yPos - 10) + "px";
                    tooltipEl.style.display = "block";
                }
            }
        };
    }

    function plotGraph(chartData) {
        const container = document.getElementById("graphContainer");
        container.innerHTML = "";
        if (!chartData.temperatureData.length) return;

        const width = container.offsetWidth || 800;
        const height = 320;
        const allVals = chartData.temperatureData.concat(chartData.humidityData);
        const yMin = Math.floor(Math.min(...allVals) - 2);
        const yMax = Math.ceil(Math.max(...allVals) + 2);

        const opts = {
            width,
            height,
            scales: {
                x: { time: false },
                y: { range: [yMin, yMax] }
            },
            axes: [
                { stroke: "#64748b", grid: { stroke: "rgba(148,163,184,0.2)" }, label: "Sample Index" },
                { stroke: "#64748b", grid: { stroke: "rgba(148,163,184,0.2)" }, label: "°C / %" }
            ],
            series: [
                { label: "Index" },
                { label: "Temperature (°C)", stroke: "#ef4444", width: 2 },
                { label: "Humidity (%)", stroke: "#2563eb", width: 2 }
            ],
            plugins: [tooltipPlugin(chartData)]
        };

        const data = [
            chartData.temperatureData.map((_, i) => i + 1),
            chartData.temperatureData,
            chartData.humidityData
        ];

        plotInstance = new uPlot(opts, data, container);
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
            let statusHtml = '<span class="slh-badge slh-badge-ok">Normal</span>';
            if (r.temperatureAnomaly && r.humidityAnomaly) {
                tr.className = "row-anomaly-both";
                statusHtml = '<span class="slh-badge slh-badge-danger">Temp + Hum Spike</span>';
            } else if (r.temperatureAnomaly) {
                tr.className = "row-anomaly-temp";
                statusHtml = '<span class="slh-badge slh-badge-danger">Temp Anomaly</span>';
            } else if (r.humidityAnomaly) {
                tr.className = "row-anomaly-hum";
                statusHtml = '<span class="slh-badge slh-badge-warn">Hum Anomaly</span>';
            }

            tr.innerHTML = `<td>${r.id}</td><td>${r.temperature.toFixed(2)}</td><td>${r.humidity.toFixed(2)}</td><td>${r.date}</td><td>${r.clock}</td><td>${statusHtml}</td>`;
            frag.appendChild(tr);
        });

        tbody.appendChild(frag);
        document.getElementById("pageInfo").textContent = `Page ${currentPage} / ${totalPages}`;
    }

    function applyDataset(csvContent, sourceLabel, fileName, isDemo) {
        const parsed = parseCsv(csvContent);
        if (!parsed.rows.length) {
            toastr.warning("No valid DataLogger rows found in dataset.");
            return;
        }

        originalData = detectAnomalies(parsed.rows);
        filteredData = originalData.slice();
        currentPage = 1;

        const anomalies = originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly).length;
        document.getElementById("kpiTotalRows").textContent = String(originalData.length);
        document.getElementById("kpiAnomalyCount").textContent = String(anomalies);

        const badge = document.getElementById("datasetSourceBadge");
        if (badge) {
            badge.style.display = isDemo ? "inline-flex" : "none";
        }

        plotGraph(parsed.chart);
        renderTable();
        saveDataset(csvContent, sourceLabel, parsed.chart, fileName).then(renderHistoryTable);
    }

    async function renderHistoryTable() {
        const entries = await getAllDatasets();
        const tbody = document.getElementById("csvTablebody");
        tbody.innerHTML = "";
        if (!entries.length) {
            tbody.innerHTML = '<tr><td colspan="7" style="text-align:center;color:#94a3b8;padding:18px;">No saved datasets in browser history yet.</td></tr>';
            return;
        }

        entries.sort((a, b) => b.id - a.id);
        entries.slice(0, 20).forEach(entry => {
            const meta = JSON.parse(entry.data);
            const tr = document.createElement("tr");
            tr.innerHTML =
                `<td>${entry.id}</td>` +
                `<td>${meta.fileName}</td>` +
                `<td>${meta.rowCount}</td>` +
                `<td>${meta.uploadDate}</td>` +
                `<td>${meta.uploadTime}</td>` +
                `<td>${meta.situation}</td>` +
                `<td><button type="button" class="slh-btn slh-btn-outline slh-btn-sm" data-load-id="${entry.id}">Load</button></td>`;
            tbody.appendChild(tr);
        });

        tbody.querySelectorAll("[data-load-id]").forEach(btn => {
            btn.addEventListener("click", async () => {
                const record = await getDatasetById(btn.dataset.loadId);
                if (!record) return;
                const meta = JSON.parse(record.data);
                const parsed = parseCsv(meta.csvContent);
                originalData = detectAnomalies(parsed.rows);
                filteredData = originalData.slice();
                currentPage = 1;
                document.getElementById("kpiTotalRows").textContent = String(originalData.length);
                document.getElementById("kpiAnomalyCount").textContent = String(originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly).length);
                plotGraph(parsed.chart);
                renderTable();
                toastr.info(`Loaded dataset #${record.id} (${meta.fileName}).`);
            });
        });
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
        const kpiPort = document.getElementById("kpiActivePort");

        if (connected) {
            portEl.disabled = true;
            baudEl.disabled = true;
            msgEl.disabled = false;
            connectBtn.style.display = "none";
            disconnectBtn.style.display = "inline-flex";
            sendBtn.style.display = "inline-flex";
            wlbBtn.style.display = "inline-flex";
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
            if (kpiPort) kpiPort.textContent = "Not Connected";
        }
    }

    async function refreshPorts() {
        try {
            const res = await fetch("/Home/Getavabileport");
            if (!res.ok) return;
            const ports = await res.json();
            const select = document.getElementById("selectedPort");
            const prev = select.value;
            select.innerHTML = "";
            ports.forEach(p => {
                const opt = document.createElement("option");
                opt.value = p;
                opt.textContent = p;
                select.appendChild(opt);
            });
            if (prev && ports.includes(prev)) {
                select.value = prev;
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
                        applyDataset(res.responseMessage, "Serial Port", `${conn.currentPort}_log.csv`, false);
                        toastr.success(`Loaded ${parsed.rows.length} readings from device.`);
                    }
                }
            } else {
                appendTerminal(`WARN: ${res.message}`);
                toastr.warning(res.message);
            }
        } catch {
            window.SerialLogHub.hideLoading();
            toastr.error("Failed to send command.");
        }
    }

    window.onSerialDataDumpReceived = function (rawCsv) {
        if (rawCsv && rawCsv.includes("CMD,OK,")) {
            applyDataset(rawCsv, "Serial Port", "SerialData.csv", false);
            toastr.success("Telemetry stream received from Desktop COM Bridge.");
        }
    };

    document.addEventListener("DOMContentLoaded", function () {
        renderHistoryTable();

        const existingConn = window.SerialLogHub.getConnectionCookie();
        if (existingConn && existingConn.Connected) {
            setConnectedUi(true, existingConn.currentPort, existingConn.currentBaud);
        }

        setInterval(refreshPorts, 4000);
        document.getElementById("refreshPortsBtn").addEventListener("click", refreshPorts);

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
            plotInstance = null;
            document.getElementById("graphContainer").innerHTML = "";
            document.getElementById("kpiTotalRows").textContent = "0";
            document.getElementById("kpiAnomalyCount").textContent = "0";
            document.getElementById("datasetSourceBadge").style.display = "none";
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

        document.getElementById("btnAllData").addEventListener("click", function () {
            this.classList.add("active");
            document.getElementById("btnFaultyData").classList.remove("active");
            filteredData = originalData.slice();
            currentPage = 1;
            renderTable();
        });

        document.getElementById("btnFaultyData").addEventListener("click", function () {
            this.classList.add("active");
            document.getElementById("btnAllData").classList.remove("active");
            filteredData = originalData.filter(r => r.temperatureAnomaly || r.humidityAnomaly);
            currentPage = 1;
            renderTable();
        });

        document.getElementById("indexSearch").addEventListener("input", function () {
            const q = this.value.trim();
            filteredData = q ? originalData.filter(r => String(r.id).includes(q)) : originalData.slice();
            currentPage = 1;
            renderTable();
        });

        document.getElementById("toggleTimeRange").addEventListener("change", function () {
            document.getElementById("timeRangeContainer").style.display = this.checked ? "flex" : "none";
        });

        document.getElementById("timeFilterBtn").addEventListener("click", function () {
            const s = document.getElementById("startDateTime").value;
            const e = document.getElementById("endDateTime").value;
            if (!s || !e) { toastr.warning("Select both start and end timestamps."); return; }
            const startMs = new Date(s).getTime();
            const endMs = new Date(e).getTime();
            filteredData = originalData.filter(r => {
                const ts = new Date(`${r.date}T${r.clock}`).getTime();
                return ts >= startMs && ts <= endMs;
            });
            currentPage = 1;
            renderTable();
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
            if (!port) { toastr.warning("Select a COM port."); return; }

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
                if (res.status === "success" && (msg.includes("OK") || msg.includes("CMD") || msg.includes("LSS"))) {
                    setConnectedUi(true, port, baud);
                    appendTerminal(`CONNECTED <- ${msg}`);
                    toastr.success(`Connected to ${port}.`);
                    window.SerialLogHub.refreshBattery();
                } else {
                    appendTerminal(`FAILED: ${res.message || msg}`);
                    toastr.error(res.message || "Device did not respond.");
                }
            } catch {
                window.SerialLogHub.hideLoading();
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
