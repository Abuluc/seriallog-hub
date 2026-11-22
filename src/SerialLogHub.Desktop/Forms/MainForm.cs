using SerialLogHub.Desktop.Models;
using SerialLogHub.Desktop.Services;

namespace SerialLogHub.Desktop.Forms;

public sealed class MainForm : Form
{
    private readonly BridgeConfiguration _config;
    private readonly BridgeEngine _engine;
    private readonly string _userEmail;

    private readonly ListBox _portsListBox;
    private readonly ListBox _logListBox;
    private readonly CheckBox _virtualDeviceCheckBox;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _portTimer;
    private readonly System.Windows.Forms.Timer _commandTimer;
    private readonly System.Windows.Forms.Timer _heartbeatTimer;

    private bool _allowExit;

    public MainForm(BridgeConfiguration config, BridgeEngine engine, string userEmail)
    {
        _config = config;
        _engine = engine;
        _userEmail = userEmail;

        Text = $"SerialLog Hub — COM Port Bridge ({userEmail})";
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(680, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9.5f);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(15, 23, 42),
            Padding = new Padding(16, 12, 16, 12)
        };
        var title = new Label
        {
            Text = $"SerialLog Hub Bridge  |  Operator: {userEmail}",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16)
        };
        header.Controls.Add(title);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(16) };
        var portsLabel = new Label { Text = "Detected COM Ports", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Dock = DockStyle.Top, Height = 24 };
        _portsListBox = new ListBox { Dock = DockStyle.Fill };

        _virtualDeviceCheckBox = new CheckBox
        {
            Text = "Enable Virtual DataLogger (Demo)",
            Checked = _config.EnableVirtualDemoDevice,
            Dock = DockStyle.Bottom,
            Height = 32
        };
        _virtualDeviceCheckBox.CheckedChanged += async (_, _) =>
        {
            _engine.IncludeVirtualDemoDevice = _virtualDeviceCheckBox.Checked;
            await _engine.SyncPortsAsync(_userEmail, forcePublish: true);
        };

        var refreshBtn = new Button
        {
            Text = "Refresh Ports Now",
            Dock = DockStyle.Bottom,
            Height = 32,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        refreshBtn.FlatAppearance.BorderSize = 0;
        refreshBtn.Click += async (_, _) => await _engine.SyncPortsAsync(_userEmail, forcePublish: true);

        leftPanel.Controls.Add(_portsListBox);
        leftPanel.Controls.Add(portsLabel);
        leftPanel.Controls.Add(refreshBtn);
        leftPanel.Controls.Add(_virtualDeviceCheckBox);

        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 16, 16, 16) };
        var logLabel = new Label { Text = "Bridge Activity & Serial Protocol Log", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Dock = DockStyle.Top, Height = 24 };
        _logListBox = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

        var clearBtn = new Button
        {
            Text = "Clear Log",
            Dock = DockStyle.Bottom,
            Height = 32,
            FlatStyle = FlatStyle.Flat
        };
        clearBtn.Click += (_, _) => _logListBox.Items.Clear();

        rightPanel.Controls.Add(_logListBox);
        rightPanel.Controls.Add(logLabel);
        rightPanel.Controls.Add(clearBtn);

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);
        Controls.Add(header);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SerialLog Hub COM Bridge",
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Bridge", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => { _allowExit = true; Close(); });
        _trayIcon.ContextMenuStrip = trayMenu;

        _engine.IncludeVirtualDemoDevice = _config.EnableVirtualDemoDevice;
        _engine.LogReceived += AppendLog;
        _engine.PortsUpdated += UpdatePortsList;

        _portTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, _config.PortScanIntervalMs) };
        _portTimer.Tick += async (_, _) => await _engine.SyncPortsAsync(_userEmail);

        _commandTimer = new System.Windows.Forms.Timer { Interval = Math.Max(500, _config.CommandPollIntervalMs) };
        _commandTimer.Tick += async (_, _) => await _engine.PollAndExecuteNextCommandAsync(_userEmail);

        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1500, _config.HeartbeatIntervalMs) };
        _heartbeatTimer.Tick += async (_, _) => await _engine.SendHeartbeatAsync(_userEmail);

        Load += async (_, _) =>
        {
            AppendLog("Bridge started. Scanning COM ports...");
            await _engine.SyncPortsAsync(_userEmail, forcePublish: true);
            await _engine.SendHeartbeatAsync(_userEmail);
            _portTimer.Start();
            _commandTimer.Start();
            _heartbeatTimer.Start();
        };

        FormClosing += OnMainFormClosing;
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            var choice = MessageBox.Show(
                "Minimize SerialLog Hub Bridge to the system tray instead of exiting?",
                "SerialLog Hub Bridge",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (choice == DialogResult.Yes)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.Visible = true;
                return;
            }

            if (choice == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        _portTimer.Stop();
        _commandTimer.Stop();
        _heartbeatTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }

        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logListBox.Items.Insert(0, entry);
        if (_logListBox.Items.Count > 500)
        {
            _logListBox.Items.RemoveAt(_logListBox.Items.Count - 1);
        }
    }

    private void UpdatePortsList(IReadOnlyList<string> ports)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdatePortsList(ports));
            return;
        }

        _portsListBox.Items.Clear();
        foreach (string port in ports)
        {
            _portsListBox.Items.Add(port);
        }
    }
}
