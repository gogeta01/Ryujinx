using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using LibHac.FsSystem.RomFs;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using Ryujinx.HLE.Loaders.Executables;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    public class ModLoader
    {
        private const string RomfsDir = "romfs";
        private const string ExefsDir = "exefs";
        private const string RomfsContainer = "romfs.bin";
        private const string ExefsContainer = "exefs.nsp";
        private const string StubExtension = ".stub";

        private const string AmsContentsDir = "contents";
        private const string AmsNsoPatchDir = "exefs_patches";
        private const string AmsNroPatchDir = "nro_patches";
        private const string AmsKipPatchDir = "kip_patches";

        public struct Mod<T> where T : FileSystemInfo
        {
            public readonly string Name;
            public readonly T Path;

            public Mod(string name, T path)
            {
                Name = name;
                Path = path;
            }
        }

        // Title dependent mods
        public class ModCache
        {
            public List<Mod<FileInfo>> RomfsContainers { get; }
            public List<Mod<FileInfo>> ExefsContainers { get; }

            public List<Mod<DirectoryInfo>> RomfsDirs { get; }
            public List<Mod<DirectoryInfo>> ExefsDirs { get; }

            public ModCache()
            {
                RomfsContainers = new List<Mod<FileInfo>>();
                ExefsContainers = new List<Mod<FileInfo>>();
                RomfsDirs = new List<Mod<DirectoryInfo>>();
                ExefsDirs = new List<Mod<DirectoryInfo>>();
            }
        }

        // Title independent mods
        public class PatchCache
        {
            public List<Mod<DirectoryInfo>> NsoPatches { get; }
            public List<Mod<DirectoryInfo>> NroPatches { get; }
            public List<Mod<DirectoryInfo>> KipPatches { get; }

            public HashSet<string> SearchedDirs { get; }

            public PatchCache()
            {
                NsoPatches = new List<Mod<DirectoryInfo>>();
                NroPatches = new List<Mod<DirectoryInfo>>();
                KipPatches = new List<Mod<DirectoryInfo>>();

                SearchedDirs = new HashSet<string>();
            }
        }

        public Dictionary<ulong, ModCache> AppMods; // key is TitleId
        public PatchCache Patches;

        private static readonly EnumerationOptions _dirEnumOptions;

        static ModLoader()
        {
            _dirEnumOptions = new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Simple,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            };
        }

        public ModLoader()
        {
            AppMods = new Dictionary<ulong, ModCache>();
            Patches = new PatchCache();
        }

        public void Clear()
        {
            AppMods.Clear();
            Patches = new PatchCache();
        }

        private static bool StrEquals(string s1, string s2) => string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);

        public void EnsureBaseDirStructure(string modsBasePath)
        {
            var modsDir = new DirectoryInfo(modsBasePath);
            modsDir.Create();

            modsDir.CreateSubdirectory(AmsContentsDir);
            modsDir.CreateSubdirectory(AmsNsoPatchDir);
            modsDir.CreateSubdirectory(AmsNroPatchDir);
            // modsDir.CreateSubdirectory(AmsKipPatchDir); // uncomment when KIPs are supported
        }

        private static DirectoryInfo FindTitleDir(DirectoryInfo contentsDir, string titleId)
            => contentsDir.EnumerateDirectories($"{titleId}*", _dirEnumOptions).FirstOrDefault();

        public string GetTitleDir(string modsBasePath, string titleId)
        {
            var contentsDir = new DirectoryInfo(Path.Combine(modsBasePath, AmsContentsDir));
            var titleModsPath = FindTitleDir(contentsDir, titleId);

            if (titleModsPath == null)
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Creating mods dir for Title {titleId.ToUpper()}");
                titleModsPath = contentsDir.CreateSubdirectory(titleId);
            }

            return titleModsPath.FullName;
        }

        // Static Query Methods
        public static void QueryPatchDirs(PatchCache cache, DirectoryInfo patchDir, DirectoryInfo searchDir)
        {
            if (!patchDir.Exists || cache.SearchedDirs.Contains(searchDir.FullName)) return;

            var patches = cache.KipPatches;
            string type = null;

            if (StrEquals(AmsNsoPatchDir, patchDir.Name)) { patches = cache.NsoPatches; type = "NSO"; }
            else if (StrEquals(AmsNroPatchDir, patchDir.Name)) { patches = cache.NroPatches; type = "NRO"; }
            else if (StrEquals(AmsKipPatchDir, patchDir.Name)) { patches = cache.KipPatches; type = "KIP"; }
            else return;

            foreach (var modDir in patchDir.EnumerateDirectories())
            {
                patches.Add(new Mod<DirectoryInfo>(modDir.Name, modDir));
                Logger.Info?.Print(LogClass.ModLoader, $"Found {type} patch '{modDir.Name}'");
            }
        }

        public static void QueryTitleDir(ModCache mods, DirectoryInfo titleDir)
        {
            if (!titleDir.Exists) return;

            var fsFile = new FileInfo(Path.Combine(titleDir.FullName, RomfsContainer));
            if (fsFile.Exists)
            {
                mods.RomfsContainers.Add(new Mod<FileInfo>($"<{titleDir.Name} RomFs>", fsFile));
            }

            fsFile = new FileInfo(Path.Combine(titleDir.FullName, ExefsContainer));
            if (fsFile.Exists)
            {
                mods.ExefsContainers.Add(new Mod<FileInfo>($"<{titleDir.Name} ExeFs>", fsFile));
            }

            System.Text.StringBuilder types = new System.Text.StringBuilder(5);

            foreach (var modDir in titleDir.EnumerateDirectories())
            {
                types.Clear();
                Mod<DirectoryInfo> mod = new Mod<DirectoryInfo>("", null);

                if (StrEquals(RomfsDir, modDir.Name))
                {
                    mods.RomfsDirs.Add(mod = new Mod<DirectoryInfo>($"<{titleDir.Name} RomFs>", modDir));
                    types.Append('R');
                }
                else if (StrEquals(ExefsDir, modDir.Name))
                {
                    mods.ExefsDirs.Add(mod = new Mod<DirectoryInfo>($"<{titleDir.Name} ExeFs>", modDir));
                    types.Append('E');
                }
                else
                {
                    var romfs = new DirectoryInfo(Path.Combine(modDir.FullName, RomfsDir));
                    var exefs = new DirectoryInfo(Path.Combine(modDir.FullName, ExefsDir));
                    if (romfs.Exists)
                    {
                        mods.RomfsDirs.Add(mod = new Mod<DirectoryInfo>(modDir.Name, romfs));
                        types.Append('R');
                    }
                    if (exefs.Exists)
                    {
                        mods.ExefsDirs.Add(mod = new Mod<DirectoryInfo>(modDir.Name, exefs));
                        types.Append('E');
                    }
                }

                if (types.Length > 0) Logger.Info?.Print(LogClass.ModLoader, $"Found mod '{mod.Name}' [{types}]");
            }
        }

        public static void QueryContentsDir(ModCache mods, DirectoryInfo contentsDir, ulong titleId)
        {
            if (!contentsDir.Exists) return;

            Logger.Info?.Print(LogClass.ModLoader, $"Searching mods for Title {titleId:X16}");

            var titleDir = FindTitleDir(contentsDir, $"{titleId:x16}");

            if (titleDir != null)
            {
                QueryTitleDir(mods, titleDir);
            }
        }

        public static void CollectMods(ModCache mods, PatchCache patches, ulong? titleId, params string[] searchDirPaths)
        {
            static bool IsPatchesDir(string name) => StrEquals(AmsNsoPatchDir, name) ||
                                                     StrEquals(AmsNroPatchDir, name) ||
                                                     StrEquals(AmsKipPatchDir, name);

            static bool TryQuery(ModCache mods, PatchCache patches, ulong? titleId, DirectoryInfo dir, DirectoryInfo searchDir)
            {
                if (StrEquals(AmsContentsDir, dir.Name))
                {
                    if (titleId.HasValue)
                    {
                        QueryContentsDir(mods, dir, (ulong)titleId);

                        return true;
                    }
                }
                else if (IsPatchesDir(dir.Name))
                {
                    QueryPatchDirs(patches, dir, searchDir);

                    return true;
                }

                return false;
            }

            foreach (var path in searchDirPaths)
            {
                var dir = new DirectoryInfo(path);
                if (!dir.Exists)
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"Mod Search Dir '{dir.FullName}' doesn't exist");
                    continue;
                }

                if (!TryQuery(mods, patches, titleId, dir, dir))
                {
                    foreach (var subdir in dir.EnumerateDirectories())
                    {
                        TryQuery(mods, patches, titleId, subdir, dir);
                    }
                }

                patches.SearchedDirs.Add(dir.FullName);
            }
        }

        public void CollectMods(ulong titleId, params string[] searchDirPaths)
        {
            if (!AppMods.TryGetValue(titleId, out ModCache mods))
            {
                mods = new ModCache();
                AppMods[titleId] = mods;
            }

            CollectMods(mods, Patches, titleId, searchDirPaths);
        }

        internal IStorage ApplyRomFsMods(ulong titleId, IStorage baseStorage)
        {
            if (!AppMods.TryGetValue(titleId, out ModCache mods) || mods.RomfsDirs.Count + mods.RomfsContainers.Count == 0)
            {
                return baseStorage;
            }

            var fileSet = new HashSet<string>();
            var builder = new RomFsBuilder();
            int count = 0;

            Logger.Info?.Print(LogClass.ModLoader, $"Applying RomFS mods for Title {titleId:X16}");

            // Prioritize loose files first
            foreach (var mod in mods.RomfsDirs)
            {
                using (IFileSystem fs = new LocalFileSystem(mod.Path.FullName))
                {
                    AddFiles(fs, mod.Name, fileSet, builder);
                }
                count++;
            }

            // Then files inside images
            foreach (var mod in mods.RomfsContainers)
            {
                Logger.Info?.Print(LogClass.ModLoader, $"Found 'romfs.bin' for Title {titleId:X16}");
                using (IFileSystem fs = new RomFsFileSystem(mod.Path.OpenRead().AsStorage()))
                {
                    AddFiles(fs, mod.Name, fileSet, builder);
                }
                count++;
            }

            if (fileSet.Count == 0)
            {
                Logger.Info?.Print(LogClass.ModLoader, "No files found. Using base RomFS");

                return baseStorage;
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Replaced {fileSet.Count} file(s) over {count} mod(s). Processing base storage...");

            // And finally, the base romfs
            var baseRom = new RomFsFileSystem(baseStorage);
            foreach (var entry in baseRom.EnumerateEntries()
                                         .Where(f => f.Type == DirectoryEntryType.File && !fileSet.Contains(f.FullPath))
                                         .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                baseRom.OpenFile(out IFile file, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                builder.AddFile(entry.FullPath, file);
            }

            Logger.Info?.Print(LogClass.ModLoader, "Building new RomFS...");
            IStorage newStorage = builder.Build();
            Logger.Info?.Print(LogClass.ModLoader, "Using modded RomFS");

            return newStorage;
        }

        private static void AddFiles(IFileSystem fs, string modName, HashSet<string> fileSet, RomFsBuilder builder)
        {
            foreach (var entry in fs.EnumerateEntries()
                                    .Where(f => f.Type == DirectoryEntryType.File)
                                    .OrderBy(f => f.FullPath, StringComparer.Ordinal))
            {
                fs.OpenFile(out IFile file, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                if (fileSet.Add(entry.FullPath))
                {
                    builder.AddFile(entry.FullPath, file);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ModLoader, $"    Skipped duplicate file '{entry.FullPath}' from '{modName}'", "ApplyRomFsMods");
                }
            }
        }

        internal bool ReplaceExefsPartition(ulong titleId, ref IFileSystem exefs)
        {
            if (!AppMods.TryGetValue(titleId, out ModCache mods) || mods.ExefsContainers.Count == 0)
            {
                return false;
            }

            if (mods.ExefsContainers.Count > 1)
            {
                Logger.Warning?.Print(LogClass.ModLoader, "Multiple ExeFS partition replacements detected");
            }

            Logger.Info?.Print(LogClass.ModLoader, $"Using replacement ExeFS partition");

            exefs = new PartitionFileSystem(mods.ExefsContainers[0].Path.OpenRead().AsStorage());

            return true;
        }

        internal bool ApplyExefsMods(ulong titleId, List<NsoExecutable> nsos)
        {
            if (!AppMods.TryGetValue(titleId, out ModCache mods) || mods.ExefsDirs.Count == 0)
            {
                return false;
            }

            bool replaced = false;

            if (nsos.Count > 32)
            {
                throw new ArgumentOutOfRangeException("NSO Count is more than 32");
            }

            var exeMods = mods.ExefsDirs;

            BitVector32 stubs = new BitVector32();
            BitVector32 repls = new BitVector32();

            foreach (var mod in exeMods)
            {
                for (int i = 0; i < nsos.Count; ++i)
                {
                    var nso = nsos[i];
                    var nsoName = nso.Name;

                    FileInfo nsoFile = new FileInfo(Path.Combine(mod.Path.FullName, nsoName));
                    if (nsoFile.Exists)
                    {
                        if (repls[1 << i])
                        {
                            Logger.Warning?.Print(LogClass.ModLoader, $"Multiple replacements to '{nsoName}'");
                            continue;
                        }

                        repls[1 << i] = true;

                        nsos[i] = new NsoExecutable(nsoFile.OpenRead().AsStorage(), nsoName);
                        Logger.Info?.Print(LogClass.ModLoader, $"NSO '{nsoName}' replaced");

                        replaced = true;

                        continue;
                    }

                    stubs[1 << i] |= File.Exists(Path.Combine(mod.Path.FullName, nsoName + StubExtension));
                }
            }

            for (int i = nsos.Count - 1; i >= 0; --i)
            {
                if (stubs[1 << i] && !repls[1 << i]) // Prioritizes replacements over stubs
                {
                    Logger.Info?.Print(LogClass.ModLoader, $"    NSO '{nsos[i].Name}' stubbed");
                    nsos.RemoveAt(i);
                    replaced = true;
                }
            }

            return replaced;
        }

        internal void ApplyNroPatches(NroExecutable nro)
        {
            var nroPatches = Patches.NroPatches;

            if (nroPatches.Count == 0) return;

            // NRO patches aren't offset relative to header unlike NSO
            // according to Atmosphere's ro patcher module
            ApplyProgramPatches(nroPatches, 0, nro);
        }

        internal bool ApplyNsoPatches(ulong titleId, params IExecutable[] programs)
        {
            IEnumerable<Mod<DirectoryInfo>> nsoMods = Patches.NsoPatches;

            if (AppMods.TryGetValue(titleId, out ModCache mods))
            {
                nsoMods = nsoMods.Concat(mods.ExefsDirs);
            }

            // NSO patches are created with offset 0 according to Atmosphere's patcher module
            // But `Program` doesn't contain the header which is 0x100 bytes. So, we adjust for that here
            return ApplyProgramPatches(nsoMods, 0x100, programs);
        }

        private static bool ApplyProgramPatches(IEnumerable<Mod<DirectoryInfo>> mods, int protectedOffset, params IExecutable[] programs)
        {
            int count = 0;

            MemPatch[] patches = new MemPatch[programs.Length];

            for (int i = 0; i < patches.Length; ++i)
            {
                patches[i] = new MemPatch();
            }

            var buildIds = programs.Select(p => p switch
            {
                NsoExecutable nso => BitConverter.ToString(nso.BuildId.Bytes.ToArray()).Replace("-", "").TrimEnd('0'),
                NroExecutable nro => BitConverter.ToString(nro.Header.BuildId).Replace("-", "").TrimEnd('0'),
                _ => string.Empty
            }).ToList();

            int GetIndex(string buildId) => buildIds.FindIndex(id => id == buildId); // O(n) but list is small

            // Collect patches
            foreach (var mod in mods)
            {
                var patchDir = mod.Path;
                foreach (var patchFile in patchDir.EnumerateFiles())
                {
                    if (StrEquals(".ips", patchFile.Extension)) // IPS|IPS32
                    {
                        string filename = Path.GetFileNameWithoutExtension(patchFile.FullName).Split('.')[0];
                        string buildId = filename.TrimEnd('0');

                        int index = GetIndex(buildId);
                        if (index == -1)
                        {
                            continue;
                        }

                        Logger.Info?.Print(LogClass.ModLoader, $"Matching IPS patch '{patchFile.Name}' in '{mod.Name}' bid={buildId}");

                        using var fs = patchFile.OpenRead();
                        using var reader = new BinaryReader(fs);

                        var patcher = new IpsPatcher(reader);
                        patcher.AddPatches(patches[index]);
                    }
                    else if (StrEquals(".pchtxt", patchFile.Extension)) // IPSwitch
                    {
                        using var fs = patchFile.OpenRead();
                        using var reader = new StreamReader(fs);

                        var patcher = new IPSwitchPatcher(reader);

                        int index = GetIndex(patcher.BuildId);
                        if (index == -1)
                        {
                            continue;
                        }

                        Logger.Info?.Print(LogClass.ModLoader, $"Matching IPSwitch patch '{patchFile.Name}' in '{mod.Name}' bid={patcher.BuildId}");

                        patcher.AddPatches(patches[index]);
                    }
                }
            }

            // Apply patches
            for (int i = 0; i < programs.Length; ++i)
            {
                count += patches[i].Patch(programs[i].Program, protectedOffset);
            }

            return count > 0;
        }
    }
}