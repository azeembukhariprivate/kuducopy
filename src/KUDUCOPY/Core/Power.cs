using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;

namespace Kuducopy.Core
{
    /// <summary>"When finished": sound, sleep or shut down.</summary>
    public static class Power
    {
        public static void Sound()
        {
            try { SystemSounds.Asterisk.Play(); }
            catch { /* no sound device */ }
        }

        public static void Sleep()
        {
            try { SetSuspendState(false, false, false); }
            catch { /* not allowed */ }
        }

        /// <summary>Shuts down in 60 seconds, with Windows' own warning. CancelShutdown() stops it.</summary>
        public static void Shutdown()
        {
            Run("/s /t 60 /c \"KUDUCOPY finished copying. Windows will shut down in 60 seconds.\"");
        }

        public static void CancelShutdown() => Run("/a");

        private static void Run(string args)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); }
            catch { /* ignore */ }
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
