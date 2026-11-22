(function (global) {
    "use strict";

    let plotInstance = null;
    let histChart = null;
    let scatterChart = null;
    let doughnutChart = null;

    function tooltipPlugin(chartData) {
        const { escapeHtml } = global.SlhStorage;
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

    function destroyPlot() {
        if (plotInstance) {
            plotInstance.destroy();
            plotInstance = null;
        }
    }

    function destroyAnalyticsCharts() {
        if (histChart) { histChart.destroy(); histChart = null; }
        if (scatterChart) { scatterChart.destroy(); scatterChart = null; }
        if (doughnutChart) { doughnutChart.destroy(); doughnutChart = null; }
    }

    function destroyAll() {
        destroyPlot();
        destroyAnalyticsCharts();
    }

    function plotGraph(chartData) {
        destroyPlot();
        const container = document.getElementById("graphContainer");
        if (!container) return;
        container.innerHTML = "";
        if (!chartData.temperatureData.length) return;

        const { arrayMin, arrayMax } = global.SlhStorage;
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

        const { arrayMin, arrayMax } = global.SlhStorage;
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

    global.SlhCharts = {
        plotGraph,
        updateTelemetryStats,
        renderAnalyticsCharts,
        destroyAll
    };
})(window);
