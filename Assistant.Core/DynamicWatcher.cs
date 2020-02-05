using Assistant.Extensions;
using Assistant.Modules.Interfaces;
using Assistant.Modules.Interfaces.LoggerInterfaces;
using System;
using System.IO;

namespace Assistant.Core {

	public class DynamicWatcher : IDynamicWatcher {
		public ILoggerBase Logger { get; set; }
		public string DirectoryToWatch { get; set; } = string.Empty;

		public int DelayBetweenReadsInSeconds { get; set; } = 2;
		public FileSystemWatcher? FileSystemWatcher { get; set; }
		private DateTime LastRead = DateTime.MinValue;

		public bool WatcherOnline { get; set; } = false;

		public bool IncludeSubdirectories { get; set; } = false;

		public (bool, DynamicWatcher?, FileSystemWatcher?) InitWatcherService() {
			Logger.Log("Starting dynamic watcher...", Enums.LogLevels.Trace);

			if (Helpers.IsNullOrEmpty(DirectoryToWatch) || DelayBetweenReadsInSeconds <= 0 || Logger == null) {
				return (false, this, FileSystemWatcher);
			}

			if (!Directory.Exists(DirectoryToWatch)) {
				return (false, this, FileSystemWatcher);
			}

			if (FileSystemWatcher == null) {
				return (false, this, FileSystemWatcher);
			}

			FileSystemWatcher.Created += OnFileCreated;
			FileSystemWatcher.Changed += OnFileChanged;
			FileSystemWatcher.Renamed += OnFileRenamed;
			FileSystemWatcher.Deleted += OnFileDeleted;
			FileSystemWatcher.IncludeSubdirectories = IncludeSubdirectories;
			FileSystemWatcher.EnableRaisingEvents = true;
			WatcherOnline = true;
			Logger.Log($"Dynamic watcher started sucessfully! ({DirectoryToWatch})");
			return (true, this, FileSystemWatcher);
		}

		public void StopWatcherServier() {
			if(FileSystemWatcher == null) {
				return;
			}

			FileSystemWatcher.EnableRaisingEvents = false;
			WatcherOnline = false;
		}

		public void OnFileDeleted(object sender, FileSystemEventArgs e) {
		}

		public void OnFileRenamed(object sender, RenamedEventArgs e) {
		}

		public void OnFileChanged(object sender, FileSystemEventArgs e) {
		}

		public void OnFileCreated(object sender, FileSystemEventArgs e) {
		}
	}
}