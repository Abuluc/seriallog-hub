(function () {
    "use strict";

    toastr.options = {
        closeButton: true,
        progressBar: true,
        positionClass: "toast-top-right",
        timeOut: "4000"
    };

    window.SerialLogHub = {
        getConnectionCookie: function () {
            const row = document.cookie
                .split("; ")
                .find(r => r.startsWith("Connection="));
            if (!row) return null;
            try {
                return JSON.parse(decodeURIComponent(row.substring("Connection=".length)));
            } catch {
                return null;
            }
        },

        clearConnectionCookie: function () {
            document.cookie = "Connection=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
        },

        showLoading: function (message) {
            const textEl = document.getElementById("loadingOverlayText");
            if (textEl && message) textEl.textContent = message;
            const overlay = document.getElementById("loadingOverlay");
            if (overlay) overlay.style.display = "flex";
        },

        hideLoading: function () {
            const overlay = document.getElementById("loadingOverlay");
            if (overlay) overlay.style.display = "none";
        },

        refreshBattery: async function () {
            const conn = this.getConnectionCookie();
            if (!conn || !conn.currentPort || !conn.currentBaud) return;

            try {
                const res = await $.ajax({
                    type: "GET",
                    url: "/Settings/GetBattery",
                    data: {
                        SelectedPort: conn.currentPort,
                        SelectedBaudRate: conn.currentBaud
                    }
                });

                if (res && res.status === "success" && res.result) {
                    const parts = res.result.split(",");
                    const pct = Math.max(0, Math.min(100, parseFloat(parts[3]) || 0));
                    const bar = document.getElementById("batteryLevelBar");
                    const txt = document.getElementById("batteryPercentageText");
                    if (bar) {
                        bar.style.width = pct + "%";
                        bar.style.background = pct < 20 ? "#ef4444" : pct < 50 ? "#f59e0b" : "#22c55e";
                    }
                    if (txt) txt.textContent = pct + "%";
                }
            } catch {
                // Silent when device is idle or offline
            }
        }
    };

    function updateClock() {
        const now = new Date();
        const clockEl = document.getElementById("slhClock");
        const dateEl = document.getElementById("slhDate");
        if (clockEl) {
            clockEl.textContent = now.toTimeString().slice(0, 8);
        }
        if (dateEl) {
            const d = String(now.getDate()).padStart(2, "0");
            const m = String(now.getMonth() + 1).padStart(2, "0");
            dateEl.textContent = `${d}/${m}/${now.getFullYear()}`;
        }
    }

    function loadUserProfile() {
        $.ajax({
            url: "/GetUserName",
            type: "POST",
            dataType: "json",
            success: function (data) {
                if (data && data.username) {
                    const el = document.getElementById("currentUserName");
                    if (el) el.textContent = data.username;
                }
            }
        });
    }

    function setupNavigationAndLogout() {
        const logoutBtn = document.getElementById("logoutBtn");
        if (logoutBtn) {
            logoutBtn.addEventListener("click", function () {
                $.ajax({
                    url: "/api/Auth/logout",
                    type: "POST",
                    success: function () {
                        window.SerialLogHub.clearConnectionCookie();
                        window.location.href = "/Login";
                    },
                    error: function () {
                        toastr.error("Sign out failed.");
                    }
                });
            });
        }

        document.querySelectorAll(".slh-device-link").forEach(link => {
            link.addEventListener("click", function (e) {
                const conn = window.SerialLogHub.getConnectionCookie();
                if (!conn || !conn.currentPort) {
                    e.preventDefault();
                    toastr.warning("Connect to a DataLogger COM port first to configure device settings.");
                }
            });
        });
    }

    let bridgeInactivityTimer = null;

    async function startSignalR() {
        if (typeof signalR === "undefined") return;

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/notificationHub")
            .withAutomaticReconnect()
            .build();

        connection.on("ReceiveMessage", (appName) => {
            if (appName !== "ConsoleApp") return;

            const dot = document.getElementById("bridgeStatusDot");
            const txt = document.getElementById("bridgeStatusText");
            const kpi = document.getElementById("kpiBridgeStatus");
            if (dot) { dot.classList.remove("offline"); dot.classList.add("online"); }
            if (txt) txt.textContent = "Bridge Online";
            if (kpi) kpi.textContent = "Online";

            if (bridgeInactivityTimer) clearTimeout(bridgeInactivityTimer);
            bridgeInactivityTimer = setTimeout(() => {
                if (dot) { dot.classList.remove("online"); dot.classList.add("offline"); }
                if (txt) txt.textContent = "Bridge Offline";
                if (kpi) kpi.textContent = "Offline";
            }, 6000);
        });

        connection.on("DataReading", (appName, message, responseMessage) => {
            if (message !== "Finish") {
                window.SerialLogHub.showLoading(message || "Reading DataLogger telemetry...");
                return;
            }

            window.SerialLogHub.hideLoading();
            if (typeof window.onSerialDataDumpReceived === "function") {
                window.onSerialDataDumpReceived(responseMessage);
            }
        });

        try {
            await connection.start();
            window.addEventListener("beforeunload", () => {
                connection.invoke("UserDisconnected").catch(() => { });
            });
        } catch {
            setTimeout(startSignalR, 5000);
        }
    }

    document.addEventListener("DOMContentLoaded", function () {
        updateClock();
        setInterval(updateClock, 1000);
        loadUserProfile();
        setupNavigationAndLogout();
        startSignalR();
        window.SerialLogHub.refreshBattery();
    });
})();
