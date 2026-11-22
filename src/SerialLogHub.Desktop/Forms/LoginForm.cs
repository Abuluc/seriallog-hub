using SerialLogHub.Desktop.Models;
using SerialLogHub.Desktop.Services;

namespace SerialLogHub.Desktop.Forms;

public sealed class LoginForm : Form
{
    private readonly BridgeConfiguration _config;
    private readonly BridgeApiClient _apiClient;
    private readonly TextBox _emailBox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;
    private readonly Button _loginButton;
    private readonly Button _demoFillButton;
    private readonly Label _statusLabel;

    public string AuthenticatedEmail { get; private set; } = string.Empty;
    public string AuthenticatedUsername { get; private set; } = string.Empty;

    public LoginForm(BridgeConfiguration config, BridgeApiClient apiClient)
    {
        _config = config;
        _apiClient = apiClient;

        Text = "SerialLog Hub — COM Port Bridge Sign In";
        ClientSize = new Size(420, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9.5f);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = Color.FromArgb(15, 23, 42)
        };
        var title = new Label
        {
            Text = "SerialLog Hub — COM Port Bridge",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14)
        };
        var subtitle = new Label
        {
            Text = "Sign in to link local COM ports with the Web Dashboard",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Location = new Point(20, 40)
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        var emailLabel = new Label { Text = "Email", Location = new Point(24, 86), AutoSize = true };
        _emailBox = new TextBox { Location = new Point(24, 108), Width = 370 };

        var userLabel = new Label { Text = "Username", Location = new Point(24, 142), AutoSize = true };
        _usernameBox = new TextBox { Location = new Point(24, 164), Width = 370 };

        var passLabel = new Label { Text = "Password", Location = new Point(24, 198), AutoSize = true };
        _passwordBox = new TextBox { Location = new Point(24, 220), Width = 370, UseSystemPasswordChar = true };

        _loginButton = new Button
        {
            Text = "Sign In",
            Location = new Point(24, 260),
            Width = 180,
            Height = 34,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _loginButton.FlatAppearance.BorderSize = 0;
        _loginButton.Click += OnLoginClick;

        _demoFillButton = new Button
        {
            Text = "Fill Demo Operator",
            Location = new Point(214, 260),
            Width = 180,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(37, 99, 235)
        };
        _demoFillButton.Click += (_, _) =>
        {
            _emailBox.Text = _config.DemoEmail;
            _usernameBox.Text = _config.DemoUsername;
            _passwordBox.Text = "Demo1234!";
        };

        _statusLabel = new Label
        {
            Location = new Point(24, 304),
            Width = 370,
            ForeColor = Color.FromArgb(220, 38, 38),
            AutoSize = false
        };

        Controls.AddRange(new Control[]
        {
            header, emailLabel, _emailBox, userLabel, _usernameBox,
            passLabel, _passwordBox, _loginButton, _demoFillButton, _statusLabel
        });

        AcceptButton = _loginButton;
    }

    private async void OnLoginClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_emailBox.Text) || string.IsNullOrWhiteSpace(_passwordBox.Text))
        {
            _statusLabel.Text = "Please enter both email and password.";
            return;
        }

        _loginButton.Enabled = false;
        _statusLabel.Text = "Authenticating...";
        _statusLabel.ForeColor = Color.FromArgb(37, 99, 235);

        try
        {
            var (success, message) = await _apiClient.LoginAsync(_emailBox.Text, _usernameBox.Text, _passwordBox.Text);
            if (!success)
            {
                _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
                _statusLabel.Text = string.IsNullOrWhiteSpace(message) ? "Invalid credentials." : message;
                return;
            }

            AuthenticatedEmail = _emailBox.Text.Trim();
            AuthenticatedUsername = _usernameBox.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
            _statusLabel.Text = $"Connection error: {ex.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                _loginButton.Enabled = true;
            }
        }
    }
}
