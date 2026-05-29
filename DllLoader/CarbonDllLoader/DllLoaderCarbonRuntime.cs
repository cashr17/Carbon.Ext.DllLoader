#region

using HarmonyLib;
using Oxide.Core;
using Oxide.Ext.DllLoader.Controller;
using Oxide.Ext.DllLoader.Mapper;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

#endregion

namespace Oxide.Ext.DllLoader.CarbonDllLoader;

public sealed class DllLoaderCarbonRuntime
{
	public const string HarmonyId = "Carbon.Ext.DllLoader";

	public static DllLoaderCarbonRuntime? Instance { get; private set; }

	private static readonly object SharedHarmonyLock = new();
	private static Harmony? _sharedHarmony;

	internal static Harmony GetOrCreateSharedHarmony()
	{
		lock (SharedHarmonyLock)
			return _sharedHarmony ??= new Harmony(HarmonyId);
	}

	private readonly DllLoaderMapper _mapper = new();
	private readonly DllPluginLoaderController _controller;
	private readonly Action _enqueueFrameTick;
	private volatile bool _runFrameTicks;
	private DllPluginDirectoryWatcher? _directoryWatcher;

	private DllLoaderCarbonRuntime()
	{
		_controller = new DllPluginLoaderController(_mapper);
		_enqueueFrameTick = RunFrameTickAndReschedule;
	}

	internal DllLoaderMapper Mapper => _mapper;
	internal DllPluginLoaderController Controller => _controller;

	public static void Install()
	{
		if (Instance != null)
			return;

		var inst = Instance = new DllLoaderCarbonRuntime();

		try
		{
			inst._mapper.OnModLoad();
			GetOrCreateSharedHarmony().PatchAll(typeof(DllLoaderCarbonEntry).Assembly);

			var pd = Interface.Oxide.PluginDirectory;
			inst._mapper.ScanAndRegisterAssemblies(pd);
			inst._runFrameTicks = true;
			ScheduleInitialDllPluginLoads(inst);

			var watcher = inst._directoryWatcher = new DllPluginDirectoryWatcher(pd);
			watcher.PluginDllCreated += (_, fullPath) => inst.OnDllCreatedOrChanged(fullPath);
			watcher.PluginDllRemoved += (_, fullPath) => inst.OnDllRemoved(fullPath);
			watcher.PluginDllChanged += (_, fullPath) => inst.OnDllChanged(fullPath);
			watcher.Start();

			var asm = typeof(DllLoaderCarbonRuntime).Assembly;
			Interface.Oxide.LogInfo(
				"[Carbon.Ext.DllLoader] Extension ready (reflection-based Filename/path). Loaded from: {0}",
				DescribeAssemblyLoadPath(asm));
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogException("[Carbon.Ext.DllLoader] Install failed.", ex);
			try
			{
				Tear(inst);
			}
			catch
			{
			}

			Instance = null;
			throw;
		}
	}

	public static void Uninstall()
	{
		if (Instance == null)
			return;

		try
		{
			Tear(Instance);
		}
		finally
		{
			Instance = null;
		}
	}

	private static void Tear(DllLoaderCarbonRuntime rt)
	{
		rt._runFrameTicks = false;

		try
		{
			if (rt._directoryWatcher != null)
			{
				rt._directoryWatcher.StopAndDispose();
				rt._directoryWatcher = null;
			}
		}
		catch
		{
		}

		try
		{
			GetOrCreateSharedHarmony().UnpatchAll(HarmonyId);
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[Carbon.Ext.DllLoader] Harmony unpatch skipped: {0}", ex.Message);
		}

		try
		{
			rt._controller.OnShutdown();
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogWarning("[Carbon.Ext.DllLoader] Controller shutdown: {0}", ex.Message);
		}
	}

	private static void ScheduleInitialDllPluginLoads(DllLoaderCarbonRuntime inst)
	{
		void Step()
		{
			if (!inst._runFrameTicks || !ReferenceEquals(Instance, inst))
				return;

			try
			{
				foreach (var pi in inst._mapper.GetAllPlugins())
					Interface.Oxide.LoadPlugin(pi.PluginName);
			}
			catch (Exception ex)
			{
				Interface.Oxide.LogException("[Carbon.Ext.DllLoader] Deferred DLL plugin load failed.", ex);
			}

			if (!inst._runFrameTicks || !ReferenceEquals(Instance, inst))
				return;

			try
			{
				inst._enqueueFrameTick();
			}
			catch (Exception ex)
			{
				Interface.Oxide.LogException("[Carbon.Ext.DllLoader] Deferred OnFrame schedule failed.", ex);
			}
		}

		Interface.Oxide.NextTick(Step);
	}

