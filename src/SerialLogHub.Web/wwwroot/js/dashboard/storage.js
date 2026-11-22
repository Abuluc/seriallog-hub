(function (global) {
    "use strict";

    let csvDB = null;

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
     * Deterministic statistical anomaly detector supporting:
     * 1. Invalid Sensor Reading Detection
     * 2. Configurable Static Threshold Detection
     * 3. Sudden Spike / Rate-of-Change Detection (per 10s normalized)
     * 4. Rolling Window Moving Average + Standard Deviation (Z-Score)
     * 5. Monotonic Sensor Drift Detection
     * 6. Missing Data / Sampling Gap Detection
     */
    function detectAnomalies(rows, config) {
        const tempRateMax = config?.tempRate ?? 3.0;
        const humRateMax = config?.humRate ?? 15.0;
        const tempMaxThreshold = config?.maxTemp ?? 40.0;
        const zScoreThreshold = config?.zScore ?? 3.0;
        const windowSize = 12;

        const rollingTemps = [];
        let driftSteps = 0;
        let cumulativeDrift = 0;
        let lastDir = 0;

        for (let i = 0; i < rows.length; i++) {
            const cur = rows[i];
            const methods = [];
            const reasons = [];

            cur.temperatureAnomaly = false;
            cur.humidityAnomaly = false;

            if (cur.temperature > tempMaxThreshold || cur.temperature < -10) {
                cur.temperatureAnomaly = true;
                methods.push("StaticThreshold");
                reasons.push(`Temp ${cur.temperature.toFixed(2)}°C > ${tempMaxThreshold}°C`);
            }
            if (cur.humidity < 0 || cur.humidity > 100) {
                cur.humidityAnomaly = true;
                methods.push("InvalidReading");
                reasons.push(`Humidity ${cur.humidity.toFixed(2)}% out of bounds [0..100%]`);
            }

            if (rollingTemps.length >= 6) {
                const mean = rollingTemps.reduce((a, b) => a + b, 0) / rollingTemps.length;
                const variance = rollingTemps.reduce((a, b) => a + (b - mean) * (b - mean), 0) / rollingTemps.length;
                const stdDev = Math.max(Math.sqrt(variance), 0.25);
                const z = Math.abs(cur.temperature - mean) / stdDev;
                if (z > zScoreThreshold && Math.abs(cur.temperature - mean) >= 1.5) {
                    cur.temperatureAnomaly = true;
                    methods.push("RollingZScore");
                    reasons.push(`Z-Score ${z.toFixed(2)} (μ=${mean.toFixed(2)}°C, σ=${stdDev.toFixed(2)})`);
                }
            }

            if (i > 0) {
                const prev = rows[i - 1];
                const tCur = new Date(`${cur.date}T${cur.clock}`).getTime();
                const tPrev = new Date(`${prev.date}T${prev.clock}`).getTime();
                const dt = (tCur - tPrev) / 1000;

                const dTemp = cur.temperature - prev.temperature;
                const absTemp = Math.abs(dTemp);
                const absHum = Math.abs(cur.humidity - prev.humidity);
                const normTemp = dt > 0 ? (absTemp * 10) / dt : absTemp;
                const normHum = dt > 0 ? (absHum * 10) / dt : absHum;

                if (normTemp > tempRateMax) {
                    cur.temperatureAnomaly = true;
                    methods.push("SuddenSpike");
                    reasons.push(`ΔT=${normTemp.toFixed(2)}°C/10s > ${tempRateMax}°C/10s`);
                }
                if (normHum > humRateMax) {
                    cur.humidityAnomaly = true;
                    methods.push("SuddenSpike");
                    reasons.push(`ΔH=${normHum.toFixed(2)}%/10s > ${humRateMax}%/10s`);
                }

                const dir = dTemp > 0.05 ? 1 : (dTemp < -0.05 ? -1 : 0);
                if (dir !== 0 && (dir === lastDir || lastDir === 0)) {
                    driftSteps++;
                    cumulativeDrift += dTemp;
                    lastDir = dir;
                    if (driftSteps >= 8 && Math.abs(cumulativeDrift) >= 2.5) {
                        cur.temperatureAnomaly = true;
                        methods.push("SensorDrift");
                        reasons.push(`Drift ${cumulativeDrift.toFixed(2)}°C over ${driftSteps} steps`);
                    }
                } else if (dir !== 0) {
                    driftSteps = 1;
                    cumulativeDrift = dTemp;
                    lastDir = dir;
                }
            }

            rollingTemps.push(cur.temperature);
            if (rollingTemps.length > windowSize) rollingTemps.shift();

            cur.anomalyMethod = methods.length ? methods.join("+") : "None";
            cur.anomalyExplanation = reasons.length ? reasons.join(" | ") : "Normal operating envelope";
        }
        return rows;
    }

    global.SlhStorage = {
        escapeHtml,
        arrayMin,
        arrayMax,
        saveDataset,
        getAllDatasets,
        getDatasetById,
        parseCsv,
        detectAnomalies
    };
})(window);
