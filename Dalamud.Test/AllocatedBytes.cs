using System;

namespace Dalamud.Test
{
    // A background GC voids every thread's allocation context without taking the unused rest off the thread's count,
    // so GC.GetAllocatedBytesForCurrentThread can rise by up to one allocation quantum (8 KB) on a thread that
    // allocated nothing. Any blocking GC empties the context properly, leaving nothing for a later void to count.
    internal static class AllocatedBytes
    {
        /// <summary>Empties this thread's allocation context, then reads its allocated-bytes count.</summary>
        /// <returns>The count to pass to <see cref="Since"/>.</returns>
        public static long Start()
        {
            GC.Collect(0);
            return GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>Bytes this thread allocated since <paramref name="start"/>.</summary>
        /// <param name="start">What <see cref="Start"/> returned.</param>
        /// <returns>The bytes allocated.</returns>
        public static long Since(long start) => GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
