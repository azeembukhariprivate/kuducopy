using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kuducopy.Core
{
    /// <summary>
    /// Keeps one KUDUCOPY running per Windows user. Later starts (e.g. one per file from a right-click menu)
    /// hand their command to the running copy and exit, so all jobs share one queue.
    /// </summary>
    public sealed class Hub : IDisposable
    {
        private static string UserKey => Regex.Replace(Environment.UserName ?? "user", "[^A-Za-z0-9]", "_");
        private static string PipeName => "KUDUCOPY.Hub." + UserKey;
        private static string MutexName => @"Local\KUDUCOPY.Hub." + UserKey;

        private readonly Mutex _mutex;
        private readonly Thread _thread;
        private volatile bool _stopping;

        private Hub(Mutex mutex, Action<string[]> received)
        {
            _mutex = mutex;
            _thread = new Thread(() => Listen(received)) { IsBackground = true, Name = "KUDUCOPY hub" };
            _thread.Start();
        }

        /// <summary>Returns a Hub when this is the first copy; null when the command was handed to the running copy.</summary>
        public static Hub Start(string[] args, Action<string[]> received)
        {
            Mutex mutex;
            bool first;
            try
            {
                mutex = new Mutex(true, MutexName, out first);
            }
            catch
            {
                return new Hub(null, received); // can't coordinate: just run on our own
            }

            if (first) return new Hub(mutex, received);

            mutex.Dispose();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        client.Connect(3000);
                        var data = Encoding.UTF8.GetBytes(string.Join("\n", args) + "\n\0");
                        client.Write(data, 0, data.Length);
                        client.Flush();
                    }
                    return null;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
            return new Hub(null, received); // the running copy didn't answer: open our own window
        }

        private void Listen(Action<string[]> received)
        {
            while (!_stopping)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        if (_stopping) return;
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            var text = reader.ReadToEnd().TrimEnd('\0', '\n');
                            received(text.Length == 0 ? new string[0] : text.Split('\n'));
                        }
                    }
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                // Wake the listener so the thread can end.
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out)) client.Connect(200);
            }
            catch { /* already gone */ }
            try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
            _mutex?.Dispose();
        }
    }
}
