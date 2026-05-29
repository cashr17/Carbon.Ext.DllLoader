#region

using System.Collections.Generic;
using Mono.Cecil;
using Oxide.Core.Plugins;
using Oxide.Ext.DllLoader.Model;

#endregion

namespace Oxide.Ext.DllLoader.API
{
    public interface IDllLoaderMapper : IAssemblyResolver
    {
        IReadOnlyCollection<PluginInfo> GetAllPlugins();

        AssemblyInfo? GetAssemblyInfoByFilename(string fileName);

        AssemblyInfo? GetAssemblyInfoByPlugin(string pluginName);

        /// <summary>
        /// Maps an Oxide/Carbon lookup name (class name or <see cref="Oxide.Core.Plugins.InfoAttribute.Title"/>) to the tracked CLR plugin type name used by this loader.
        /// </summary>
        bool TryGetTrackedDllPluginClassName(string lookupName, out string? pluginClassName);

        IEnumerable<string> ScanDirectoryPlugins(string directory);

        void RemoveAssembly(AssemblyInfo assemblyInfo);

        void ScanAndRegisterAssemblies(string directory);
    }
}