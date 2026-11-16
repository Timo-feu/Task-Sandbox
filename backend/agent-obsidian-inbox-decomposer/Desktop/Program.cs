using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;

namespace TaskSandbox.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const string DashboardUrl = "http://127.0.0.1:5080";
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _agentProcess;

    public MainForm()
    {
        Text = "TS — Task Sandbox";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 680);
        Size = new Size(1440, 900);
        BackColor = Color.FromArgb(17, 20, 24);
        Controls.Add(_webView);
        Load += OnLoadAsync;
        FormClosing += OnFormClosing;
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TS", "TaskSandbox", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            StartAgentBackend();
            await WaitForBackendAsync();
            _webView.Source = new Uri(DashboardUrl);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Не удалось запустить TS — Task Sandbox.\n\n{exception.Message}\n\nПроверьте наличие Microsoft Edge WebView2 Runtime и корректность сборки backend.",
                "Ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private void StartAgentBackend()
    {
        string backendPath = Path.Combine(AppContext.BaseDirectory, "Agent", "backend.dll");
        if (!File.Exists(backendPath))
            throw new FileNotFoundException("Не найдена сборка Agent\\backend.dll. Пересоберите Desktop-проект.", backendPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(backendPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(backendPath);

        _agentProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить backend агента.");
    }

    private async Task WaitForBackendAsync()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (_agentProcess is { HasExited: true })
                throw new InvalidOperationException($"Backend завершился с кодом {_agentProcess.ExitCode}.");

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    $"{DashboardUrl}/api/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }

            await Task.Delay(400);
        }

        throw new TimeoutException("Backend не ответил за 45 секунд.");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _httpClient.Dispose();

        if (_agentProcess is { HasExited: false })
        {
            try { _agentProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        _agentProcess?.Dispose();
    }
}
