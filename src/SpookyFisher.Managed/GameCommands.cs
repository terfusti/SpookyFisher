using System;
using Terraria;

namespace SpookyFisher
{
    public static class GameCommands
    {
        // Acceptance is not completion: paused/world-exit/shutdown frames discard pending work.
        // Capture values, not Terraria objects. Keep actions short and validate on execution.
        public static bool TryEnqueue(Action<Player> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var runtime = Runtime.Current;
            return runtime != null && runtime.TryEnqueue(command);
        }
    }
}
