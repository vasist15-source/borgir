using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Path = System.IO.Path;
using UserId = Ryujinx.HLE.HOS.Services.Account.Acc.UserId;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// User profiles for Settings › Profiles: adding, renaming, pictures and deleting, plus the Switch's own avatars
    /// from the installed firmware. Profiles live where upstream Ryujinx keeps them (system/Profiles.json in the data
    /// folder), so other Ryujinx builds see the same ones.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class NativeProfiles
    {
        // A profile's nickname is 32 bytes of UTF-8 on the Switch.
        private const int MaxNameBytes = 32;

        // Games read the picture into a buffer of 128 KiB.
        private const int MaxImageBytes = 0x20000;

        // The system archive with the Switch's avatar pictures.
        private const ulong AvatarArchiveId = 0x010000000000080A;

        private static List<string> _avatars;

        private static AccountManager Accounts => NativeApp.AccountManager;

        public static JsonObject Add(string name)
        {
            if (Refuse(out JsonObject error) || !TryCheckName(name, out name, out error))
            {
                return error;
            }

            UserId id = new(Guid.NewGuid().ToString("N"));
            byte[] image = EmbeddedResources.Read("Ryujinx.HLE/HOS/Services/Account/Acc/DefaultUserImage.jpg");

            Accounts.AddUser(name, image, id);

            return new JsonObject { ["ok"] = true, ["id"] = id.ToString() };
        }

        public static JsonObject Rename(string id, string name)
        {
            if (Refuse(out JsonObject error) || !TryFind(id, out UserProfile profile, out error) || !TryCheckName(name, out name, out error))
            {
                return error;
            }

            Accounts.SetUserName(profile.UserId, name);

            return NativeBridge.Ok();
        }

        /// <summary>Sets a profile's picture from a base64 JPEG, which the interface crops and scales to 256 pixels.</summary>
        public static JsonObject SetImage(string id, string base64)
        {
            if (Refuse(out JsonObject error) || !TryFind(id, out UserProfile profile, out error))
            {
                return error;
            }

            byte[] image;

            try
            {
                image = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return NativeBridge.Error("The picture could not be read.");
            }

            if (image is not [0xFF, 0xD8, ..])
            {
                return NativeBridge.Error("The picture must be a JPEG.");
            }

            if (image.Length > MaxImageBytes)
            {
                return NativeBridge.Error("The picture is too large for games to load.");
            }

            Accounts.SetUserImage(profile.UserId, image);

            return NativeBridge.Ok();
        }

        /// <summary>Deletes a profile and, with it, all of that profile's save data.</summary>
        public static JsonObject Delete(string id)
        {
            if (Refuse(out JsonObject error) || !TryFind(id, out UserProfile profile, out error))
            {
                return error;
            }

            // The account manager recreates the default profile on every start and falls back to it after a delete.
            if (profile.UserId == AccountManager.DefaultUserId)
            {
                return NativeBridge.Error("The default profile can't be deleted.");
            }

            try
            {
                Accounts.DeleteUser(profile.UserId);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Deleting profile {profile.Name} failed: {ex}");

                return NativeBridge.Error($"The profile could not be deleted: {ex.Message}");
            }

            Logger.Notice.Print(LogClass.Application, $"Deleted profile {profile.Name} ({profile.UserId}) and its save data.");

            return NativeBridge.Ok();
        }

        /// <summary>The Switch's avatar pictures from the installed firmware, as transparent 256-pixel PNGs.</summary>
        public static JsonObject Avatars()
        {
            try
            {
                _avatars ??= ExtractAvatars();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Reading the firmware's avatars failed: {ex}");

                return NativeBridge.Error($"The firmware's avatars could not be read: {ex.Message}");
            }

            if (_avatars.Count == 0)
            {
                return NativeBridge.Error("Install the Switch firmware (Settings › General) to choose one of its avatars.");
            }

            return new JsonObject { ["ok"] = true, ["avatars"] = new JsonArray(_avatars.Select(path => (JsonNode)path).ToArray()) };
        }

        private static List<string> ExtractAvatars()
        {
            List<string> avatars = [];

            string contentPath = NativeApp.ContentManager.GetInstalledContentPath(AvatarArchiveId, StorageId.BuiltInSystem, NcaContentType.Data);
            string archivePath = VirtualFileSystem.SwitchPathToSystemPath(contentPath);

            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return avatars;
            }

            string directory = Path.Combine(NativeApp.CacheDirectory, "avatars");
            Directory.CreateDirectory(directory);

            using IStorage storage = new LocalStorage(archivePath, FileAccess.Read, FileMode.Open);
            Nca nca = new(NativeApp.VirtualFileSystem.KeySet, storage);
            IFileSystem romfs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);

            foreach (DirectoryEntryEx entry in romfs.EnumerateEntries().OrderBy(entry => entry.FullPath, StringComparer.Ordinal))
            {
                // Each chara/*.szs is a Yaz0-compressed 256×256 RGBA picture.
                if (entry.Type != DirectoryEntryType.File || !entry.FullPath.Contains("chara") || !entry.FullPath.EndsWith(".szs"))
                {
                    continue;
                }

                string path = Path.Combine(directory, Path.GetFileNameWithoutExtension(entry.FullPath) + ".png");

                if (!File.Exists(path))
                {
                    using UniqueRef<IFile> file = new();
                    romfs.OpenFile(ref file.Ref, ("/" + entry.FullPath.TrimStart('/')).ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    using MemoryStream compressed = new();
                    file.Get.AsStream().CopyTo(compressed);
                    compressed.Position = 0;

                    using SKImage image = SKImage.FromPixelCopy(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul), DecompressYaz0(compressed));
                    using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(path, png.ToArray());
                }

                avatars.Add(path);
            }

            return avatars;
        }

        // From the Avalonia interface's firmware avatar picker.
        private static byte[] DecompressYaz0(MemoryStream stream)
        {
            using BinaryReader reader = new(stream);

            reader.ReadInt32(); // Magic

            uint decodedLength = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());

            reader.ReadInt64(); // Padding

            byte[] input = new byte[stream.Length - stream.Position];
            stream.ReadExactly(input, 0, input.Length);

            uint inputOffset = 0;

            byte[] output = new byte[decodedLength];
            uint outputOffset = 0;

            ushort mask = 0;
            byte header = 0;

            while (outputOffset < decodedLength)
            {
                if ((mask >>= 1) == 0)
                {
                    header = input[inputOffset++];
                    mask = 0x80;
                }

                if ((header & mask) != 0)
                {
                    if (outputOffset == output.Length)
                    {
                        break;
                    }

                    output[outputOffset++] = input[inputOffset++];
                }
                else
                {
                    byte byte1 = input[inputOffset++];
                    byte byte2 = input[inputOffset++];

                    uint dist = (uint)((byte1 & 0xF) << 8) | byte2;
                    uint position = outputOffset - (dist + 1);

                    uint length = (uint)byte1 >> 4;
                    if (length == 0)
                    {
                        length = (uint)input[inputOffset++] + 0x12;
                    }
                    else
                    {
                        length += 2;
                    }

                    uint gap = outputOffset - position;
                    uint nonOverlappingLength = length;

                    if (nonOverlappingLength > gap)
                    {
                        nonOverlappingLength = gap;
                    }

                    Buffer.BlockCopy(output, (int)position, output, (int)outputOffset, (int)nonOverlappingLength);
                    outputOffset += nonOverlappingLength;
                    position += nonOverlappingLength;
                    length -= nonOverlappingLength;

                    while (length-- > 0)
                    {
                        output[outputOffset++] = output[position++];
                    }
                }
            }

            return output;
        }

        /// <summary>Profiles are read while a game runs, so they only change while none does.</summary>
        private static bool Refuse(out JsonObject error)
        {
            error = NativeApp.IsGameRunning ? NativeBridge.Error("Stop the game before changing profiles.") : null;

            return error != null;
        }

        private static bool TryFind(string id, out UserProfile profile, out JsonObject error)
        {
            profile = Accounts.GetAllUsers().FirstOrDefault(user => user.UserId.ToString() == id);
            error = profile == null ? NativeBridge.Error("That profile no longer exists.") : null;

            return profile != null;
        }

        private static bool TryCheckName(string name, out string trimmed, out JsonObject error)
        {
            trimmed = name?.Trim() ?? string.Empty;
            error = null;

            if (trimmed.Length == 0)
            {
                error = NativeBridge.Error("A profile needs a name.");
            }
            else if (Encoding.UTF8.GetByteCount(trimmed) > MaxNameBytes)
            {
                error = NativeBridge.Error($"Profile names can be at most {MaxNameBytes} bytes long; that is 32 letters, or fewer with accents or emoji.");
            }

            return error == null;
        }
    }
}
