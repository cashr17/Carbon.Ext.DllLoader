#region

using System;
using System.Reflection;
using HarmonyLib;
using Oxide.Core;

#endregion

namespace Oxide.Ext.DllLoader.CarbonDllLoader;

[HarmonyPatch(typeof(OxideMod), nameof(OxideMod.LoadPlugin))]
internal static class Harmony_Carbon_LoadPlugin_DllIntercept
{
	private static bool Prefix(OxideMod __instance, ref bool __result, string name)
	{
		var inst = DllLoaderCarbonRuntime.Instance;
		if (inst != null && inst.Mapper.TryGetTrackedDllPluginClassName(name, out var cls) && cls != null)
		{
			__result = inst.Controller.LoadDllPluginRequest(cls);
			return false;
		}

		return true;
	}
}

[HarmonyPatch(typeof(OxideMod), nameof(OxideMod.UnloadPlugin))]
internal static class Harmony_Carbon_UnloadPlugin_DllIntercept
{
	private static bool Prefix(OxideMod __instance, ref bool __result, string name)
	{
		var inst = DllLoaderCarbonRuntime.Instance;
		if (inst != null && inst.Mapper.TryGetTrackedDllPluginClassName(name, out var cls) && cls != null)
		{
			__result = inst.Controller.UnloadDllPluginRequest(cls);
			return false;
		}

		return true;
	}
}

[HarmonyPatch(typeof(OxideMod), nameof(OxideMod.ReloadPlugin))]
internal static class Harmony_Carbon_ReloadPlugin_DllIntercept
{
	private static bool Prefix(OxideMod __instance, ref bool __result, string name)
	{
		var inst = DllLoaderCarbonRuntime.Instance;
		if (inst != null && inst.Mapper.TryGetTrackedDllPluginClassName(name, out var cls) && cls != null)
		{
			__result = inst.Controller.ReloadDllPluginRequest(cls);
			return false;
		}

		return true;
	}
}

[HarmonyPatch]
internal static class Harmony_Carbon_ProcessCommands_DllSkip
{
	private static MethodBase? TargetMethod()
	{
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? modLoaderType;
			try
			{
				modLoaderType = asm.GetType("Carbon.Core.ModLoader", throwOnError: false, ignoreCase: false);
			}
			catch
			{
				continue;
			}

			if (modLoaderType == null)
				continue;

			foreach (var m in modLoaderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
			{
				if (!string.Equals(m.Name, "ProcessCommands", StringComparison.Ordinal))
					continue;

				var ps = m.GetParameters();
				if (ps.Length == 5 && ps[0].ParameterType == typeof(Type))
					return m;
			}
		}

		return null;
	}

	private static bool Prefix(Type type)
	{
		var inst = DllLoaderCarbonRuntime.Instance;
		if (inst == null || type == null)
			return true;

		return inst.Mapper.GetAssemblyInfoByPlugin(type.Name) == null;
	}
}
