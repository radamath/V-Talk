using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using VTalk.Core;

namespace VTalk.Services;

/// <summary>
/// Arayüz iş parçacığının yanıt verip vermediğini arka planda ölçer ve uzun donmaları günlüğe yazar. Süreç içinden başka bir
/// iş parçacığının yığını okunamadığı için dispatcher kancalarıyla o an çalışan işlem (tür.yöntem, öncelik) izlenir ve
/// donma raporuna eklenir; bellek ve çöp toplama sayıları da yazılır. Karartılmış adlar sürümle saklanan eşleme
/// dosyasıyla (installer\out\obfuscation-map-SÜRÜM.txt) çözülür.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StillBlocked = TimeSpan.FromSeconds(10);
    // DispatcherOperation'ın çağıracağı temsilci herkese açık değildir; yalnızca donma raporu için okunur.
    private static readonly FieldInfo? OperationMethod =
        typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private volatile DispatcherOperation? _current;
    private long _currentStartedTicks;

    public UiWatchdog(Dispatcher ui)
    {
        _ui = ui;
        _ui.Hooks.OperationStarted += OnOperationStarted;
        _ui.Hooks.OperationCompleted += OnOperationFinished;
        _ui.Hooks.OperationAborted += OnOperationFinished;
        _thread = new Thread(Run) { IsBackground = true, Name = "VTalk.UiWatchdog", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    // Her işlemde yalnızca başvuru ve zaman yazılır (biçimlendirme yalnızca donma anında): ek yük ihmal edilebilir.
    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        Interlocked.Exchange(ref _currentStartedTicks, Stopwatch.GetTimestamp());
        _current = e.Operation;
    }

    private void OnOperationFinished(object? sender, DispatcherHookEventArgs e)
    {
        if (ReferenceEquals(_current, e.Operation)) _current = null;
    }

    private void Run()
    {
        while (!_stopped)
        {
            Thread.Sleep(Interval);
            if (_stopped || _ui.HasShutdownStarted) return;
            var sw = Stopwatch.StartNew();
            var gcBefore = GcCounts();
            var op = _ui.BeginInvoke(DispatcherPriority.Normal, () => { });
            if (op.Wait(StillBlocked) != DispatcherOperationStatus.Completed)
            {
                if (_stopped || _ui.HasShutdownStarted) return;
                Log.Warn($"Arayüz {StillBlocked.TotalSeconds:F0} sn'dir yanıt vermiyor. {Describe(gcBefore)}");
                op.Wait();
            }
            if (sw.Elapsed > ReportAfter && !_stopped)
                Log.Warn($"Arayüz iş parçacığı {sw.Elapsed.TotalSeconds:F1} sn yanıt vermedi. {Describe(gcBefore)}");
        }
    }

    /// <summary>Donma anında çalışan işlem ve süreç durumu (tanı için).</summary>
    private string Describe((int Gen0, int Gen1, int Gen2) gcBefore)
    {
        var current = _current;
        var operation = "bilinmiyor (girdi/düzen işlemi olabilir)";
        if (current != null)
        {
            var age = Stopwatch.GetElapsedTime(Interlocked.Read(ref _currentStartedTicks));
            var method = OperationMethod?.GetValue(current) as Delegate;
            var name = method == null ? "?" : $"{method.Method.DeclaringType?.FullName}.{method.Method.Name}";
            operation = $"{name} [öncelik {current.Priority}, {age.TotalSeconds:F1} sn'dir çalışıyor]";
        }
        var gc = GcCounts();
        using var process = Process.GetCurrentProcess();
        return $"Çalışan işlem: {operation}; GC +{gc.Gen0 - gcBefore.Gen0}/{gc.Gen1 - gcBefore.Gen1}/{gc.Gen2 - gcBefore.Gen2}; " +
               $"bellek {process.WorkingSet64 / (1024 * 1024)} MB (yönetilen {GC.GetTotalMemory(false) / (1024 * 1024)} MB); " +
               $"sürüm {AppUpdate.CurrentText}";
    }

    private static (int Gen0, int Gen1, int Gen2) GcCounts() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public void Dispose()
    {
        _stopped = true;
        _ui.Hooks.OperationStarted -= OnOperationStarted;
        _ui.Hooks.OperationCompleted -= OnOperationFinished;
        _ui.Hooks.OperationAborted -= OnOperationFinished;
    }
}
