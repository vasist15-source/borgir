using Ryujinx.Ava.Common.Models;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Path = System.IO.Path;

namespace Ryujinx.Ava.Native
{
    /// <summary>
    /// A game's updates, DLC and mods: what the library's detail pane lists and toggles. Updates and DLC are stored
    /// the way upstream Ryujinx stores them (games/&lt;id&gt;/updates.json and dlc.json), mods in games/&lt;id&gt;/mods.json.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class NativeContent
    {
        private static readonly ModMetadataJsonSerializerContext _modSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());

        private static ApplicationLibrary Library => NativeApp.ApplicationLibrary;

        public static JsonObject Get(string id)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            JsonArray updates = [];

            // Configs written elsewhere can list one file twice under paths that differ only in case ("Switch Rom" and
            // "Switch ROM"), which is the same file on macOS. Show it once, keeping the selected entry.
            IEnumerable<(TitleUpdateModel TitleUpdate, bool IsSelected)> uniqueUpdates = Library.FindUpdateConfigurationFor(game.Id)
                .GroupBy(update => update.TitleUpdate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(update => update.IsSelected).First());

            foreach ((TitleUpdateModel update, bool selected) in uniqueUpdates.OrderByDescending(update => update.TitleUpdate.Version))
            {
                updates.Add((JsonNode)new JsonObject
                {
                    ["path"] = update.Path,
                    ["version"] = update.DisplayVersion,
                    ["selected"] = selected,
                    ["bundled"] = update.IsBundled,
                });
            }

            JsonArray dlc = [];

            IEnumerable<(DownloadableContentModel Dlc, bool IsEnabled)> uniqueDlc = Library.FindDlcConfigurationFor(game.Id)
                .GroupBy(content => (content.Dlc.ContainerPath.ToUpperInvariant(), content.Dlc.FullPath.ToUpperInvariant()))
                .Select(group => group.OrderByDescending(content => content.IsEnabled).First());

            foreach ((DownloadableContentModel content, bool enabled) in uniqueDlc.OrderBy(content => content.Dlc.TitleId))
            {
                dlc.Add((JsonNode)new JsonObject
                {
                    ["titleId"] = content.TitleIdStr,
                    ["name"] = DlcName(content),
                    ["path"] = content.ContainerPath,
                    ["contentPath"] = content.FullPath,
                    ["enabled"] = enabled,
                    ["bundled"] = content.IsBundled,
                });
            }

            JsonArray mods = [];
            List<Mod> foundMods = FindMods(game, out List<ModLoader.Cheat> foundCheats);

            foreach (Mod mod in foundMods)
            {
                mods.Add((JsonNode)new JsonObject
                {
                    ["name"] = mod.Name,
                    ["path"] = mod.Path,
                    ["enabled"] = mod.Enabled,
                });
            }

            JsonArray cheats = [];
            HashSet<string> enabledCheats = ReadEnabledCheats(game);

