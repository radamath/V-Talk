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
    private EventWaitHandle? _activateSignal;
    private Identity? _identity;
    private LocalProtector? _protector;
    private Database? _db;
    private ChatService? _chat;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ayrı profil, AppPaths'e ilk erişimden önce seçilmelidir.
        var selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
            Environment.SetEnvironmentVariable("VTALK_PROFILE", "selftest-" + Guid.NewGuid().ToString("N")[..8]);

        ProcessHardening.Apply();

        if (selfTest >= 0)
        {
            Shutdown(SelfTest.Run(selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : null));
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
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
            _chat = new ChatService(_identity, settings, _db, directory);

            _controller = new AppController(_chat);
            _controller.Start(startMinimized: settings.StartMinimized || e.Args.Contains("--minimized"));
            if (AppPaths.Profile.Length == 0) StartupRegistration.Apply(settings.StartWithWindows);

            var signal = _activateSignal;
            var listener = new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(() => _controller?.ShowMain());
            }) { IsBackground = true, Name = "VTalk-Activate" };
            listener.Start();

            Log.Info($"V-Talk başladı. Kimlik {_identity.PeerId[..8]}, TCP {_chat.TcpPort}");
        }
        catch (Exception ex)
        {
            Log.Error("Başlatma hatası", ex);
            MessageBox.Show("V-Talk başlatılamadı:\n\n" + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
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
            try { _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
