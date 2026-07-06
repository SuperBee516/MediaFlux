using System.Runtime.InteropServices;

namespace Encode.Services
{
    public sealed class SleepPreventionService : IDisposable
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        private bool _active;

        private SleepPreventionService()
        {
            _active = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) != 0;
        }

        public static SleepPreventionService? Acquire(bool enabled)
        {
            return enabled ? new SleepPreventionService() : null;
        }

        public void Dispose()
        {
            if (!_active)
                return;

            SetThreadExecutionState(ES_CONTINUOUS);
            _active = false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
