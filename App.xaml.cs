using System.Threading;
using System.Windows;

namespace HerdrBubble;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "HerdrBubble_SingleInstance_Mutex";
    internal const string WakeupEventName = "HerdrBubble_Wakeup_Event";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeupEvent;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
        _ownsMutex = isNewInstance;

        if (!isNewInstance)
        {
            TrySignalExistingInstance();
            Shutdown();
            return;
        }

        _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeupEventName);
        base.OnStartup(e);
    }

    internal EventWaitHandle? WakeupEvent => _wakeupEvent;

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var existingEvent = EventWaitHandle.OpenExisting(WakeupEventName);
            existingEvent.Set();
        }
        catch
        {
            // Existing instance may still be initializing or shutting down.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ignore if already released.
            }
        }

        _singleInstanceMutex?.Dispose();
        _wakeupEvent?.Dispose();
        base.OnExit(e);
    }
}
