#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using Oxide.Ext.DllLoader.API;
using Oxide.Ext.DllLoader.Model;

#endregion

namespace Oxide.Ext.DllLoader.Controller;

/// <summary>
/// Loads DLL <see cref="RustPlugin"/> types exclusively through <c>Carbon.Core.ModLoader.InitializePlugin</c> / <c>UninitializePlugin</c>.
/// </summary>
public sealed class DllPluginLoaderController
{
	private const int CarbonPkgBindPending = 0;
	private const int CarbonPkgBindOk = 1;

	private static readonly ConcurrentDictionary<string, object> CarbonPackageByDllPath =
		new(StringComparer.OrdinalIgnoreCase);

	private static volatile int _carbonPkgBindState;
	private static volatile bool _carbonBindReady;
	private static volatile bool _warnedCarbonPkgMissing;
	private static volatile bool _warnedCarbonRegisterPkgMissing;
	private static volatile bool _warnedCarbonFatalNoPluginPackageField;
	private static volatile bool _warnedCarbonInitProbe;
	private static readonly object CarbonPkgBindLock = new();
	private static FieldInfo? _carbonPluginPackageField;
	private static Type? _carbonPackageClrType;
	private static MethodInfo? _carbonPackageGetThreeArgs;
	private static MethodInfo? _carbonPackageGetTwoArgs;
	private static MethodInfo? _carbonModLoaderRegisterPackage;
	private static MethodInfo? _carbonModLoaderInitializePlugin;
	private static MethodInfo? _carbonModLoaderUninitializePlugin;
	private static Type? _boundCarbonModLoaderClrType;

	/// <summary>Same CLR type Carbon uses for static <see cref="_carbonModLoaderInitializePlugin"/> (not first random ModLoader match).</summary>
	internal static Type? BoundCarbonModLoaderClrType => _boundCarbonModLoaderClrType;

	/// <summary>
	/// Mirrors script <see cref="Carbon.Managers.ScriptLoader"/> preInit essentials: paths + ScriptProcessor, without compiler hook tables.
	/// </summary>
	private sealed class CarbonDllPreInit
	{
		private readonly string _absDllPath;

		public CarbonDllPreInit(string absDllPath) => _absDllPath = absDllPath;

		public void Invoke(RustPlugin p)
		{
			var pl = (Plugin)p;
			TryAssignPluginAssemblyPath(pl, _absDllPath);
			TryAssignCarbonPluginFilePathAndName(pl, _absDllPath);
			TryAssignScriptProcessorLikeScriptLoader(pl);
		}
	}

	public static void InvalidateCarbonPackageCache(string? assemblyDllFullPath)
	{
		if (assemblyDllFullPath is null || string.IsNullOrWhiteSpace(assemblyDllFullPath))
			return;

		CarbonPackageByDllPath.TryRemove(assemblyDllFullPath.Trim(), out _);
	}

	private readonly IDllLoaderMapperLoadable _mapper;
	private readonly Dictionary<string, PluginInfo> _pluginsWaitingDep = [];
	private readonly List<Plugin> _onFramePlugins = [];
	private readonly HashSet<string> _loadingPlugins = [];
	private readonly Dictionary<string, Plugin> _loadedPlugins = [];
	private readonly Dictionary<string, string> _pluginErrors = [];

	public DllPluginLoaderController(IDllLoaderMapperLoadable mapper) => _mapper = mapper;

	public IDllLoaderMapper Mapper => _mapper;
	public IReadOnlyCollection<Plugin> OnFramePlugins => _onFramePlugins;

	public void OnShutdown()
	{
		_pluginsWaitingDep.Clear();
		_onFramePlugins.Clear();
		_loadingPlugins.Clear();
		_loadedPlugins.Clear();
		_pluginErrors.Clear();
		_mapper.OnShutdown();
	}

	public bool LoadDllPluginRequest(string pluginName)
	{
#if DEBUG
		Interface.Oxide.LogDebug("Loading requested to plugin({0}).", pluginName);
#endif
		if (!_loadingPlugins.Add(pluginName))
		{
#if DEBUG
			Interface.Oxide.LogDebug("Load requested failed, plugin({0}) is already loading.", pluginName);
#endif
			return false;
		}

		if (_loadedPlugins.ContainsKey(pluginName))
		{
			_loadingPlugins.Remove(pluginName);
#if DEBUG
			Interface.Oxide.LogDebug("Load requested failed, plugin({0}) is already loaded.", pluginName);
#endif
			return false;
		}

		var assemblyInfo = Mapper.GetAssemblyInfoByPlugin(pluginName);
		if (assemblyInfo == null)
		{
			_loadingPlugins.Remove(pluginName);
			Interface.Oxide.LogError("Fail to find assembly info for plugin({0}).", pluginName);
			_pluginErrors[pluginName] = "Fail to find assembly info";
			return false;
		}

		var pluginInfo = assemblyInfo.GetPluginInfo(pluginName);
		if (pluginInfo == null)
		{
			_loadingPlugins.Remove(pluginName);
			Interface.Oxide.LogError("Fail to find plugin({0}) in assembly({1}).", pluginName, assemblyInfo.OriginalName);
			_pluginErrors[pluginName] = "Fail to find plugin info.";
			return false;
		}

		// If previous hot-reload left ghost instances in Carbon manager, purge them before loading a new copy.
		PurgeResidualPluginInstances(pluginInfo.PluginName, pluginInfo.PluginType.Name);

		Interface.Oxide.NextTick(() =>
		{
			try
			{
				var committed = TryAdvanceLoad(pluginInfo);
				if (!_pluginsWaitingDep.ContainsKey(pluginName) && !committed)
					_loadingPlugins.Remove(pluginName);
				else if (committed && _loadedPlugins.ContainsKey(pluginName))
					ResolveWaitingDependents(pluginName);
			}
			catch (Exception ex)
			{
				_loadingPlugins.Remove(pluginName);
				_pluginsWaitingDep.Remove(pluginName);
				Interface.Oxide.LogException($"[DllLoader] Deferred load failed ({pluginName})", ex);
			}
		});

		return true;
	}

