using System.Text.Json;
using SerialLogHub.Desktop.Models;
using SerialLogHub.Desktop.Services;

namespace SerialLogHub.Desktop.Forms;

public sealed class LoginForm : Form
{
    private readonly BridgeConfiguration _config;
    private readonly BridgeApiClient _apiClient;
    private readonly TextBox _identifierBox;
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
        ClientSize = new Size(420, 290);
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

        var idLabel = new Label { Text = "Email or Username", Location = new Point(24, 86), AutoSize = true };
        _identifierBox = new TextBox { Location = new Point(24, 108), Width = 370 };

        var passLabel = new Label { Text = "Password", Location = new Point(24, 144), AutoSize = true };
        _passwordBox = new TextBox { Location = new Point(24, 166), Width = 370, UseSystemPasswordChar = true };

        _loginButton = new Button
        {
            Text = "Sign In",
            Location = new Point(24, 210),
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
            Location = new Point(214, 210),
            Width = 180,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(37, 99, 235)
        };
        _demoFillButton.Click += (_, _) =>
        {
            _identifierBox.Text = _config.DemoEmail;
            _passwordBox.Text = "Demo1234!";
        };

        _statusLabel = new Label
        {
            Location = new Point(24, 254),
            Width = 370,
            ForeColor = Color.FromArgb(220, 38, 38),
            AutoSize = false
        };

        Controls.AddRange(new Control[]
        {
            header, idLabel, _identifierBox,
            passLabel, _passwordBox, _loginButton, _demoFillButton, _statusLabel
        });

        AcceptButton = _loginButton;
    }

    private async void OnLoginClick(object? sender, EventArgs e)
    {
        string identifier = _identifierBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(_passwordBox.Text))
        {
            _statusLabel.Text = "Please enter your email/username and password.";
            return;
        }

        _loginButton.Enabled = false;
        _statusLabel.Text = "Authenticating...";
        _statusLabel.ForeColor = Color.FromArgb(37, 99, 235);

        try
        {
            var (success, message) = await _apiClient.LoginAsync(identifier, identifier, _passwordBox.Text);
            if (!success)
            {
                _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
                _statusLabel.Text = string.IsNullOrWhiteSpace(message) ? "Invalid credentials." : message;
                return;
            }

            string resolvedEmail = identifier;
            string resolvedUsername = identifier;
            try
            {
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("email", out var ep) && !string.IsNullOrWhiteSpace(ep.GetString()))
                {
                    resolvedEmail = ep.GetString()!;
                }
                if (doc.RootElement.TryGetProperty("username", out var up) && !string.IsNullOrWhiteSpace(up.GetString()))
                {
                    resolvedUsername = up.GetString()!;
                }
            }
            catch
            {
                // Fallback to identifier
            }

            AuthenticatedEmail = resolvedEmail.Trim();
            AuthenticatedUsername = resolvedUsername.Trim();
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
