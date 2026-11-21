using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using VTalk.Core;
using VTalk.Data;
using VTalk.Security;
using VTalk.Services;
using VTalk.UI;

namespace VTalk;

public partial class App : Application
{
    private Mutex? _singleInstance;
    /// <summary>İkinci örnek kilide sahip değildir; bırakmaya çalışması istisna atar.</summary>
    private bool _ownsSingleInstance;
    private EventWaitHandle? _activateSignal;
    private Identity? _identity;
    private LocalProtector? _protector;
    private Database? _db;
    private ChatService? _chat;
    private AppController? _controller;
    private UiWatchdog? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ayrı profil, AppPaths'e ilk erişimden önce seçilmelidir.
        var selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
            Environment.SetEnvironmentVariable("VTALK_PROFILE", "selftest-" + Guid.NewGuid().ToString("N")[..8]);

        ProcessHardening.Apply();

        if (Array.IndexOf(e.Args, "--apply-update") >= 0)
        {
            Shutdown(AppUpdate.ApplyPending());
            return;
        }

        // SYSTEM kurulum sonrası: PowerShell yok; CreateProcessAsUser ile kullanıcı oturumunda --show.
        if (Array.IndexOf(e.Args, "--launch-in-session") >= 0)
        {
            Shutdown(AppUpdate.LaunchInUserSession());
            return;
        }

        if (selfTest >= 0)
        {
            Shutdown(SelfTest.Run(selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : null));
            return;
        }

        var export = Array.IndexOf(e.Args, "--export-org-pub");
        if (export >= 0)
        {
            Shutdown(ExportOrgPublicKey(export + 1 < e.Args.Length ? e.Args[export + 1] : null));
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) => Log.Error("Tepsi hatası", ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("İşlenmemiş hata", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Gözlenmeyen görev hatası", ex.Exception);
            ex.SetObserved();
        };

        // Kullanıcı oturumu başına tek örnek; ikinci çalıştırma mevcut pencereyi öne getirir.
        var sid = (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) +
                  (AppPaths.Profile.Length > 0 ? "-" + AppPaths.Profile : "");
        _singleInstance = new Mutex(true, $"Local\\VTalk-Instance-{sid}", out bool isFirst);
        _ownsSingleInstance = isFirst;
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\VTalk-Activate-{sid}");
        if (!isFirst)
        {
            _activateSignal.Set();
            Shutdown();
            return;
        }

        try
        {
            AppPaths.EnsureCreated();
            var settings = Settings.Load();
            _identity = Identity.LoadOrCreate();
            _protector = LocalProtector.LoadOrCreate();
            _db = new Database(AppPaths.DatabaseFile, _protector);
            var directory = new DirectoryService();
            directory.Load();
            _chat = new ChatService(_identity, settings, _db, directory, deployment: DeploymentConfig.Load());

            _controller = new AppController(_chat);
            // --show / --after-update: kurulum sonrası veya tepsi yerine pencereyi zorla aç.
            var forceShow = Array.IndexOf(e.Args, "--show") >= 0 || Array.IndexOf(e.Args, "--after-update") >= 0;
            _controller.Start(startMinimized: !forceShow && (settings.StartMinimized || e.Args.Contains("--minimized")));
            if (forceShow) _controller.ShowMain();
            if (AppPaths.Profile.Length == 0) StartupRegistration.Apply(settings.StartWithWindows);

            var signal = _activateSignal;
            var listener = new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(() => _controller?.ShowMain());
            }) { IsBackground = true, Name = "VTalk-Activate" };
            listener.Start();

            _watchdog = new UiWatchdog(Dispatcher);
            Log.Info($"V-Talk başladı. Kimlik {_identity.PeerId[..8]}, TCP {_chat.TcpPort}");
        }
        catch (Exception ex)
        {
            Log.Error("Başlatma hatası", ex);
            MessageBox.Show("V-Talk başlatılamadı:\n\n" + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Kurulum paketi üretimi için bu bilgisayardaki admin anahtarının açık kısmını PEM olarak yazar.
    /// Çıkış kodları: 0 başarılı, 2 admin anahtarı yok, 3 geçersiz yol, 4 ağda sabitlenmiş anahtardan farklı, 1 hata.
    /// </summary>
    private static int ExportOrgPublicKey(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) return 3;
            var pub = AdminKeyStore.LocalPublicKey();
            if (pub == null) return 2;
            using (var check = CryptoUtil.ImportP256PublicKey(pub))
            {
                if (check == null) return 1;
            }
            System.IO.File.WriteAllText(System.IO.Path.GetFullPath(path), DirectoryService.ToPem(pub));

            var directory = new DirectoryService();
            directory.Load();
            return directory.PinnedOrgKey != null &&
                   !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(directory.PinnedOrgKey, pub) ? 4 : 0;
        }
        catch (Exception ex)
        {
            Log.Error("Organizasyon anahtarı dışa aktarılamadı", ex);
            return 1;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Arayüz hatası", e.Exception);
        MessageBox.Show("Beklenmeyen bir hata oluştu:\n" + e.Exception.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Dispose();
        try
        {
            _controller?.Dispose();
            _chat?.Dispose();
            _db?.Dispose();
            _protector?.Dispose();
            _identity?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Kapanış hatası", ex);
        }
        _activateSignal?.Dispose();
        if (_singleInstance != null)
        {
            if (_ownsSingleInstance) _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