	public bool UnloadDllPluginRequest(string pluginName)
	{
		_loadedPlugins.Remove(pluginName);

		var assemblyInfo = Mapper.GetAssemblyInfoByPlugin(pluginName);
		var pluginInfo = assemblyInfo?.GetPluginInfo(pluginName);
		if (pluginInfo == null)
		{
			Interface.Oxide.LogError("Plugin({0}) could not be unloaded correctly.", pluginName);
			return false;
		}

		var typeName = pluginInfo.PluginType.Name;
		var live =
			TryGetRustPluginFromManager(pluginName)
			?? (string.Equals(typeName, pluginName, StringComparison.OrdinalIgnoreCase)
				? null
				: TryGetRustPluginFromManager(typeName));

		pluginInfo.Dispose();
		_onFramePlugins.RemoveAll(p =>
			string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(p.GetType().Name, pluginName, StringComparison.OrdinalIgnoreCase));

		foreach (var depPlugin in _mapper.GetAllPlugins().Where(p =>
			         p.IsPluginLoaded && p.PluginReferences.Contains(pluginName)))
			Interface.Oxide.UnloadPlugin(depPlugin.PluginName);

		if (live != null)
			TryCarbonModLoaderUninitialize(live);

		// Always run a hard purge loop; a single remove-by-name often leaves "(1)" ghosts in manager.
		PurgeResidualPluginInstances(pluginName, typeName);
		return true;
	}

	public bool ReloadDllPluginRequest(string pluginName)
	{
		UnloadDllPluginRequest(pluginName);
		return LoadDllPluginRequest(pluginName);
	}

	private void ResolveWaitingDependents(string loadedPluginName)
	{
		foreach (var blockedName in _pluginsWaitingDep.Keys.ToArray())
		{
			if (!_pluginsWaitingDep.TryGetValue(blockedName, out var pending) ||
			    !pending.PluginReferences.Contains(loadedPluginName))
				continue;

			var committedNext = TryAdvanceLoad(pending);
			if (!committedNext && !_pluginsWaitingDep.ContainsKey(blockedName))
				_loadingPlugins.Remove(blockedName);

			if (committedNext && _loadedPlugins.ContainsKey(blockedName))
				ResolveWaitingDependents(blockedName);
		}
	}

	private bool TryAdvanceLoad(PluginInfo pluginInfo)
	{
		var pluginName = pluginInfo.PluginName;

		var referencesNotLoaded = pluginInfo.PluginReferences.Where(r => !_loadedPlugins.ContainsKey(r)).ToArray();
		if (referencesNotLoaded.Length != 0)
		{
			var missingReferences = referencesNotLoaded.Where(r => !_loadingPlugins.Contains(r)).ToArray();
			if (missingReferences.Length != 0)
			{
				_pluginErrors[pluginName] =
					$"Fail to load plugin({pluginName}) missing dependencies: {JoinNames(missingReferences)}";
				Interface.Oxide.LogError("Fail to load plugin({0}) missing dependencies: {1}", pluginName,
					JoinNames(missingReferences));
				return false;
			}

			if (referencesNotLoaded.Any(r => !_pluginsWaitingDep.ContainsKey(r)))
			{
				_pluginsWaitingDep[pluginName] = pluginInfo;
				return false;
			}
		}

		if (pluginInfo.IsPluginLoaded)
		{
			_loadedPlugins[pluginName] = pluginInfo.Plugin;
			_loadingPlugins.Remove(pluginName);
			_pluginsWaitingDep.Remove(pluginName);
			return true;
		}

		TryRemoveRustPluginFromManager(pluginName);
		pluginInfo.Dispose();

		var plugin = InstantiateRustPlugin(pluginInfo);

		if (plugin == null)
			return false;

		_loadedPlugins[pluginName] = plugin;
		_loadingPlugins.Remove(pluginName);
		_pluginsWaitingDep.Remove(pluginName);
		return true;
	}

	private RustPlugin? InstantiateRustPlugin(PluginInfo pluginInfo)
	{
		var pluginName = pluginInfo.PluginName;

		if (TryInstantiateRustPluginViaCarbonModLoader(pluginInfo, out var rustPlugin))
			return rustPlugin;

		var reflectOk = EnsureCarbonModPackageReflect();
		string reason;
		if (!reflectOk)
			reason =
				"[DllLoader] Carbon ModLoader/Package reflection incomplete (Carbon.Core.ModLoader or Package.Get not ready — check earlier warnings). Retries automatically when Core loads.";
		else if (_carbonModLoaderInitializePlugin == null)
			reason =
				"[DllLoader] ModLoader.InitializePlugin overload matching (Type|Assembly), out RustPlugin, Package, preInit delegate, bool not found.";
		else
			reason = "Carbon ModLoader.InitializePlugin returned false or threw; see preceding exception.";

		_pluginErrors[pluginName] = reason;
		Interface.Oxide.LogError("[DllLoader] Cannot load DLL plugin '{0}': {1}", pluginName, reason);
		return null;
	}

