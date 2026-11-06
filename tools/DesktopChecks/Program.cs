using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StardewCompanion.Core;
using StardewCompanion.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? "."), ".local", "checks", "desktop-" + Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                // Never interrupt another running Companion instance.
                using (var probe = new TcpListener(IPAddress.Loopback, 27841)) probe.Start();
                await RecoverAndRetry(Path.Combine(folder, "recovery"));
                Console.WriteLine("PASS desktop recovers settings, retries startup, saves safely and releases its port");
                await SaveAfterStartupFailure(Path.Combine(folder, "save-retry"));
                Console.WriteLine("PASS saving settings after startup failure starts the service");
                await CloseDuringStartup(Path.Combine(folder, "closing"));
                Console.WriteLine("PASS repeated close during startup waits for cleanup");
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.WriteLine("FAIL desktop checks: " + ex); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static MainWindow HiddenWindow(string data) => new(data)
    {
        Opacity = 0, ShowInTaskbar = false, ShowActivated = false
    };

    private static async Task RecoverAndRetry(string data)
    {
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "model.credentials.json");
        const string damaged = "{truncated";
        File.WriteAllText(path, damaged);
        using var occupied = new TcpListener(IPAddress.Loopback, 27841);
        occupied.Start();
        var window = HiddenWindow(data);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        try
        {
            await Wait(() => Text(window, "启动失败"), "startup failure was not shown");
            Require(Text(window, "设置文件损坏，已备份"), "configuration recovery was not shown");
            var backup = Directory.GetFiles(data, "model.credentials.json.invalid-*.bak").Single();
            Require(File.ReadAllText(backup) == damaged, "damaged settings were lost");
            occupied.Stop();
            Click(window, "重试启动本地服务");
            await Wait(() => Text(window, "服务已就绪"), "retry did not start the service");
            Require(await Healthy(data), "retried service was not reachable");

            var model = Descendants(window).OfType<TextBox>().Single(t => t.Text == "mimo-v2.6-pro");
            var endpoint = Descendants(window).OfType<TextBox>().Single(t => t.Text == "https://api.xiaomimimo.com/v1/chat/completions");
            model.Text = "desktop-recovered-model";
            Click(window, "保存设置");
            await Wait(() => ModelSettings.Load(path).Model == "desktop-recovered-model", "settings were not saved");
            var committed = File.ReadAllText(path);
            endpoint.Text = "invalid endpoint";
            Click(window, "保存设置");
            Require(File.ReadAllText(path) == committed, "invalid edits replaced saved settings");
            Require(File.ReadAllText(backup) == damaged, "saving removed the recovery copy");
        }
        finally
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var probe = new TcpListener(IPAddress.Loopback, 27841);
        probe.Start();
    }

    private static async Task CloseDuringStartup(string data)
    {
        var window = HiddenWindow(data);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        // MainWindow's own Loaded handler begins asynchronous initialization first.
        window.Loaded += (_, _) => { window.Close(); window.Close(); };
        window.Show();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var probe = new TcpListener(IPAddress.Loopback, 27841);
        probe.Start();
    }

    private static async Task SaveAfterStartupFailure(string data)
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 27841);
        occupied.Start();
        var window = HiddenWindow(data);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        try
        {
            await Wait(() => Text(window, "启动失败"), "startup failure was not shown");
            occupied.Stop();
            Click(window, "保存设置");
            await Wait(() => Text(window, "服务已就绪"), "save did not retry initialization");
            Require(await Healthy(data), "saved settings did not produce a ready service");
        }
        finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task<bool> Healthy(string data)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "session.json")));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("token").GetString());
        using var response = await http.GetAsync("http://127.0.0.1:27841/health");
        return response.IsSuccessStatusCode;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static bool Text(Window window, string text) => Descendants(window).OfType<TextBlock>().Any(t => t.Text.Contains(text));
    private static void Click(Window window, string label) => Descendants(window).OfType<Button>().Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Wait(Func<bool> predicate, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            if (timeout.IsCancellationRequested) throw new Exception(message);
            await Task.Delay(20);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
