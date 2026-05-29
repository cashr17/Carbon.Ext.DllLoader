#region

using System;
using System.IO;

#endregion

namespace Oxide.Ext.DllLoader.CarbonDllLoader;

public sealed class DllPluginDirectoryWatcher : IDisposable
{
	private readonly FileSystemWatcher _watcher;
	private readonly object _eventGateLock = new();
	private readonly System.Collections.Generic.Dictionary<string, int> _lastRaisedAtByPath =
		new(StringComparer.OrdinalIgnoreCase);
	private const int EventDebounceMs = 1200;

	public DllPluginDirectoryWatcher(string pluginDirectoryPath)
	{
		if (string.IsNullOrWhiteSpace(pluginDirectoryPath))
			throw new ArgumentException("Plugin directory path cannot be null or empty.", nameof(pluginDirectoryPath));
		_watcher = new FileSystemWatcher(pluginDirectoryPath, "*.dll")
		{
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
		};

		_watcher.Created += OnCreated;
		_watcher.Changed += OnChanged;
		_watcher.Deleted += OnDeleted;
		_watcher.Renamed += OnRenamed;
	}

	public event EventHandler<string>? PluginDllCreated;
	public event EventHandler<string>? PluginDllRemoved;
	public event EventHandler<string>? PluginDllChanged;

	public void Start() => _watcher.EnableRaisingEvents = true;

	public void StopAndDispose()
	{
		_watcher.EnableRaisingEvents = false;
		Dispose();
	}

	private void OnCreated(object sender, FileSystemEventArgs e)
	{
		if (!IsEligibleDll(e.FullPath))
			return;
		if (!ShouldRaiseNow(e.FullPath))
			return;
		PluginDllCreated?.Invoke(this, e.FullPath);
	}

	private void OnChanged(object sender, FileSystemEventArgs e)
	{
		if (!IsEligibleDll(e.FullPath))
			return;
		if (!ShouldRaiseNow(e.FullPath))
			return;
		PluginDllChanged?.Invoke(this, e.FullPath);
	}

	private void OnDeleted(object sender, FileSystemEventArgs e) =>
		PluginDllRemoved?.Invoke(this, e.FullPath);

	private void OnRenamed(object sender, RenamedEventArgs e)
	{
		PluginDllRemoved?.Invoke(this, e.OldFullPath);
		if (!IsEligibleDll(e.FullPath))
			return;
		if (!ShouldRaiseNow(e.FullPath))
			return;
		PluginDllCreated?.Invoke(this, e.FullPath);
	}

	private bool ShouldRaiseNow(string fullPath)
	{
		var now = Environment.TickCount;
		lock (_eventGateLock)
		{
			if (_lastRaisedAtByPath.TryGetValue(fullPath, out var last))
			{
				var elapsed = unchecked(now - last);
				if (elapsed >= 0 && elapsed < EventDebounceMs)
					return false;
			}

			if (_lastRaisedAtByPath.Count > 4096)
				_lastRaisedAtByPath.Clear();

			_lastRaisedAtByPath[fullPath] = now;
			return true;
		}
	}

	private static bool IsEligibleDll(string fullPath)
	{
		try
		{
			var fi = new FileInfo(fullPath);
			if ((fi.Attributes & FileAttributes.Hidden) != 0)
				return false;
			return fi.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	public void Dispose() => _watcher.Dispose();
}