	private bool TryInstantiateRustPluginViaCarbonModLoader(PluginInfo pluginInfo, out RustPlugin? rustPlugin)
	{
		rustPlugin = null;
		if (!EnsureCarbonModPackageReflect())
			return false;

		var init = _carbonModLoaderInitializePlugin;
		if (init == null)
			return false;

		try
		{
			object pkg = GetOrCreateCarbonPackageForDll(pluginInfo.PluginFile);
			var holder = new CarbonDllPreInit(pluginInfo.PluginFile);
			var decl = typeof(CarbonDllPreInit).GetMethod(nameof(CarbonDllPreInit.Invoke),
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (decl == null)
				return false;

			var psInit = init.GetParameters();
			var preInitSlot = FindPreInitDelegateParameterIndex(psInit);
			if (preInitSlot < 0)
				return false;

			var actionT = psInit[preInitSlot].ParameterType;
			Delegate del = Delegate.CreateDelegate(actionT, holder, decl);

			if (!TryBuildCarbonInitializeInvokeArgs(init, pluginInfo, pkg, del, true, out var args, out var outRustIdx))
				return false;

			var okObj = init.Invoke(null, args);
			if (okObj is bool success && !success)
			{
				Interface.Oxide.LogWarning(
					"[DllLoader] Carbon ModLoader.InitializePlugin returned false for '{0}'.",
					pluginInfo.PluginName);
				return false;
			}

			rustPlugin = args[outRustIdx] as RustPlugin;
			if (rustPlugin == null)
				return false;

			pluginInfo.SetRuntimePlugin(rustPlugin);
			TryAllowCarbonConsoleReloadAfterDllModLoaderInit(rustPlugin);

			if (HasOnFrameHook(rustPlugin))
				_onFramePlugins.Add(rustPlugin);

			return true;
		}
		catch (TargetInvocationException tex)
		{
			var thrown = tex.InnerException ?? tex;
			Interface.Oxide.LogException(
				$"[DllLoader] Carbon ModLoader.InitializePlugin failed ({pluginInfo.PluginName})", thrown);
			return false;
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogException(
				$"[DllLoader] Carbon ModLoader.InitializePlugin failed ({pluginInfo.PluginName})", ex);
			return false;
		}
	}

	private static void TryAllowCarbonConsoleReloadAfterDllModLoaderInit(RustPlugin plugin)
	{
		try
		{
			const string flag = "IsPrecompiled";
			var basePlugin = typeof(global::Oxide.Core.Plugins.Plugin);
			var piProp = basePlugin.GetProperty(flag,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			var sm = piProp?.GetSetMethod(true);
			if (sm != null)
			{
				sm.Invoke(plugin, new object[] { false });
				return;
			}

			const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			foreach (var f in basePlugin.GetFields(bf))
			{
				var n = f.Name;
				if (!string.Equals(n, flag, StringComparison.Ordinal) &&
				    !string.Equals(n, "<IsPrecompiled>k__BackingField", StringComparison.Ordinal))
					continue;

				if (f.FieldType != typeof(bool))
					continue;

				f.SetValue(plugin, false);
				return;
			}
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[DllLoader] Could not clear IsPrecompiled for '{0}' ({1}): {2}",
				plugin.Name ?? "?", plugin.GetType().FullName, ex.Message);
		}
	}

	private static void TryAssignPluginAssemblyPath(Plugin plugin, string assemblyFilePath)
	{
		if (string.IsNullOrWhiteSpace(assemblyFilePath))
			return;

		const BindingFlags dec = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
		                         BindingFlags.DeclaredOnly;

		for (var cur = plugin.GetType();
		     cur != null && typeof(Plugin).IsAssignableFrom(cur);
		     cur = cur.BaseType)
		{
			foreach (var prop in cur.GetProperties(dec))
			{
				if (!IsFilenameLikeProperty(prop.Name))
					continue;

				var setter = prop.GetSetMethod(nonPublic: true);
				if (setter == null)
					continue;

				try
				{
					setter.Invoke(plugin, new object[] { assemblyFilePath });
					return;
				}
				catch (TargetInvocationException)
				{
					//
				}
			}

			foreach (var field in cur.GetFields(dec))
			{
				if (field.FieldType != typeof(string) || field.IsLiteral || field.IsInitOnly)
					continue;

				var fn = field.Name;
				var looksStoredPath = IsPathPropertyBackingField(fn) ||
				                      FilenameHintFields.Contains(fn);

				if (!looksStoredPath)
					continue;

				try
				{
					field.SetValue(plugin, assemblyFilePath);
					return;
				}
				catch (FieldAccessException)
				{
					//
				}
			}
		}

#if DEBUG
		Interface.Oxide.LogDebug("[DllLoader] Could not assign assembly path ({0}); plugin type {1} has no writable Filename/FileName backing store.",
			assemblyFilePath, plugin.GetType().FullName);
#endif
	}

	private static readonly HashSet<string> FilenameHintFields =
		new(StringComparer.OrdinalIgnoreCase)
		{
			"_filename", "__filename", "filename",
			"_fileName", "_FileName",
			"_filepath", "__filepath",
			"filepath", "_sourceFilePath", "_assemblyPath",
		};

	private static bool IsFilenameLikeProperty(string propName) =>
		string.Equals(propName, "Filename", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(propName, "FileName", StringComparison.OrdinalIgnoreCase);

	private static bool IsPathPropertyBackingField(string fieldName)
	{
		if (!fieldName.EndsWith(">k__BackingField", StringComparison.Ordinal))
			return false;

		var lt = fieldName.IndexOf('<');
		var gt = fieldName.IndexOf('>');
		if (lt < 0 || gt <= lt + 1)
			return false;

		var inner = fieldName.Substring(lt + 1, gt - lt - 1);
		return string.Equals(inner, "Filename", StringComparison.OrdinalIgnoreCase)
		       || string.Equals(inner, "FileName", StringComparison.OrdinalIgnoreCase);
	}

	private static Type UnwrapNullableStruct(Type t)
	{
		var u = Nullable.GetUnderlyingType(t);
		return u ?? t;
	}

	private static void TryAssignCarbonPluginFilePathAndName(Plugin plugin, string absDllPath)
	{
		if (string.IsNullOrWhiteSpace(absDllPath))
			return;

		var fileNameOnly = Path.GetFileName(absDllPath);
		const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		for (var cur = plugin.GetType(); cur != null && cur != typeof(object); cur = cur.BaseType)
		foreach (var prop in cur.GetProperties(inst))
		{
			object? value = null;
			if (string.Equals(prop.Name, "FilePath", StringComparison.Ordinal))
				value = absDllPath;
			else if (string.Equals(prop.Name, "FileName", StringComparison.Ordinal))
				value = fileNameOnly;
			else
				continue;

			var set = prop.GetSetMethod(true);
			if (set == null)
				continue;

			try
			{
				set.Invoke(plugin, new[] { value });
			}
			catch
			{
				//
			}
		}
	}

	private static void TryAssignScriptProcessorLikeScriptLoader(Plugin plugin)
	{
		try
		{
			var token = TryResolveCommunityScriptProcessorToken();
			if (token == null)
				return;

			for (var cur = plugin.GetType();
			     cur != null && typeof(Plugin).IsAssignableFrom(cur);
			     cur = cur.BaseType)
			{
				foreach (var m in cur.GetMethods(BindingFlags.Instance | BindingFlags.Public |
				                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (!string.Equals(m.Name, "SetProcessor", StringComparison.Ordinal))
						continue;

					var ps = m.GetParameters();
					if (ps.Length != 2)
						continue;

					if (!ps[0].ParameterType.IsInstanceOfType(token) && !ps[0].ParameterType.IsAssignableFrom(token.GetType()))
						continue;

					try
					{
						m.Invoke(plugin, new[] { token, null });
						return;
					}
					catch
					{
						//
					}
				}
			}
		}
		catch
		{
			//
		}
	}

	private static object? TryResolveCommunityScriptProcessorToken()
	{
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? rt;
			try
			{
				rt = asm.GetType("Community.Runtime", throwOnError: false, ignoreCase: false);
			}
			catch
			{
				continue;
			}

			if (rt == null)
				continue;

			foreach (var p in rt.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
			                                  BindingFlags.FlattenHierarchy))
			{
				if (!string.Equals(p.Name, "ScriptProcessor", StringComparison.Ordinal))
					continue;

				try
				{
					return p.GetValue(null);
				}
				catch
				{
					//
				}
			}
		}

		return null;
	}

	private static Type? TryFindCarbonCoreModLoaderType()
	{
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? t;
			try
			{
				t = asm.GetType("Carbon.Core.ModLoader", throwOnError: false, ignoreCase: false);
			}
			catch
			{
				continue;
			}

			if (t != null)
				return t;
		}

		return null;
	}

