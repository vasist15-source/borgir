namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// Cross-layer diagnostic join key: the GPU emulation thread increments the
    /// sequence once per draw's binding commit, and backend-side diagnostics record
    /// the current value, so records from the shared layer and the backend can be
    /// joined exactly instead of by wall clock. Diagnostic use only.
    /// </summary>
    public static class DrawDiagnostics
    {
        public static ulong BindSequence;
    }
}
