namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// The buffer a binding slot found for its range the last time it looked, so that a draw binding the same range
    /// again does not search the buffer cache for it. It holds only while the cache's list of buffers is unchanged:
    /// <see cref="Generation"/> is the cache's at the time, and any buffer created, merged into a larger one or
    /// unmapped since gives the cache a new one. Only the search is skipped; syncing guest writes into the buffer and
    /// marking GPU writes still happen on every use.
    /// </summary>
    struct BufferLookup
    {
        public ulong Address;
        public ulong Size;
        public int Generation;
        public Buffer Buffer;
    }
}
