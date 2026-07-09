// First-run linking (SDD §15 device flow): user pastes server URL (prefilled)
// and the 6-char code from the website; we claim it and store the token.
using System.Net.Http.Json;

namespace GameNight.Agent;

public sealed class LinkForm : Form
{
    private readonly TextBox _url = new() { Width = 320 };
    private readonly TextBox _code = new() { Width = 320, CharacterCasing = CharacterCasing.Upper };
    private readonly Button _ok = new() { Text = "Link this PC", Width = 320, Height = 34 };
    private readonly Label _status = new() { Width = 320, Height = 40, ForeColor = Color.IndianRed };

    public string? Token { get; private set; }
    public string ServerUrl => _url.Text.TrimEnd('/');

    public LinkForm(string defaultUrl)
    {
        Text = "GameNight — link this PC";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(360, 220);
        StartPosition = FormStartPosition.CenterScreen;

        _url.Text = defaultUrl;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16) };
        flow.Controls.Add(new Label { Text = "Server URL", Width = 320 });
        flow.Controls.Add(_url);
        flow.Controls.Add(new Label { Text = "Link code (from the website → Home → Link your PC)", Width = 320 });
        flow.Controls.Add(_code);
        flow.Controls.Add(_ok);
        flow.Controls.Add(_status);
        Controls.Add(flow);

        _ok.Click += async (_, _) => await ClaimAsync();
    }

    private async Task ClaimAsync()
    {
        _ok.Enabled = false;
        _status.Text = "claiming…";
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
            var resp = await http.PostAsJsonAsync("/api/v1/devices/claim",
                new ClaimRequest(_code.Text.Trim(), Environment.MachineName));
            ClaimResponse? body = await resp.Content.ReadFromJsonAsync<ClaimResponse>();
            if (!resp.IsSuccessStatusCode || body?.Token is null)
            {
                _status.Text = body?.Error ?? $"failed: HTTP {(int)resp.StatusCode}";
                _ok.Enabled = true;
                return;
            }
            Token = body.Token;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = $"cannot reach server: {ex.Message}";
            _ok.Enabled = true;
        }
    }
}
