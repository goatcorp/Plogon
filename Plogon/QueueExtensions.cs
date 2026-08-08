using System.Collections.Generic;

namespace Plogon
{
    /// <summary><see cref="Queue{T}"/> extensions.</summary>
    public static class QueueExtensions
    {
        /// <summary>Enqueue a range of items into a <paramref name="queue"/>.</summary>
        /// <typeparam name="T">Item type.</typeparam>
        /// <param name="queue"><see cref="Queue{T}"/> to enqueue into.</param>
        /// <param name="items">Items to enqueue.</param>
        public static void EnqueueRange<T>(this Queue<T> queue, IEnumerable<T> items)
        {
            foreach (var item in items) queue.Enqueue(item);
        }
    }
}
