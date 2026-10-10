using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RuleForge.Masaustu
{
    /// <summary>
    /// RuleForge masaüstü kabuğu: arayüz sunucusunu (ruleforge.exe arayuz) bu bilgisayarda gizli başlatır ve
    /// WebView2 penceresinde gösterir. Pencere kapanınca sunucu da kapanır. Uzak sunucu gerekmez.
    /// </summary>
    internal static class Program
    {
        private const string WebView2Indir = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Aynı anda tek pencere.
            using (var mutex = new Mutex(true, "RuleForge.Masaustu.Tek", out var yeni))
            {
                if (!yeni)
                {
                    MessageBox.Show("RuleForge zaten açık.", "RuleForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                var sunucuYolu = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ruleforge.exe");
                if (!File.Exists(sunucuYolu))
                {
                    Hata("ruleforge.exe bulunamadı. Programı yeniden derleyin (dotnet build -c Release).");
                    return 1;
                }

                Process? sunucu = null;
                try
                {
                    int port = BosPort();
                    sunucu = SunucuyuBaslat(sunucuYolu, port, args);
                    var url = $"http://127.0.0.1:{port}/";
                    if (!Hazir(url, sunucu, TimeSpan.FromSeconds(40)))
                    {
                        Hata("Arayüz sunucusu başlamadı." + (sunucu.HasExited ? $" (çıkış kodu {sunucu.ExitCode})" : string.Empty));
                        return 1;
                    }
                    Application.Run(new Pencere(url));
                    return 0;
                }
                finally
                {
                    SunucuyuKapat(sunucu);
                }
            }
        }

        private static Process SunucuyuBaslat(string yol, int port, string[] args)
        {
            var arg = $"arayuz --no-browser --port {port}";
            // İsteğe bağlı: RuleForgeApp.exe --projeler "D:\Projeler"
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--projeler") arg += $" --projeler \"{args[i + 1]}\"";
            return Process.Start(new ProcessStartInfo(yol, arg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            })!;
        }

        private static void SunucuyuKapat(Process? sunucu)
        {
            try
            {
                if (sunucu != null && !sunucu.HasExited)
                {
                    sunucu.Kill();
                    sunucu.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // zaten kapanmış olabilir
            }
        }

        private static int BosPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static bool Hazir(string url, Process sunucu, TimeSpan sure)
        {
            var bitis = DateTime.UtcNow + sure;
            while (DateTime.UtcNow < bitis && !sunucu.HasExited)
            {
                try
                {
                    var istek = (HttpWebRequest)WebRequest.Create(url);
                    istek.Timeout = 1500;
                    using (istek.GetResponse()) return true;
                }
                catch (WebException ex) when (ex.Response != null)
                {
                    return true; // sunucu cevap veriyor (hata kodu olsa bile)
                }
                catch (Exception)
                {
                    Thread.Sleep(250);
                }
            }
            return false;
        }

        internal static void Hata(string mesaj) =>
            MessageBox.Show(mesaj, "RuleForge", MessageBoxButtons.OK, MessageBoxIcon.Error);

        private sealed class Pencere : Form
        {
            private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
            private readonly string _url;

            public Pencere(string url)
            {
                _url = url;
                Text = "RuleForge";
                Width = 1400;
                Height = 900;
                StartPosition = FormStartPosition.CenterScreen;
                Controls.Add(_web);
                Load += async (s, e) => await Baslat();
            }

            private async System.Threading.Tasks.Task Baslat()
            {
                try
                {
                    var veri = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuleForge", "WebView2");
                    var ortam = await CoreWebView2Environment.CreateAsync(null, veri);
                    await _web.EnsureCoreWebView2Async(ortam);
                }
                catch (Exception ex) when (ex is WebView2RuntimeNotFoundException || ex is System.Runtime.InteropServices.COMException || ex is InvalidOperationException)
                {
                    var cevap = MessageBox.Show(
                        "RuleForge'un pencereyi göstermesi için Microsoft WebView2 bileşeni gerekiyor ve bu bilgisayarda bulunamadı.\n\n" +
                        "Ücretsiz yükleyiciyi şimdi açmak ister misiniz? Kurulumdan sonra RuleForge'u yeniden açın.",
                        "RuleForge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (cevap == DialogResult.Yes) Process.Start(new ProcessStartInfo(WebView2Indir) { UseShellExecute = true });
                    Close();
                    return;
                }

                var ayar = _web.CoreWebView2.Settings;
                ayar.AreDefaultContextMenusEnabled = true;
                ayar.IsStatusBarEnabled = false;
                // Dış bağlantılar ve yeni pencereler varsayılan tarayıcıda açılır; RuleForge penceresi sadece arayüzü gösterir.
                _web.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                    catch (Exception) { /* açılamadıysa yoksay */ }
                };
                _web.CoreWebView2.DocumentTitleChanged += (s, e) =>
                    Text = string.IsNullOrWhiteSpace(_web.CoreWebView2.DocumentTitle) ? "RuleForge" : _web.CoreWebView2.DocumentTitle;
                _web.CoreWebView2.Navigate(_url);
            }
        }
    }
}