	private static Type? TryFindNestedModLoaderPackage(Type modLoader) =>
		modLoader.GetNestedType("Package", BindingFlags.Public | BindingFlags.NonPublic);

	private static bool BindPackageStaticGetMethods(Type primaryPackageClr)
	{
		const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		_carbonPackageGetThreeArgs = primaryPackageClr.GetMethod("Get", bf, Type.DefaultBinder,
			new[] { typeof(string), typeof(bool), typeof(string) }, null);
		_carbonPackageGetTwoArgs = primaryPackageClr.GetMethod("Get", bf, Type.DefaultBinder,
			new[] { typeof(string), typeof(bool) }, null);

		if (_carbonPackageGetThreeArgs != null || _carbonPackageGetTwoArgs != null)
			return true;

		foreach (var m in primaryPackageClr.GetMethods(bf))
		{
			if (!string.Equals(m.Name, "Get", StringComparison.Ordinal))
				continue;

			var ps = m.GetParameters();
			if (ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool) &&
			    ps[2].ParameterType == typeof(string))
				_carbonPackageGetThreeArgs = m;
			else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) &&
			         ps[1].ParameterType == typeof(bool))
				_carbonPackageGetTwoArgs = m;
		}

		return _carbonPackageGetThreeArgs != null || _carbonPackageGetTwoArgs != null;
	}

	private static void BindRegisterPackageFlexible(Type modLoader, Type packageClr)
	{
		_carbonModLoaderRegisterPackage = null;
		var pkgU = UnwrapNullableStruct(packageClr);

		foreach (var m in modLoader.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (!string.Equals(m.Name, "RegisterPackage", StringComparison.Ordinal))
				continue;

			var ps = m.GetParameters();
			if (ps.Length != 1)
				continue;

			var p0 = UnwrapNullableStruct(ps[0].ParameterType);
			if (p0 == pkgU || ps[0].ParameterType == packageClr ||
			    (string.Equals(p0.Name, "Package", StringComparison.OrdinalIgnoreCase) &&
			     string.Equals(pkgU.Name, "Package", StringComparison.OrdinalIgnoreCase)))
			{
				_carbonModLoaderRegisterPackage = m;
				return;
			}
		}
	}

	private static bool EnsureCarbonModPackageReflect()
	{
		if (_carbonBindReady)
			return true;

		lock (CarbonPkgBindLock)
		{
			if (_carbonBindReady)
				return true;

			_carbonPluginPackageField ??= typeof(Plugin).GetField("Package",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			if (_carbonPluginPackageField == null)
			{
				if (!_warnedCarbonFatalNoPluginPackageField)
				{
					_warnedCarbonFatalNoPluginPackageField = true;
					Interface.Oxide.LogError("[DllLoader] Plugin.Package field not found; Carbon DLL loading disabled.");
				}

				return false;
			}

			_carbonPackageClrType = UnwrapNullableStruct(_carbonPluginPackageField.FieldType);

			var modLoader = TryFindCarbonCoreModLoaderType();
			if (modLoader == null)
			{
				if (!_warnedCarbonPkgMissing)
				{
					_warnedCarbonPkgMissing = true;
					Interface.Oxide.LogWarning(
						"[DllLoader] Carbon.Core.ModLoader not loaded yet (will retry). Ensure Carbon core is present.");
				}

				return false;
			}

			if (!BindPackageStaticGetMethods(_carbonPackageClrType))
			{
				var nestedPkg = TryFindNestedModLoaderPackage(modLoader);
				if (nestedPkg != null && nestedPkg != _carbonPackageClrType)
					BindPackageStaticGetMethods(nestedPkg);
			}

			if (_carbonPackageGetThreeArgs == null && _carbonPackageGetTwoArgs == null)
			{
				if (!_warnedCarbonPkgMissing)
				{
					_warnedCarbonPkgMissing = true;
					Interface.Oxide.LogWarning(
						"[DllLoader] Could not resolve static Package.Get for {0}.",
						_carbonPackageClrType.FullName ?? _carbonPackageClrType.Name);
				}

				return false;
			}

			BindRegisterPackageFlexible(modLoader, _carbonPackageClrType);

			if (_carbonModLoaderRegisterPackage == null && !_warnedCarbonRegisterPkgMissing)
			{
				_warnedCarbonRegisterPkgMissing = true;
				Interface.Oxide.LogWarning(
					"[DllLoader] ModLoader.RegisterPackage(package) not bound; DLL packages may be missing from c.plugins.");
			}

			_carbonModLoaderInitializePlugin = PickCarbonModLoaderInitializeFlexible(modLoader, _carbonPackageClrType);
			_carbonModLoaderUninitializePlugin = modLoader.GetMethod("UninitializePlugin",
				BindingFlags.Public | BindingFlags.Static, null,
				new[] { typeof(RustPlugin), typeof(bool), typeof(bool) }, null);

			if (_carbonModLoaderInitializePlugin == null)
			{
				if (!_warnedCarbonInitProbe)
				{
					_warnedCarbonInitProbe = true;
					LogCarbonInitializePluginProbeOnce(modLoader);
				}

				return false;
			}

			_boundCarbonModLoaderClrType = _carbonModLoaderInitializePlugin.DeclaringType;

			_carbonPkgBindState = CarbonPkgBindOk;
			_carbonBindReady = true;
			return true;
		}
	}

	private static void LogCarbonInitializePluginProbeOnce(Type modLoader)
	{
		try
		{
			var list = modLoader.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(mi => mi.Name == "InitializePlugin")
				.Select(mi =>
				{
					var ps = mi.GetParameters();
					return $"{mi.Name}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name}"))})";
				})
				.Take(12)
				.ToArray();

			if (list.Length == 0)
				Interface.Oxide.LogWarning("[DllLoader] ModLoader has no public static InitializePlugin methods.");
			else
				Interface.Oxide.LogWarning(
					"[DllLoader] No compatible InitializePlugin overload. Candidates: {0}",
					string.Join(" | ", list));
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[DllLoader] InitializePlugin probe failed: {0}", ex.Message);
		}
	}

	private static MethodInfo? PickCarbonModLoaderInitializeFlexible(Type modLoader, Type packageClr)
	{
		foreach (var m in modLoader.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (m.Name != "InitializePlugin")
				continue;

			if (MatchesScriptStyleInitializePlugin(m, packageClr))
				return m;
		}

		return null;
	}

	private static bool MatchesScriptStyleInitializePlugin(MethodInfo m, Type packageClr) =>
		TryResolveCarbonInitializeSlots(m, packageClr, out _, out _, out _, out _, out _);

	private static bool TryResolveCarbonInitializeSlots(MethodInfo m, Type packageClr, out int typeOrAsmIdx,
		out int outRustIdx, out int pkgIdx, out int preInitIdx, out int precompiledBoolIdx)
	{
		typeOrAsmIdx = outRustIdx = pkgIdx = preInitIdx = precompiledBoolIdx = -1;

		var ps = m.GetParameters();
		if (ps.Length < 5)
			return false;

		for (var i = 0; i < ps.Length; i++)
		{
			var pt = ps[i].ParameterType;
			if (pt.IsByRef)
				continue;

			if (typeOrAsmIdx >= 0)
				continue;

			if (pt == typeof(Type) || pt == typeof(Assembly))
				typeOrAsmIdx = i;
		}

		for (var i = 0; i < ps.Length; i++)
		{
			var pt = ps[i].ParameterType;
			if (!pt.IsByRef)
				continue;

			var el = pt.GetElementType();
			if (el == null || !typeof(RustPlugin).IsAssignableFrom(el))
				continue;

			if (outRustIdx >= 0)
				return false;

			outRustIdx = i;
		}

		for (var i = 0; i < ps.Length; i++)
		{
			if (i == typeOrAsmIdx || i == outRustIdx)
				continue;

			if (!PackageParameterCompatible(ps[i].ParameterType, packageClr))
				continue;

			if (pkgIdx >= 0)
				return false;

			pkgIdx = i;
		}

		preInitIdx = FindPreInitDelegateParameterIndex(ps);
		if (typeOrAsmIdx < 0 || outRustIdx < 0 || pkgIdx < 0 || preInitIdx < 0)
			return false;

		var overlapping = typeOrAsmIdx == pkgIdx || typeOrAsmIdx == preInitIdx || outRustIdx == pkgIdx ||
		                  outRustIdx == preInitIdx || pkgIdx == preInitIdx;
		if (overlapping)
			return false;

		precompiledBoolIdx = ResolvePrecompiledBoolSlot(ps);
		return precompiledBoolIdx >= 0;
	}

	private static int ResolvePrecompiledBoolSlot(ParameterInfo[] ps)
	{
		for (var i = 0; i < ps.Length; i++)
			if (ps[i].ParameterType == typeof(bool) &&
			    ps[i].Name.IndexOf("precompiled", StringComparison.OrdinalIgnoreCase) >= 0)
				return i;

		var idx = Array.FindIndex(ps, p => p.ParameterType == typeof(bool));
		if (idx >= 0)
			return idx;

		for (var i = ps.Length - 1; i >= 0; i--)
			if (ps[i].ParameterType == typeof(bool))
				return i;

		return -1;
	}

	private static bool PackageParameterCompatible(Type paramType, Type packageClrField)
	{
		var a = UnwrapNullableStruct(paramType);
		var b = UnwrapNullableStruct(packageClrField);
		return a == b || a.Equals(b) ||
		       (string.Equals(a.Name, "Package", StringComparison.Ordinal) &&
		        string.Equals(b.Name, "Package", StringComparison.Ordinal));
	}

	private static bool IsCarbonPreInitDelegate(Type t)
	{
		if (!typeof(Delegate).IsAssignableFrom(t))
			return false;

		var inv = t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
		if (inv == null)
			return false;

		var ps = inv.GetParameters();
		return ps.Length == 1 && typeof(Plugin).IsAssignableFrom(ps[0].ParameterType);
	}

	private static int FindPreInitDelegateParameterIndex(ParameterInfo[] ps)
	{
		for (var i = 0; i < ps.Length; i++)
			if (IsCarbonPreInitDelegate(ps[i].ParameterType))
				return i;

		return -1;
	}

	private static object NormalizeParameterDefault(ParameterInfo p)
	{
		if (!p.HasDefaultValue)
			return Missing.Value;

		var dv = p.DefaultValue;
		return ReferenceEquals(dv, Missing.Value) ? Type.Missing : dv;
	}

	private static bool TryBuildCarbonInitializeInvokeArgs(MethodInfo init, PluginInfo pluginInfo, object pkg,
		Delegate preInitDel, bool precompiled, out object?[] args, out int outRustPluginIndex)
	{
		args = [];
		outRustPluginIndex = -1;
		var ps = init.GetParameters();
		var clr = _carbonPackageClrType;
		if (clr == null || !TryResolveCarbonInitializeSlots(init, clr, out var ia, out var orust, out var ipkg,
			    out var preIdx, out var boolIdx))
			return false;

		args = new object?[ps.Length];
		for (var i = 0; i < ps.Length; i++)
		{
			if (ps[i].HasDefaultValue)
				args[i] = NormalizeParameterDefault(ps[i]);
		}

		args[ia] = ps[ia].ParameterType == typeof(Assembly)
			? pluginInfo.PluginType.Assembly
			: pluginInfo.PluginType;

		args[orust] = null;
		outRustPluginIndex = orust;

		args[ipkg] = pkg;

		args[preIdx] = preInitDel;
		args[boolIdx] = precompiled;

		for (var i = 0; i < ps.Length; i++)
		{
			if (i == orust)
				continue;
			if (args[i] != null || ReferenceEquals(args[i], Type.Missing))
				continue;
			args[i] = Type.Missing;
		}

		return true;
	}

	private static void TryRegisterCarbonModLoaderPackage(object boxedPackageStruct)
	{
		var reg = _carbonModLoaderRegisterPackage;
		if (reg == null)
			return;

		try
		{
			reg.Invoke(null, new[] { boxedPackageStruct });
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[DllLoader] ModLoader.RegisterPackage rejected ({0}).", ex.Message);
		}
	}

	private static object GetOrCreateCarbonPackageForDll(string dllPath) =>
		CarbonPackageByDllPath.GetOrAdd(dllPath, CreateCarbonPackageForDllPath);

	private static object CreateCarbonPackageForDllPath(string path)
	{
		var pname = Path.GetFileNameWithoutExtension(path);
		object boxed = _carbonPackageGetThreeArgs != null
			? _carbonPackageGetThreeArgs.Invoke(null, new object[] { pname, false, path })!
			: _carbonPackageGetTwoArgs!.Invoke(null, new object[] { pname, false })!;
		TryRegisterCarbonModLoaderPackage(boxed);
		return boxed;
	}

	private static bool HasOnFrameHook(Plugin plugin) =>
		plugin.GetType().GetMethod("OnFrame",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
			new[] { typeof(float) }, null) != null;

	private static RustPlugin? TryGetRustPluginFromManager(string pluginName)
	{
		try
		{
			var pm = Interface.Oxide.RootPluginManager;
			if (pm == null)
				return null;

			return GetPluginMgrReflect(pm).InvokeGet(pm, pluginName) as RustPlugin;
		}
		catch
		{
			return null;
		}
	}

	private static void PurgeResidualPluginInstances(string pluginName, string? typeName)
	{
		const int maxPasses = 12;
		var keyB = string.IsNullOrWhiteSpace(typeName) ? pluginName : typeName!;
		var keyA = pluginName;
		var candidates = BuildPluginKeyCandidates(keyA, keyB, 16);

		for (var i = 0; i < maxPasses; i++)
		{
			var anyFound = false;
			foreach (var candidate in candidates)
			{
				var live = TryGetRustPluginFromManager(candidate);
				if (live == null)
					continue;

				anyFound = true;

				// Try Carbon-native uninitialize first; if it fails, force-remove from plugin manager.
				var removedByCarbon = TryCarbonModLoaderUninitialize(live);
				if (!removedByCarbon)
					TryRemoveRustPluginFromManager(live.Name ?? candidate);

				TryRemoveRustPluginFromManager(candidate);
			}

			if (!anyFound)
				break;
		}

		// Informative warning if manager still returns a plugin after purge passes.
		RustPlugin? residue = null;
		foreach (var candidate in candidates)
		{
			residue = TryGetRustPluginFromManager(candidate);
			if (residue != null)
				break;
		}

		if (residue != null)
			Interface.Oxide.LogWarning(
				"[DllLoader] Residual plugin instance remains after purge: requested='{0}', type='{1}', runtime='{2}'.",
				keyA, keyB, residue.GetType().FullName);
	}

	private static string[] BuildPluginKeyCandidates(string keyA, string keyB, int maxSuffix)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			keyA,
			keyB,
		};

		for (var i = 1; i <= maxSuffix; i++)
		{
			set.Add($"{keyA} ({i})");
			set.Add($"{keyB} ({i})");
		}

		return set.ToArray();
	}

	private static bool TryCarbonModLoaderUninitialize(RustPlugin plugin)
	{
		var m = _carbonModLoaderUninitializePlugin;
		if (m == null)
			return false;

		try
		{
			var r = m.Invoke(null, new object[] { plugin, false, true });
			return r is bool b && b;
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[DllLoader] ModLoader.UninitializePlugin failed for {0}: {1}",
				plugin.Name ?? plugin.GetType().Name, ex.Message);
			return false;
		}
	}

	private static void TryRemoveRustPluginFromManager(string pluginName)
	{
		try
		{
			var pm = Interface.Oxide.RootPluginManager!;
			var reflect = GetPluginMgrReflect(pm);
			var existing = reflect.InvokeGet(pm, pluginName);

			var ok = existing is RustPlugin rust && reflect.TryInvokeRemovePlugin(pm, rust);

			if (!ok && reflect.RemoveByPluginNameOrId != null)
				reflect.RemoveByPluginNameOrId.Invoke(pm, new object[] { pluginName });

			if (reflect.PluginRemoveHook == null && reflect.RemoveByPluginNameOrId == null)
				Interface.Oxide.LogWarning(
					"DllLoader: RootPluginManager type {0} has no Remove/Unregister/remove-by-string API we could bind.",
					pm.GetType().FullName);
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("DllLoader QuietRemove skipped for {0}: {1}", pluginName, ex.Message);
		}
	}

	private static string JoinNames(string[] names) =>
		string.Join(", ", names);

	private const BindingFlags DeclaredInstance =
		BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static IEnumerable<MethodInfo> EnumerateInstanceMethodsHierarchy(Type owner)
	{
		for (var c = owner; c != null && c != typeof(object); c = c.BaseType)
			foreach (var m in c.GetMethods(DeclaredInstance))
				yield return m;
	}

	private static IEnumerable<PropertyInfo> EnumerateInstancePropertiesHierarchy(Type owner)
	{
		for (var c = owner; c != null && c != typeof(object); c = c.BaseType)
			foreach (var p in c.GetProperties(DeclaredInstance))
				yield return p;
	}

	private static HashSet<string> NameSet(params string[] names) =>
		new(names, StringComparer.Ordinal);

	private static MissingMethodException MissingPluginManagerInterop(Type mgr, string verb)
	{
		var preview = EnumerateInstanceMethodsHierarchy(mgr)
			.Where(m => m.Name.Contains("Plugin", StringComparison.OrdinalIgnoreCase))
			.Select(m =>
				$"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
			.Distinct()
			.Take(48)
			.ToArray();

		var detail =
			preview.Length > 0 ? string.Join(" ; ", preview) : "no *Plugin*-named instance methods on chain";

		return new MissingMethodException(
			$"{mgr.FullName}: DllLoader could not bind a {verb}. {detail}");
	}

	private static int DepthFromObject(Type? t)
	{
		var n = 0;
		while (t != null)
		{
			n++;
			t = t.BaseType;
		}
		return n;
	}

	private static readonly object PluginMgrReflectLock = new();
	private static Type? CachedPluginMgrType;
	private static PluginMgrReflectCached? CachedPluginMgrReflect;

	private static PluginMgrReflectCached GetPluginMgrReflect(object mgr)
	{
		var t = mgr.GetType();
		lock (PluginMgrReflectLock)
		{
			if (CachedPluginMgrType == t && CachedPluginMgrReflect != null)
				return CachedPluginMgrReflect;

			CachedPluginMgrReflect = PluginMgrReflectCached.Resolve(t);
			CachedPluginMgrType = t;
			return CachedPluginMgrReflect;
		}
	}

	private sealed class PluginMgrReflectCached
	{
		public readonly MethodInfo? GetByString;
		public readonly MethodInfo? PluginRemoveHook;
		public readonly MethodInfo? RemoveByPluginNameOrId;

		private PluginMgrReflectCached(MethodInfo? getByString, MethodInfo? pluginRemoveHook,
			MethodInfo? removeByPluginNameOrId)
		{
			GetByString = getByString;
			PluginRemoveHook = pluginRemoveHook;
			RemoveByPluginNameOrId = removeByPluginNameOrId;
		}

		public object? InvokeGet(object mgr, string pluginName)
		{
			if (GetByString == null)
				return null;
			try
			{
				return GetByString.Invoke(mgr, new object[] { pluginName });
			}
			catch
			{
				return null;
			}
		}

		public bool TryInvokeRemovePlugin(object mgr, RustPlugin plugin)
		{
			if (PluginRemoveHook == null)
				return false;
			var ret = PluginRemoveHook.Invoke(mgr, new object[] { plugin });
			return ret is not bool ok || ok;
		}

		public static PluginMgrReflectCached Resolve(Type managerType)
		{
			var rustBase = typeof(RustPlugin);

			var add = PickRegistrar(managerType, rustBase);

			var get =
				PickRetrieverByExplicitNames(managerType) ??
				FindStringIndexerGetter(managerType);

			var removeHook = PickRemovalByPlugin(managerType, rustBase);
			var removeName = PickRemovalBySingleString(managerType);

			if (add == null)
				throw MissingPluginManagerInterop(managerType, "registrar/add");

			return new PluginMgrReflectCached(get, removeHook, removeName);
		}
	}

	private static readonly string[] AddPluginMethodNames =
	[
		"AddPlugin",
		"RegisterPlugin",
		"AttachPlugin",
		"InsertPlugin",
		"AddRustPlugin",
		"RegisterRustPlugin",
		"EnqueuePlugin",
	];

	private static readonly string[] RetrievePluginNames =
	[
		"GetPlugin",
		"FindPlugin",
		"GetRustPlugin",
		"AcquirePlugin",
	];

	private static readonly string[] RemovePluginMethodNames =
	[
		"RemovePlugin",
		"UnregisterPlugin",
		"DetachPlugin",
		"UnloadPlugin",
		"Unload",
	];

	private static MethodInfo? PickRegistrar(Type managerType, Type rustConcreteBase)
	{
		Type[] roots = rustConcreteBase != typeof(Plugin)
			? [rustConcreteBase, typeof(Plugin)]
			: [rustConcreteBase];

		foreach (var root in roots)
		{
			var hit = TryPickRegistrarExplicit(managerType, root) ?? TryPickRegistrarHeuristic(managerType, root);
			if (hit != null)
				return hit;
		}

		return null;
	}

	private static MethodInfo? TryPickRegistrarExplicit(Type managerType, Type assignFromRoot)
	{
		var names = NameSet(AddPluginMethodNames);
		var tier = new List<MethodInfo>();
		foreach (var m in EnumerateInstanceMethodsHierarchy(managerType))
		{
			if (!names.Contains(m.Name) || m.ContainsGenericParameters || m.ReturnType.Name.Contains("IEnumerator"))
				continue;

			var ps = m.GetParameters();
			if (ps.Length != 1)
				continue;

			var pt = ps[0].ParameterType;
			if (pt == typeof(object))
				continue;

			if (!pt.IsAssignableFrom(assignFromRoot))
				continue;

			tier.Add(m);
		}

		return PickMostSpecific(tier);
	}

	private static MethodInfo? TryPickRegistrarHeuristic(Type managerType, Type assignFromRoot)
	{
		var tier = new List<MethodInfo>();
		foreach (var m in EnumerateInstanceMethodsHierarchy(managerType))
		{
			if (m.Name.IndexOf("Plugin", StringComparison.OrdinalIgnoreCase) < 0 ||
			    m.ContainsGenericParameters)
				continue;

			var n = m.Name;
			if (!(n.StartsWith("Add", StringComparison.OrdinalIgnoreCase) ||
			      n.StartsWith("Register", StringComparison.OrdinalIgnoreCase) ||
			      n.StartsWith("Attach", StringComparison.OrdinalIgnoreCase)))
				continue;

			var ps = m.GetParameters();
			if (ps.Length != 1)
				continue;

			var pt = ps[0].ParameterType;
			if (pt == typeof(object) || !pt.IsAssignableFrom(assignFromRoot))
				continue;

			tier.Add(m);
		}

		return PickMostSpecific(tier);
	}

	private static MethodInfo? PickRemovalByPlugin(Type managerType, Type rustConcreteBase)
	{
		Type[] roots = rustConcreteBase != typeof(Plugin)
			? [rustConcreteBase, typeof(Plugin)]
			: [rustConcreteBase];

		foreach (var root in roots)
		{
			var hit = TryPickRemovalExplicit(managerType, root);
			if (hit != null)
				return hit;
		}

		return null;
	}

	private static MethodInfo? TryPickRemovalExplicit(Type managerType, Type assignFromRoot)
	{
		var names = NameSet(RemovePluginMethodNames);
		var matches = new List<MethodInfo>();
		foreach (var m in EnumerateInstanceMethodsHierarchy(managerType))
		{
			if (!names.Contains(m.Name) || m.ContainsGenericParameters)
				continue;
			var ps = m.GetParameters();
			if (ps.Length != 1)
				continue;
			var pt = ps[0].ParameterType;

			if (pt == typeof(object) || !pt.IsAssignableFrom(assignFromRoot))
				continue;
			matches.Add(m);
		}

		return PickMostSpecific(matches);
	}

	private static MethodInfo? PickRemovalBySingleString(Type managerType)
	{
		var names = NameSet(["RemovePlugin", "UnregisterPlugin", "UnloadPlugin", "Unload"]);

		MethodInfo? best = null;
		var bestTier = int.MinValue;
		foreach (var m in EnumerateInstanceMethodsHierarchy(managerType))
		{
			if (!names.Contains(m.Name) || m.ContainsGenericParameters)
				continue;

			var ps = m.GetParameters();
			if (ps.Length != 1 || ps[0].ParameterType != typeof(string))
				continue;

			var tier = DepthFromObject(ps[0].ParameterType);
			if (tier > bestTier)
			{
				bestTier = tier;
				best = m;
			}
		}

		return best;
	}

	private static MethodInfo? PickRetrieverByExplicitNames(Type managerType)
	{
		var names = NameSet(RetrievePluginNames);

		MethodInfo? best = null;
		var tier = int.MinValue;
		foreach (var m in EnumerateInstanceMethodsHierarchy(managerType))
		{
			if (!names.Contains(m.Name) || m.ContainsGenericParameters)
				continue;

			var ps = m.GetParameters();
			if (ps.Length != 1 || ps[0].ParameterType != typeof(string))
				continue;

			if (m.ReturnType.Name.Contains("Coroutine", StringComparison.Ordinal))
				continue;
			var score = DepthFromObject(m.ReturnType);
			if (score > tier)
			{
				tier = score;
				best = m;
			}
		}

		return best;
	}

	private static MethodInfo? FindStringIndexerGetter(Type managerType)
	{
		foreach (var p in EnumerateInstancePropertiesHierarchy(managerType))
		{
			var idx = p.GetIndexParameters();
			if (idx.Length != 1 || idx[0].ParameterType != typeof(string))
				continue;

			var g = p.GetGetMethod(true);
			if (g?.GetParameters().Length == 1)
				return g;
		}

		return null;
	}

	private static MethodInfo? PickMostSpecific(List<MethodInfo> matches)
	{
		if (matches.Count == 0)
			return null;

		MethodInfo? best = null;
		var bestTier = int.MinValue;
		foreach (var m in matches)
		{
			var pt = m.GetParameters()[0].ParameterType;
			var tier = DepthFromObject(pt);

			if (tier > bestTier)
			{
				bestTier = tier;
				best = m;
			}
		}

		return best;
	}
}
