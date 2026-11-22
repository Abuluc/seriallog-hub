(function () {
    "use strict";

    let csvDB = null;
    let plotInstance = null;
    let histChart = null;
    let scatterChart = null;
    let doughnutChart = null;
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

    function escapeHtml(value) {
        return String(value ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function arrayMin(arr) {
        let min = Infinity;
        for (let i = 0; i < arr.length; i++) {
            if (arr[i] < min) min = arr[i];
        }
        return min;
    }

    function arrayMax(arr) {
        let max = -Infinity;
        for (let i = 0; i < arr.length; i++) {
            if (arr[i] > max) max = arr[i];
        }
        return max;
    }

    function isPlaceholderPort(port) {
        if (!port) return true;
        const p = port.trim();
        return !p || p.startsWith("No COM") || p.startsWith("Error:");
    }

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
     * Combined anomaly detector with user-adjustable thresholds:
     * 1. Rate-of-change spike detection (per 10s normalized)
     * 2. Absolute threshold & out-of-range detection
     */
    function detectAnomalies(rows) {
        const tempRateMax = anomalyConfig.tempRate;
        const humRateMax = anomalyConfig.humRate;
        const tempMaxThreshold = anomalyConfig.maxTemp;

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
                        `<div><strong>Time:</strong> ${escapeHtml(chartData.timeLabels[idx])}</div>` +
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
        if (plotInstance) {
            plotInstance.destroy();
            plotInstance = null;
        }
        const container = document.getElementById("graphContainer");
        container.innerHTML = "";
        if (!chartData.temperatureData.length) return;

        const width = container.offsetWidth || 800;
        const height = 320;
        const minVal = Math.min(arrayMin(chartData.temperatureData), arrayMin(chartData.humidityData));
        const maxVal = Math.max(arrayMax(chartData.temperatureData), arrayMax(chartData.humidityData));
        const yMin = Math.floor(minVal - 2);
        const yMax = Math.ceil(maxVal + 2);

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

    function updateTelemetryStats(rows) {
        const setVal = (id, text) => {
            const el = document.getElementById(id);
            if (el) el.textContent = text;
        };

        if (!rows || !rows.length) {
            setVal("statMinTemp", "-- °C");
            setVal("statAvgTemp", "-- °C");
            setVal("statMaxTemp", "-- °C");
            setVal("statMinHum", "-- %");
            setVal("statAvgHum", "-- %");
            setVal("statMaxHum", "-- %");
            return;
        }

        const temps = rows.map(r => r.temperature);
        const hums = rows.map(r => r.humidity);
        const avg = arr => arr.reduce((a, b) => a + b, 0) / arr.length;

        setVal("statMinTemp", `${arrayMin(temps).toFixed(2)} °C`);
        setVal("statAvgTemp", `${avg(temps).toFixed(2)} °C`);
        setVal("statMaxTemp", `${arrayMax(temps).toFixed(2)} °C`);
        setVal("statMinHum", `${arrayMin(hums).toFixed(2)} %`);
        setVal("statAvgHum", `${avg(hums).toFixed(2)} %`);
        setVal("statMaxHum", `${arrayMax(hums).toFixed(2)} %`);
    }

    function destroyAnalyticsCharts() {
        if (histChart) { histChart.destroy(); histChart = null; }
        if (scatterChart) { scatterChart.destroy(); scatterChart = null; }
        if (doughnutChart) { doughnutChart.destroy(); doughnutChart = null; }
    }

    function renderAnalyticsCharts(rows) {
        destroyAnalyticsCharts();
        if (typeof Chart === "undefined" || !rows || !rows.length) return;

        // 1. Temperature Distribution Histogram
        const buckets = [
            { label: "< 20°C", count: 0 },
            { label: "20–22°C", count: 0 },
            { label: "22–24°C", count: 0 },
            { label: "24–26°C", count: 0 },
            { label: "> 26°C", count: 0 }
        ];
        rows.forEach(r => {
            if (r.temperature < 20) buckets[0].count++;
            else if (r.temperature < 22) buckets[1].count++;
            else if (r.temperature < 24) buckets[2].count++;
            else if (r.temperature < 26) buckets[3].count++;
            else buckets[4].count++;
        });

        const histCtx = document.getElementById("tempHistogramChart");
        if (histCtx) {
            histChart = new Chart(histCtx, {
                type: "bar",
                data: {
                    labels: buckets.map(b => b.label),
                    datasets: [{
                        label: "Readings",
                        data: buckets.map(b => b.count),
                        backgroundColor: ["#38bdf8", "#3b82f6", "#2563eb", "#f59e0b", "#ef4444"],
                        borderRadius: 6
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: { legend: { display: false } },
                    scales: { y: { beginAtZero: true, ticks: { precision: 0 } } }
                }
            });
        }

        // 2. Temperature vs Humidity Scatter Plot
        const normalPts = [];
        const anomalyPts = [];
        rows.forEach(r => {
            const pt = { x: r.temperature, y: r.humidity };
            if (r.temperatureAnomaly || r.humidityAnomaly) anomalyPts.push(pt);
            else normalPts.push(pt);
        });

        const scatterCtx = document.getElementById("tempHumScatterChart");
        if (scatterCtx) {
            scatterChart = new Chart(scatterCtx, {
                type: "scatter",
                data: {
                    datasets: [
                        { label: "Normal", data: normalPts, backgroundColor: "rgba(37, 99, 235, 0.65)", pointRadius: 4 },
                        { label: "Anomaly", data: anomalyPts, backgroundColor: "#dc2626", pointRadius: 6 }
                    ]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    scales: {
                        x: { title: { display: true, text: "Temperature (°C)" } },
                        y: { title: { display: true, text: "Humidity (%)" } }
                    }
                }
            });
        }

        // 3. Health & Anomaly Breakdown Doughnut
        const tempOnly = rows.filter(r => r.temperatureAnomaly && !r.humidityAnomaly).length;
        const humOnly = rows.filter(r => !r.temperatureAnomaly && r.humidityAnomaly).length;
        const both = rows.filter(r => r.temperatureAnomaly && r.humidityAnomaly).length;
        const normal = rows.length - tempOnly - humOnly - both;

        const doughnutCtx = document.getElementById("anomalyDoughnutChart");
        if (doughnutCtx) {
            doughnutChart = new Chart(doughnutCtx, {
                type: "doughnut",
                data: {
                    labels: ["Normal", "Temp Anomaly", "Hum Anomaly", "Dual Spike"],
                    datasets: [{
                        data: [normal, tempOnly, humOnly, both],
                        backgroundColor: ["#16a34a", "#ef4444", "#f59e0b", "#7f1d1d"],
                        borderWidth: 2,
                        borderColor: "#ffffff"
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    cutout: "62%",
                    plugins: { legend: { position: "bottom" } }
                }
            });
        }
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

            tr.innerHTML = `<td>${r.id}</td><td>${r.temperature.toFixed(2)}</td><td>${r.humidity.toFixed(2)}</td><td>${escapeHtml(r.date)}</td><td>${escapeHtml(r.clock)}</td><td>${statusHtml}</td>`;
            frag.appendChild(tr);
        });

        tbody.appendChild(frag);
        document.getElementById("pageInfo").textContent = `Page ${currentPage} / ${totalPages}`;
    }

    function updateAnomalyDisplayAndCharts() {
        if (!originalData.length) return;
        detectAnomalies(originalData);
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

        originalData = detectAnomalies(parsed.rows);
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
                originalData = detectAnomalies(parsed.rows);
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
            const res = await fetch("/Home/Getavabileport");
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
                // Connected port disappeared from bridge port list
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

            const labelText = extractDiag(commRes, "CMD,OK,COMMENT,");
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
            if (plotInstance) {
                plotInstance.destroy();
                plotInstance = null;
            }
            document.getElementById("graphContainer").innerHTML = "";
            document.getElementById("kpiTotalRows").textContent = "0";
            document.getElementById("kpiAnomalyCount").textContent = "0";
            document.getElementById("datasetSourceBadge").style.display = "none";
            updateTelemetryStats([]);
            destroyAnalyticsCharts();
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

        // Adjustable Anomaly Thresholds
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
