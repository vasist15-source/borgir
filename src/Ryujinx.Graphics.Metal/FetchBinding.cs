namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// One same-pixel self-read to serve from tile memory: the fragment texture binding
    /// that samples a current colour attachment, and the attachment slot it aliases.
    /// </summary>
    readonly record struct FetchBinding(int Binding, int Slot);
}
