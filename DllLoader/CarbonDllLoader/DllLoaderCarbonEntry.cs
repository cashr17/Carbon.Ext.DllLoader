#region

using System;
using API.Assembly;

#endregion

namespace Oxide.Ext.DllLoader.CarbonDllLoader;

public sealed class DllLoaderCarbonEntry : ICarbonExtension
{
	public void Awake(EventArgs _)
	{
	}

	public void OnLoaded(EventArgs _) => DllLoaderCarbonRuntime.Install();

	public void OnUnloaded(EventArgs _) => DllLoaderCarbonRuntime.Uninstall();
}
