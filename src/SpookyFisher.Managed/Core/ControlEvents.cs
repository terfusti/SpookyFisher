using System;
using System.Threading;

namespace SpookyFisher
{
    internal sealed class ControlEvents : IDisposable
    {
        private EventWaitHandle toggle, stop, detached;

        internal ControlEvents(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("The native bridge must supply control events.", nameof(prefix));
            try
            {
                toggle = EventWaitHandle.OpenExisting(prefix + ".Toggle");
                stop = EventWaitHandle.OpenExisting(prefix + ".Stop");
                detached = EventWaitHandle.OpenExisting(prefix + ".Detached");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool StopRequested => stop.WaitOne(0);
        internal bool TakeToggle() => toggle.WaitOne(0);
        internal void AcknowledgeDetach() => detached.Set();

        public void Dispose()
        {
            toggle?.Dispose();
            stop?.Dispose();
            detached?.Dispose();
            toggle = stop = detached = null;
        }
    }
}
