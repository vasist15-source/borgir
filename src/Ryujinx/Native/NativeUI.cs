using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// Mirrors PXCoreCallbacks in macos/ProjectXUI/Sources/PXBridge/include/PXBridge.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct CoreCallbacks
    {
        public const uint CurrentVersion = 1;

        public uint Version;
        public delegate* unmanaged<byte*, byte*> Command;
        public delegate* unmanaged<byte*, void> FreeString;
        public delegate* unmanaged<ushort, byte, void> KeyEvent;
        public delegate* unmanaged<int, double, double, int, void> MouseEvent;
        public delegate* unmanaged<double, double, double, void> ViewResized;
        public delegate* unmanaged<byte, void> FocusChanged;
    }

    /// <summary>
    /// The Swift UI library (macos/ProjectXUI). The app bundle keeps it in Contents/Frameworks; a loose
    /// development build keeps it next to the executable.
    /// </summary>
    internal static unsafe class NativeUI
    {
        public const string LibraryFileName = "libProjectXUI.dylib";

        private static delegate* unmanaged<CoreCallbacks*, int> _run;
        private static delegate* unmanaged<byte*, void> _postEvent;
        private static delegate* unmanaged<byte*, byte*> _request;
        private static delegate* unmanaged<byte*, void> _free;

        public static bool IsLoaded => _run != null;

        public static string FindLibrary()
        {
            string baseDirectory = AppContext.BaseDirectory;

            string[] candidates =
            [
                Path.Combine(baseDirectory, "..", "Frameworks", LibraryFileName),
                Path.Combine(baseDirectory, LibraryFileName),
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        public static bool TryLoad(out string error)
        {
            string path = FindLibrary();

            if (path == null)
            {
                error = $"{LibraryFileName} was not found next to the app.";
                return false;
            }

            try
            {
                nint handle = NativeLibrary.Load(path);

                _run = (delegate* unmanaged<CoreCallbacks*, int>)NativeLibrary.GetExport(handle, "px_ui_run");
                _postEvent = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(handle, "px_ui_post_event");
                _request = (delegate* unmanaged<byte*, byte*>)NativeLibrary.GetExport(handle, "px_ui_request");
                _free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(handle, "px_ui_free");
            }
            catch (Exception ex)
            {
                _run = null;
                error = $"Could not load {path}: {ex.Message}";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Hands the main thread to the UI. Returns when the UI quits (AppKit usually exits the process first).
        /// </summary>
        public static int Run(CoreCallbacks* callbacks) => _run(callbacks);

        public static void PostEvent(string json)
        {
            if (_postEvent == null)
            {
                return;
            }

            byte* utf8 = AllocUtf8(json);

            try
            {
                _postEvent(utf8);
            }
            finally
            {
                NativeMemory.Free(utf8);
            }
        }

        /// <summary>
        /// Asks the player something and waits for the answer. Must not be called on the main thread's run loop
        /// while it waits for this thread.
        /// </summary>
        public static string Request(string json)
        {
            if (_request == null)
            {
                return null;
            }

            byte* utf8 = AllocUtf8(json);

            try
            {
                byte* reply = _request(utf8);

                if (reply == null)
                {
                    return null;
                }

                string text = Marshal.PtrToStringUTF8((nint)reply);
                _free(reply);

                return text;
            }
            finally
            {
                NativeMemory.Free(utf8);
            }
        }

        public static byte* AllocUtf8(string text)
        {
            int length = Encoding.UTF8.GetByteCount(text);
            byte* buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);

            Encoding.UTF8.GetBytes(text, new Span<byte>(buffer, length));
            buffer[length] = 0;

            return buffer;
        }
    }
}
