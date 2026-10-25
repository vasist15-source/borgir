namespace Ryujinx.Graphics.GAL
{
    public enum HostSyncCreateSource
    {
        Unknown,
        WaitForIdle,
        Syncpoint,
        SetReference,
        TextureUnbindForce,
    }

    public enum HostSyncWaitSource
    {
        Unknown,
        BufferModifiedRange,
        TextureGroup,
        TextureGroupInBuffer,
    }
}
