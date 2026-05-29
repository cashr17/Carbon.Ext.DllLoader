#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.DllLoader.API;
using Oxide.Ext.DllLoader.Model;
using static HarmonyLib.AccessTools;

#endregion

namespace Oxide.Ext.DllLoader.Mapper
{
    public sealed class DllLoaderMapper : DefaultAssemblyResolver, IDllLoaderMapperLoadable
    {
        /// <summary>
        /// File watcher and startup can invoke <see cref="ScanAndRegisterAssemblies"/> concurrently; dictionaries are not safe for concurrent writes.
        /// </summary>
        private readonly object _registryLock = new();

        private readonly IDictionary<string, AssemblyInfo> _assembliesInfoByName =
            new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly IDictionary<string, IReadOnlyCollection<PluginInfo>> _pluginsInfoByAssemblyName =
            new Dictionary<string, IReadOnlyCollection<PluginInfo>>(StringComparer.OrdinalIgnoreCase);

        private static readonly FieldInfo CecilResolverCacheField =
            typeof(DefaultAssemblyResolver).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mono.Cecil DefaultAssemblyResolver has no cache field.");

        #region AssemblyResolver

        private readonly ISet<string> _searchResolverDirs = new HashSet<string>();

        public void OnModLoad()
        {
            AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolve;
            AddSearchDirectory(Interface.Oxide.ExtensionDirectory);
        }

        public void OnShutdown()
        {
            RemoveSearchDirectory(Interface.Oxide.ExtensionDirectory);
            AppDomain.CurrentDomain.AssemblyResolve -= AssemblyResolve;
            lock (_registryLock)
            {
                _assembliesInfoByName.Values.Do(a => a.Dispose());
                _assembliesInfoByName.Clear();
                _pluginsInfoByAssemblyName.Clear();
            }
        }

        private Assembly? AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = AssemblyNameReference.Parse(args.Name);
            var assemblyInfo = GetAssemblyInfoFromNameReference(assemblyName);
            if (assemblyInfo != null)
                return assemblyInfo.Assembly;

            return TryAssemblyLoadFromSearchDirectories(assemblyName.Name);
        }

        /// <summary>
        /// Loads a dependency that was not recorded in the Cecil registry yet (ordering, omitted scan, renamed file, etc.).
        /// Uses <see cref="AddDirectoryToResolver"/> paths (extensions + scanned plugin dirs).
        /// </summary>
        private Assembly? TryAssemblyLoadFromSearchDirectories(string? simpleAssemblyName)
        {
            if (string.IsNullOrEmpty(simpleAssemblyName))
                return null;

            string[] dirs;
            lock (_registryLock)
            {
                if (_searchResolverDirs.Count == 0)
                    return null;
                dirs = _searchResolverDirs.ToArray();
            }

            foreach (var dir in dirs)
            {
                string path;
                try
                {
                    path = Path.Combine(dir, simpleAssemblyName + ".dll");
                }
                catch
                {
                    continue;
                }

                if (!File.Exists(path))
                    continue;

                try
                {
                    return LoadAssemblyFromDiskWithoutFileLock(path);
                }
                catch
                {
                    //
                }
            }

            return null;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (!name.FullName.StartsWith("Oxide.") && !name.Name.StartsWith("Oxide.") &&
                !name.FullName.StartsWith("Carbon.") && !name.Name.StartsWith("Carbon."))
            {
                var assemblyInfo = GetAssemblyInfoFromNameReference(name);
                if (assemblyInfo != null)
                    return assemblyInfo.AssemblyDefinition;
            }

            return base.Resolve(name);
        }

        private void AddDirectoryToResolver(string directory)
        {
            if (!_searchResolverDirs.Contains(directory))
            {
                _searchResolverDirs.Add(directory);
                AddSearchDirectory(directory);
            }
        }

        #endregion

        #region ScanDiretory

        public IEnumerable<string> ScanDirectoryPlugins(string directory)
        {
            AddDirectoryToResolver(directory);
            ScanAndRegisterAssemblies(directory);

#if DEBUG
            lock (_registryLock)
                Interface.Oxide.LogDebug("Total assemblies registered({0}).", _assembliesInfoByName.Count);
#endif

            string[] plugins;
            lock (_registryLock)
                plugins = _assembliesInfoByName.Values.SelectMany(ai => ai.PluginsName).ToArray();
#if DEBUG
            Interface.Oxide.LogDebug("Total plugins registered({0}).", plugins.Length);
#endif
            return plugins;
        }

        public void ScanAndRegisterAssemblies(string directory)
        {
#if DEBUG
            Interface.Oxide.LogDebug("Scanning directory({0}) for assemblies...", directory);
#endif
            if (!Directory.Exists(directory))
            {
                Interface.Oxide.LogWarning("Fail to scan directory({0}), directory not found.", directory);
                return;
            }

            AddDirectoryToResolver(directory);

            var dirFiles = new DirectoryInfo(directory).GetFiles("*.dll", SearchOption.AllDirectories);

            lock (_registryLock)
            {
                foreach (var file in dirFiles)
                    RegisterAssemblyFromFileCore(file);

                foreach (var assemblyInfo in _assembliesInfoByName.Values.Where(a =>
                             !_pluginsInfoByAssemblyName.ContainsKey(a.OriginalName)))
                    try
                    {
                        _pluginsInfoByAssemblyName[assemblyInfo.OriginalName] = assemblyInfo.PluginsInfo;
                    }
                    catch (Exception ex)
                    {
                        Interface.Oxide.LogException($"Fail to patch assembly({assemblyInfo.OriginalName})", ex);
                    }
            }
        }

