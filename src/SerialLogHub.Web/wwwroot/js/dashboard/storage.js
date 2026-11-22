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
     * Combined anomaly detector with user-adjustable thresholds:
     * 1. Rate-of-change spike detection (per 10s normalized)
     * 2. Absolute threshold & out-of-range detection
     */
    function detectAnomalies(rows, config) {
        const tempRateMax = config?.tempRate ?? 3.0;
        const humRateMax = config?.humRate ?? 15.0;
        const tempMaxThreshold = config?.maxTemp ?? 40.0;

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