	private void OnDllCreatedOrChanged(string dllFullPath)
	{
#if DEBUG
		Interface.Oxide.LogDebug("Assembly file event: {0}.", dllFullPath);
#endif
		_mapper.ScanAndRegisterAssemblies(Interface.Oxide.PluginDirectory);

		var asmNameNoExt = Path.GetFileNameWithoutExtension(dllFullPath);

		const int registerResolveRetries = 25;

		void TryResolveAndLoad(int resolveAttempt)
		{
			var refreshed = _mapper.GetAssemblyInfoByFilename(dllFullPath)
			                ?? _mapper.GetAssemblyInfoByFilename(asmNameNoExt);

			if (refreshed != null)
			{
				foreach (var pluginName in refreshed.PluginsName)
					Interface.Oxide.LoadPlugin(pluginName);

				return;
			}

			if (resolveAttempt < registerResolveRetries)
			{
				_mapper.ScanAndRegisterAssemblies(Interface.Oxide.PluginDirectory);
				Interface.Oxide.NextTick(() => TryResolveAndLoad(resolveAttempt + 1));
				return;
			}

			Interface.Oxide.LogWarning(
				"[Carbon.Ext.DllLoader] File '{0}' was not registered — still copying, not a DLL plugin assembly, hidden, or read error. Check preceding DllLoader warnings.",
				Path.GetFileName(dllFullPath));
		}

		Interface.Oxide.NextTick(() => TryResolveAndLoad(0));
	}

	private void OnDllChanged(string dllFullPath)
	{
#if DEBUG
		Interface.Oxide.LogDebug("Assembly({0}) file changed!", dllFullPath);
#endif
		OnDllRemoved(dllFullPath);
		OnDllCreatedOrChanged(dllFullPath);
	}

	private void OnDllRemoved(string dllFullPath)
	{
#if DEBUG
		Interface.Oxide.LogDebug("Assembly removed: {0}.", dllFullPath);
#endif
		var assemblyInfo = _mapper.GetAssemblyInfoByFilename(dllFullPath)
		                    ?? _mapper.GetAssemblyInfoByFilename(Path.GetFileNameWithoutExtension(dllFullPath));
		if (assemblyInfo == null)
		{
			Interface.Oxide.LogWarning("[Carbon.Ext.DllLoader] Unload skipped: '{0}' is not tracked (already removed?).",
				Path.GetFileName(dllFullPath));
			return;
		}

		foreach (var pluginName in assemblyInfo.PluginsName)
			Interface.Oxide.UnloadPlugin(pluginName);

		DllPluginLoaderController.InvalidateCarbonPackageCache(assemblyInfo.AssemblyFile);

		_mapper.RemoveAssembly(assemblyInfo);
	}

	private void OnFrame(float delta)
	{
		foreach (var plugin in _controller.OnFramePlugins)
			plugin.CallHook("OnFrame", delta);
	}

	private static string DescribeAssemblyLoadPath(Assembly asm)
	{
		try
		{
			if (!string.IsNullOrEmpty(asm.Location))
				return asm.Location;

			var fq = asm.ManifestModule?.FullyQualifiedName;
			if (fq != null && fq.Length > 0 && !fq.StartsWith("<", StringComparison.Ordinal))
				return fq;

			return $"in-memory / shadow-copied; module={asm.ManifestModule?.Name ?? "?"}; {asm.FullName}";
		}
		catch
		{
			return "(unknown)";
		}
	}

	private void RunFrameTickAndReschedule()
	{
		if (!_runFrameTicks || !ReferenceEquals(Instance, this))
			return;

		try
		{
			OnFrame(Time.deltaTime);
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogException("[Carbon.Ext.DllLoader] OnFrame forwarded tick failed.", ex);
		}

		if (!_runFrameTicks || !ReferenceEquals(Instance, this))
			return;

		try
		{
			Interface.Oxide.NextTick(_enqueueFrameTick);
		}
		catch (Exception ex)
		{
			Interface.Oxide.LogException("[Carbon.Ext.DllLoader] Failed scheduling next OnFrame tick.", ex);
		}
	}
}