        /// <remarks>Caller must hold <see cref="_registryLock"/>.</remarks>
        private void RegisterAssemblyFromFileCore(FileInfo file)
        {
            if (!file.Exists)
            {
                Interface.Oxide.LogError("Fail to load assembly({0}), file does not exist.", file.FullName);
                return;
            }

#if DEBUG
            Interface.Oxide.LogDebug("Found file({0}) in directory({1}).", file.Name, file.DirectoryName);
#endif
            if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            {
                Interface.Oxide.LogInfo("Ignoring file({0}), marked as hidden.", file.Name);
                return;
            }

            AssemblyDefinition assemblyDefinition;
            try
            {
                assemblyDefinition = GetAssemblyDefinitionFromFile(file.FullName);
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogWarning("DllLoader: cannot read '{0}' (still copying or invalid PE): {1}", file.FullName,
                    ex.Message);
                return;
            }

            var assemblyDefinitionName = assemblyDefinition.Name;
            var assemblyInfo = GetAssemblyInfoFromNameReference(assemblyDefinitionName);
            if (assemblyInfo != null)
            {
#if DEBUG
                Interface.Oxide.LogDebug("Assembly({0}) already registered.", file.FullName);
#endif
                return;
            }

            try
            {
                assemblyInfo = new AssemblyInfo(assemblyDefinition, file.FullName, this);

                _assembliesInfoByName.Add(assemblyInfo.OriginalName, assemblyInfo);
                var cache =
                    (IDictionary<string, AssemblyDefinition>)CecilResolverCacheField.GetValue(this)!;
                cache[assemblyInfo.OriginalName] = assemblyInfo.AssemblyDefinition;
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogWarning("DllLoader: failed to register '{0}': {1}", file.FullName, ex.Message);
            }
        }

        #endregion

        #region Getters

        public IReadOnlyCollection<PluginInfo> GetAllPlugins()
        {
            lock (_registryLock)
                return _pluginsInfoByAssemblyName.Values.SelectMany(p => p).ToList();
        }

        public AssemblyDefinition GetAssemblyDefinitionFromFile(string filepath)
        {
            return AssemblyDefinition.ReadAssembly(filepath, new ReaderParameters
            {
                AssemblyResolver = this,
                InMemory = true,
                ReadingMode = ReadingMode.Immediate,
            });
        }

        public AssemblyInfo? GetAssemblyInfoFromNameReference(AssemblyNameReference assemblyNameReference)
        {
            lock (_registryLock)
                return _assembliesInfoByName.TryGetValue(assemblyNameReference.Name, out var assemblyInfo)
                    ? assemblyInfo
                    : null;
        }

        public AssemblyInfo? GetAssemblyInfoByPlugin(string pluginName)
        {
            lock (_registryLock)
                return _assembliesInfoByName.Values.FirstOrDefault(assemblyInfo =>
                    assemblyInfo.ContainsPlugin(pluginName));
        }

        public bool TryGetTrackedDllPluginClassName(string lookupName, out string? pluginClassName)
        {
            pluginClassName = null;
            if (string.IsNullOrWhiteSpace(lookupName))
                return false;

            var trimmed = lookupName.Trim();

            if (GetAssemblyInfoByPlugin(trimmed) != null)
            {
                pluginClassName = trimmed;
                return true;
            }

            foreach (var pi in GetAllPlugins())
            {
                if (string.Equals(pi.PluginName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    pluginClassName = pi.PluginName;
                    return true;
                }

                var ia = pi.PluginType.GetCustomAttribute<InfoAttribute>(true);
                var titleRaw = ia?.Title;
                if (titleRaw == null || string.IsNullOrWhiteSpace(titleRaw))
                    continue;

                var titleNorm = titleRaw.Trim();
                if (string.Equals(titleNorm, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    pluginClassName = pi.PluginName;
                    return true;
                }
            }

            return false;
        }

        public AssemblyInfo? GetAssemblyInfoByFilename(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            var probe = fileName.Trim();

            lock (_registryLock)
            {
                foreach (var assemblyInfo in _assembliesInfoByName.Values)
                {
                    if (string.Equals(assemblyInfo.AssemblyFile, probe, StringComparison.OrdinalIgnoreCase))
                        return assemblyInfo;
                }

                return _assembliesInfoByName.Values.FirstOrDefault(assemblyInfo =>
                    assemblyInfo.IsFile(fileName));
            }
        }

        public void RemoveAssembly(AssemblyInfo assemblyInfo)
        {
            lock (_registryLock)
            {
                _assembliesInfoByName.Remove(assemblyInfo.OriginalName);
                _pluginsInfoByAssemblyName.Remove(assemblyInfo.OriginalName);
            }

            assemblyInfo.Dispose();
        }

        #endregion

        private static Assembly LoadAssemblyFromDiskWithoutFileLock(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var rawAsm = ms.ToArray();

            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdbPath))
                return Assembly.Load(rawAsm);

            try
            {
                using var pdbFs = new FileStream(pdbPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var pdbMs = new MemoryStream();
                pdbFs.CopyTo(pdbMs);
                return Assembly.Load(rawAsm, pdbMs.ToArray());
            }
            catch
            {
                return Assembly.Load(rawAsm);
            }
        }
    }
}