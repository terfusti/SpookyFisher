using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SpookyFisher
{
    internal static class Log
    {
        private static readonly object gate = new object();
        private static Action<string> sink;
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(typeof(Entry).Assembly.Location), "SpookyFisher.managed.log");

        internal static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            lock (gate)
            {
                try { File.AppendAllText(FilePath, line); } catch { }
                try { sink?.Invoke(message); } catch { }
                try { OutputDebugStringW(line); } catch { }
            }
        }

        // Taking the same lock waits for in-flight native log calls before detach can unload it.
        internal static void SetSink(Action<string> value)
        {
            lock (gate) sink = value;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern void OutputDebugStringW(string message);
    }
}
