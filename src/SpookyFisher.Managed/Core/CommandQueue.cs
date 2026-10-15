using System;
using System.Collections.Generic;

namespace SpookyFisher
{
    // Producers may be on any thread. Only the game thread drains or closes the queue.
    internal sealed class CommandQueue<T>
    {
        private readonly object gate = new object();
        private readonly Queue<Action<T>> pending = new Queue<Action<T>>();
        private readonly int capacity;
        private bool closed;

        internal CommandQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        internal bool TryEnqueue(Action<T> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (closed || pending.Count >= capacity) return false;
                pending.Enqueue(command);
                return true;
            }
        }

        internal void Drain(T context, int budget, Action<Exception> onError)
        {
            // Bound this batch so a command that posts another cannot monopolize a frame.
            int count;
            lock (gate) count = Math.Min(pending.Count, budget);
            for (int i = 0; i < count; i++)
            {
                Action<T> command;
                lock (gate)
                {
                    if (pending.Count == 0) break;
                    command = pending.Dequeue();
                }
                try { command(context); }
                catch (Exception ex) { onError(ex); }
            }
        }

        internal void Clear()
        {
            lock (gate) pending.Clear();
        }

        internal void Close()
        {
            lock (gate)
            {
                closed = true;
                pending.Clear();
            }
        }
    }
}