            foreach (ModLoader.Cheat cheat in foundCheats)
            {
                string key = CheatKey(cheat);

                cheats.Add((JsonNode)new JsonObject
                {
                    ["key"] = key,
                    // ModLoader names a cheat "<name Cheat>".
                    ["name"] = cheat.Name.TrimStart('<').TrimEnd('>').Replace(" Cheat", string.Empty, StringComparison.Ordinal),
                    ["buildId"] = Path.GetFileNameWithoutExtension(cheat.Path.Name).ToUpperInvariant(),
                    ["source"] = CheatSource(cheat),
                    ["path"] = cheat.Path.FullName,
                    ["enabled"] = enabledCheats.Contains(key),
                });
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["updates"] = updates,
                ["dlc"] = dlc,
                ["mods"] = mods,
                ["cheats"] = cheats,
                ["ownSettings"] = NativeGameSettings.Keys(game.Id),
                ["modsDir"] = ModsDirectory(game.IdString),
            };
        }

        /// <summary>
        /// Plays the game with the update at <paramref name="path"/>, or the base version when it is empty.
        /// </summary>
        public static JsonObject SelectUpdate(string id, string path)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            List<(TitleUpdateModel, bool)> updates = Library.FindUpdateConfigurationFor(game.Id)
                .Select(update => (update.TitleUpdate, update.TitleUpdate.Path == path))
                .ToList();

            Library.SaveTitleUpdatesForGame(game, updates);
            NativeApp.PostLibrary();

            return NativeBridge.Ok();
        }

        public static JsonObject SetDlc(string id, string path, string contentPath, bool enabled)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            List<(DownloadableContentModel, bool)> dlcs = Library.FindDlcConfigurationFor(game.Id)
                .Select(content => (content.Dlc, SamePath(content.Dlc.ContainerPath, path) && SamePath(content.Dlc.FullPath, contentPath) ? enabled : content.IsEnabled))
                .ToList();

            Library.SaveDownloadableContentsForGame(game, dlcs);

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Adds the updates and DLC for this game found in an NSP or XCI file.
        /// </summary>
        public static JsonObject Add(string id, string path)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            if (!File.Exists(path))
            {
                return NativeBridge.Error($"{path} does not exist.");
            }

            int updatesAdded = 0;
            int dlcAdded = 0;

            if (Library.TryGetTitleUpdatesFromFile(path, out List<TitleUpdateModel> foundUpdates))
            {
                List<(TitleUpdateModel Update, bool Selected)> updates = [.. Library.FindUpdateConfigurationFor(game.Id)];
                List<TitleUpdateModel> newUpdates = foundUpdates
                    .Where(update => update.TitleIdBase == game.IdBase && updates.All(existing => existing.Update != update))
                    .ToList();

                if (newUpdates.Count > 0)
                {
                    // Play the newest update added, as upstream does.
                    TitleUpdateModel newest = newUpdates.MaxBy(update => update.Version);

                    updates = updates.Select(update => (update.Update, false)).ToList();
                    updates.AddRange(newUpdates.Select(update => (update, update == newest)));

                    Library.SaveTitleUpdatesForGame(game, updates);
                    updatesAdded = newUpdates.Count;
                }
            }

            if (Library.TryGetDownloadableContentFromFile(path, out List<DownloadableContentModel> foundDlc))
            {
                List<(DownloadableContentModel Dlc, bool Enabled)> dlcs = [.. Library.FindDlcConfigurationFor(game.Id)];
                List<DownloadableContentModel> newDlc = foundDlc
                    .Where(content => content.TitleIdBase == game.IdBase && dlcs.All(existing => existing.Dlc != content))
                    .ToList();

                if (newDlc.Count > 0)
                {
                    dlcs.AddRange(newDlc.Select(content => (content, true)));

                    Library.SaveDownloadableContentsForGame(game, dlcs);
                    dlcAdded = newDlc.Count;
                }
            }

            if (updatesAdded == 0 && dlcAdded == 0)
            {
                return NativeBridge.Error($"{Path.GetFileName(path)} has no new update or DLC for {game.Name}.");
            }

            Logger.Info?.Print(LogClass.Application, $"Added {updatesAdded} update(s) and {dlcAdded} DLC for {game.Name} from {path}");
            NativeApp.PostLibrary();

            return new JsonObject { ["ok"] = true, ["updates"] = updatesAdded, ["dlc"] = dlcAdded };
        }

        /// <summary>
        /// Forgets an update or DLC file. The file itself stays where it is.
        /// </summary>
        public static JsonObject Remove(string id, string kind, string path)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            if (kind == "update")
            {
                List<(TitleUpdateModel, bool)> updates = Library.FindUpdateConfigurationFor(game.Id)
                    .Where(update => !SamePath(update.TitleUpdate.Path, path))
                    .Select(update => (update.TitleUpdate, update.IsSelected))
                    .ToList();

                Library.SaveTitleUpdatesForGame(game, updates);
                NativeApp.PostLibrary();
            }
            else
            {
                List<(DownloadableContentModel, bool)> dlcs = Library.FindDlcConfigurationFor(game.Id)
                    .Where(content => !SamePath(content.Dlc.ContainerPath, path))
                    .Select(content => (content.Dlc, content.IsEnabled))
                    .ToList();

                Library.SaveDownloadableContentsForGame(game, dlcs);
            }

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Where imported mods go: the emulated SD card's atmosphere/contents/&lt;id&gt;, where Ryujinx's own mod
        /// manager puts them.
        /// </summary>
        public static string ModsDirectory(string titleId) => ModLoader.GetApplicationDir(ModLoader.GetSdModsBasePath(), titleId);

        private static readonly string[] _modParts = ["romfs", "exefs", "romfs.bin", "exefs.nsp", "cheats"];

        private sealed record FoundMod(string Name, DirectoryInfo Root, ulong? TitleId);

        /// <summary>
        /// Installs the mods in a folder or a downloaded archive (anything bsdtar reads: ZIP, 7z, RAR) for a game. Each
        /// folder with a romfs, exefs or cheats folder in it is one mod, however deep it sits; an Atmosphère title id
        /// folder on the way says which game it is for, and mods for another game are left out.
        /// </summary>
        public static JsonObject ImportMod(string id, string path)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            string sourceName = Path.GetFileName(path.TrimEnd('/'));
            string extracted = null;

            try
            {
                DirectoryInfo source;

                if (Directory.Exists(path))
                {
                    source = new DirectoryInfo(path);
                }
                else if (File.Exists(path))
                {
                    extracted = Path.Combine(Path.GetTempPath(), $"Borgir-mod-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(extracted);

                    if (!Extract(path, extracted))
                    {
                        return NativeBridge.Error($"Borgir couldn't open {sourceName}. Extract it in Finder, then import the folder.");
                    }

                    source = new DirectoryInfo(extracted);
                    sourceName = Path.GetFileNameWithoutExtension(sourceName);
                }
                else
                {
                    return NativeBridge.Error($"{path} does not exist.");
                }

                List<FoundMod> found = [];

                // Choosing the romfs (or exefs, or cheats) folder itself means the folder holding it.
                if (_modParts.Contains(source.Name, StringComparer.OrdinalIgnoreCase) && source.Parent != null)
                {
                    found.Add(new FoundMod(source.Parent.Name, source.Parent, null));
                }
                else
                {
                    FindModRoots(source, sourceName, null, found, 0);
                }

                if (found.Count == 0)
                {
                    return NativeBridge.Error($"{sourceName} has no romfs, exefs or cheats folder inside, so it isn't a mod Borgir can load.");
                }

                JsonArray imported = [];
                JsonArray skipped = [];

                foreach (FoundMod mod in found)
                {
                    ulong titleId = mod.TitleId ?? game.Id;

                    if ((titleId & ~0x1FFFUL) != game.IdBase)
                    {
                        string other = Library.GetNameForApplicationId(titleId & ~0x1FFFUL)?.Trim();

                        skipped.Add((JsonNode)$"{mod.Name}: it is for {(string.IsNullOrEmpty(other) ? $"another game ({titleId:X16})" : other)}.");
                        continue;
                    }

                    // DLC have their own mods folder; an update's mods go with the base game's.
                    string targetTitle = (titleId & 0x1000) != 0 ? titleId.ToString("x16") : game.IdString;
                    string destination = Path.Combine(ModsDirectory(targetTitle), SafeName(mod.Name));

                    if (Path.Exists(destination))
                    {
                        skipped.Add((JsonNode)$"{mod.Name}: a mod with this name is already installed.");
                        continue;
                    }

                    // A folder of our own extraction can simply move; the player's own folder is copied.
                    if (extracted != null && mod.Root.FullName != source.FullName)
                    {
                        Directory.Move(mod.Root.FullName, destination);
                    }
                    else
                    {
                        CopyDirectory(mod.Root, destination);
                    }

                    imported.Add((JsonNode)Path.GetFileName(destination));
                    Logger.Info?.Print(LogClass.Application, $"Imported mod '{Path.GetFileName(destination)}' for {game.Name} from {path}");
                }

                if (imported.Count == 0)
                {
                    return NativeBridge.Error(string.Join("\n", skipped.Select(reason => reason.GetValue<string>())));
                }

                return new JsonObject { ["ok"] = true, ["imported"] = imported, ["skipped"] = skipped };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return NativeBridge.Error($"{sourceName} could not be imported: {ex.Message}");
            }
            finally
            {
                if (extracted != null)
                {
                    try
                    {
                        Directory.Delete(extracted, true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Could not clean up {extracted}: {ex.Message}");
                    }
                }
            }
        }

        private static void FindModRoots(DirectoryInfo directory, string name, ulong? titleId, List<FoundMod> found, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (directory.EnumerateFileSystemInfos().Any(entry => _modParts.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            {
                found.Add(new FoundMod(name, directory, titleId));
                return;
            }

            foreach (DirectoryInfo child in directory.EnumerateDirectories().OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (child.Name.StartsWith('.') || child.Name == "__MACOSX")
                {
                    continue;
                }

                if (child.Name.Length == 16 && ulong.TryParse(child.Name, System.Globalization.NumberStyles.HexNumber, null, out ulong childTitle))
                {
                    // contents/<title id> says which game; the name stays what the download called the mod.
                    FindModRoots(child, name, childTitle, found, depth + 1);
                }
                else if (child.Name.Equals("atmosphere", StringComparison.OrdinalIgnoreCase) || child.Name.Equals("contents", StringComparison.OrdinalIgnoreCase))
                {
                    FindModRoots(child, name, titleId, found, depth + 1);
                }
                else
                {
                    FindModRoots(child, child.Name, titleId, found, depth + 1);
                }
            }
        }

        /// <summary>
        /// Extracts an archive with the system's bsdtar, which refuses absolute paths and ".." in entry names.
        /// </summary>
        private static bool Extract(string archive, string destination)
        {
            using Process tar = Process.Start(new ProcessStartInfo("/usr/bin/tar", ["-xf", archive, "-C", destination])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            });

            string errors = tar.StandardError.ReadToEnd();
            tar.WaitForExit();

            if (tar.ExitCode != 0)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not extract {archive}: {errors.Trim()}");
            }

            return tar.ExitCode == 0;
        }

        private static void CopyDirectory(DirectoryInfo source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (FileInfo file in source.EnumerateFiles())
            {
                file.CopyTo(Path.Combine(destination, file.Name));
            }

            foreach (DirectoryInfo child in source.EnumerateDirectories())
            {
                CopyDirectory(child, Path.Combine(destination, child.Name));
            }
        }

        private static string SafeName(string name)
        {
            string safe = string.Concat(name.Select(character => character is '/' or ':' ? '-' : character)).Trim().TrimStart('.');

            return safe.Length == 0 ? "Mod" : safe;
        }

        public static JsonObject SetMod(string id, string path, bool enabled)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            ModMetadata metadata = new();

            foreach (Mod mod in FindMods(game, out _))
            {
                if (mod.Path == path)
                {
                    mod.Enabled = enabled;
                }

                metadata.Mods.Add(mod);
            }

            string directory = Path.Combine(AppDataManager.GamesDirPath, game.IdString);
            Directory.CreateDirectory(directory);
            JsonHelper.SerializeToFile(Path.Combine(directory, "mods.json"), metadata, _modSerializerContext.ModMetadata);

            return NativeBridge.Ok();
        }

        /// <summary>
        /// Turns one cheat on or off. Cheats are kept the way upstream Ryujinx keeps them, as lines in the game's
        /// mods/contents/&lt;id&gt;/cheats/enabled.txt, and a running game picks the change up at once.
        /// </summary>
        public static JsonObject SetCheat(string id, string key, bool enabled)
        {
            if (!TryFindGame(id, out ApplicationData game, out JsonObject error))
            {
                return error;
            }

            HashSet<string> enabledCheats = ReadEnabledCheats(game);

            if (enabled ? !enabledCheats.Add(key) : !enabledCheats.Remove(key))
            {
                return NativeBridge.Ok();
            }

            try
            {
                string path = EnabledCheatsPath(game);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, enabledCheats.Order(StringComparer.Ordinal));
            }
            catch (Exception ex)
            {
                return NativeBridge.Error($"The cheat list could not be saved: {ex.Message}");
            }

            NativeApp.EnableCheatsInRunningGame(game.Id);

            return NativeBridge.Ok();
        }

        private static string EnabledCheatsPath(ApplicationData game)
            => Path.Combine(ModLoader.GetApplicationDir(ModLoader.GetModsBasePath(), game.IdString), "cheats", "enabled.txt");

        private static HashSet<string> ReadEnabledCheats(ApplicationData game)
        {
            string path = EnabledCheatsPath(game);

            try
            {
                return File.Exists(path)
                    ? File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal)
                    : [];
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not read {path}: {ex.Message}");
                return [];
            }
        }

        /// <summary>
        /// How enabled.txt names a cheat, matching the key TamperMachine registers it under.
        /// </summary>
        private static string CheatKey(ModLoader.Cheat cheat)
            => $"{Path.GetFileNameWithoutExtension(cheat.Path.Name).ToUpperInvariant()}-{cheat.Name}";

        /// <summary>
        /// The mod folder a cheat file came with, or "cheats" for the game's own cheats folder.
        /// </summary>
        private static string CheatSource(ModLoader.Cheat cheat)
        {
            DirectoryInfo cheatsDir = cheat.Path.Directory;
            DirectoryInfo owner = cheatsDir?.Parent;

            // contents/<id>/cheats has the title id folder above it; a mod's cheats have the mod's folder.
            return owner == null || ulong.TryParse(owner.Name, System.Globalization.NumberStyles.HexNumber, null, out _)
                ? cheatsDir?.Name ?? string.Empty
                : owner.Name;
        }

        /// <summary>
        /// The mods ModLoader finds for this game, in the data folder and on the emulated SD card, keyed the way
        /// mods.json keys them: a mod folder by the folder holding its romfs/exefs, a container by its file. Also
        /// returns the cheats found among them.
        /// </summary>
        private static List<Mod> FindMods(ApplicationData game, out List<ModLoader.Cheat> cheats)
        {
            ulong[] installedDlc = Library.DownloadableContents.Keys
                .Where(content => content.TitleIdBase == game.IdBase)
                .Select(content => content.TitleId)
                .ToArray();

            List<Mod> mods = [];
            List<ModLoader.Cheat> foundCheats = [];

            void Add(string name, string path, bool enabled)
            {
                if (mods.All(mod => mod.Path != path))
                {
                    mods.Add(new Mod { Name = name, Path = path, Enabled = enabled });
                }
            }

            foreach (string basePath in new[] { ModLoader.GetModsBasePath(), ModLoader.GetSdModsBasePath() })
            {
                ModLoader.ModCache cache = new();
                ModLoader.QueryContentsDir(cache, new DirectoryInfo(Path.Combine(basePath, "contents")), game.Id, installedDlc);

                foreach (ModLoader.Mod<DirectoryInfo> mod in cache.RomfsDirs.Concat(cache.ExefsDirs))
                {
                    Add(mod.Name, mod.Path.Parent!.FullName, mod.Enabled);
                }

                foreach (ModLoader.Mod<FileInfo> mod in cache.RomfsContainers.Concat(cache.ExefsContainers))
                {
                    Add(mod.Name, mod.Path.FullName, mod.Enabled);
                }

                foundCheats.AddRange(cache.Cheats.Where(cheat => foundCheats.All(found => CheatKey(found) != CheatKey(cheat))));
            }

            cheats = foundCheats;

            return mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// DLC carry no name of their own, so this reads one from the file name, as release names usually describe it:
        /// "Mario Kart 8 Deluxe [DLC Booster Course Pass] [0100152000023001][v65536][US].nsp" gives "Booster Course Pass".
        /// </summary>
        private static string DlcName(DownloadableContentModel content)
        {
            string fileName = Path.GetFileNameWithoutExtension(content.ContainerPath);

            foreach (Match match in Regex.Matches(fileName, @"\[([^\]]+)\]"))
            {
                string part = match.Groups[1].Value.Trim();

                // Skip title ids, versions, regions and other tags; a description has a space or a "DLC" prefix in it.
                if (part.Contains(' ') && !Regex.IsMatch(part, @"^[0-9A-Fa-f]{16}$"))
                {
                    return Regex.Replace(part, @"^DLC\s+", string.Empty, RegexOptions.IgnoreCase);
                }
            }

            string name = Library.GetNameForApplicationId(content.TitleId).Trim();

            return string.IsNullOrEmpty(name) ? content.TitleIdStr : name;
        }

        /// <summary>
        /// macOS file systems ignore case, so two paths that differ only in case name the same file.
        /// </summary>
        private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool TryFindGame(string id, out ApplicationData game, out JsonObject error)
        {
            game = null;
            error = null;

            if (!ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ulong titleId) || !Library.FindApplication(titleId, out game))
            {
                error = NativeBridge.Error("That game is no longer in the library.");
                return false;
            }

            return true;
        }
    }
}
