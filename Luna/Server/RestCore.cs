using Luna.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Web;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Luna.Server {
	internal class RestCore : IDisposable {
		private readonly InternalLogger Logger = new InternalLogger(nameof(RestCore));
		private readonly CancellationTokenSource ShutdownTokenSource = new CancellationTokenSource();
		private readonly Mutex ServerInstanceMutex;
		private readonly object LockObject = new object();
		private readonly int Port;
		private readonly IHost ServerHost;
		private readonly bool IsMutexLocked;
		private readonly bool IsDebuggingMode;
		private readonly string WebrootDirectory;
		private readonly string ContentRootDirectory;

		internal RestCore(int _port, bool _isDebugMode = false) {
			lock (LockObject) {
				string _mutexName = $"RestServerInstance-{_port}";
				ServerInstanceMutex = new Mutex(false, _mutexName);
				Logger.Info("Locking into server Mutex...");

				bool mutexAcquired = false;

				try {
					mutexAcquired = ServerInstanceMutex.WaitOne(60000);
				}
				catch (AbandonedMutexException) {
					mutexAcquired = true;
				}

				if (!mutexAcquired) {
					Logger.Error("Failed to acquire server lock.");
					Logger.Error($"This indicates another instance of the server is running on the same port '{_port}' !");
					Logger.Error("Please close that instance in order to initiate REST HTTP server!");
					IsMutexLocked = false;
					return;
				}

				ServerInstanceMutex.WaitOne();
				IsMutexLocked = true;
			}

			Port = _port;
			IsDebuggingMode = _isDebugMode;
			string currentDirectory = Directory.GetCurrentDirectory();
			WebrootDirectory = Path.Combine(currentDirectory, "Server", "wwwroot");
			ContentRootDirectory = Path.Combine(currentDirectory, "Server");
			HostBuilder _host = GenerateHostBuilder() ?? throw new InvalidOperationException(nameof(GenerateHostBuilder) + " host creation failed.");
			ServerHost = _host.Build();
		}

		internal async Task InitServerAsync() {
			if (ServerHost == null || ServerInstanceMutex == null) {
				throw new InvalidOperationException(nameof(ServerHost) + " is not assigned or is null!");
			}

			await ServerHost.StartAsync(ShutdownTokenSource.Token).ConfigureAwait(false);
			Logger.Info($"Server started at '{Port}' port.");
		}

		internal async Task ShutdownServer() {
			if (ServerHost == null) {
				return;
			}

			if (ShutdownTokenSource != null && !ShutdownTokenSource.IsCancellationRequested) {
				ShutdownTokenSource.Cancel();
			}

			await ServerHost.StopAsync().ConfigureAwait(false);

			if (ServerInstanceMutex != null) {
				lock (LockObject) {
					ServerInstanceMutex.ReleaseMutex();
				}
			}

			Logger.Info($"Server running at '{Port}' has been shutdown.");
		}

		public void Dispose() {
			if (ShutdownTokenSource != null && !ShutdownTokenSource.IsCancellationRequested) {
				ShutdownTokenSource.Cancel();
			}

			ShutdownTokenSource?.Dispose();
			ServerHost?.Dispose();

			if (ServerInstanceMutex != null) {
				lock (LockObject) {
					ServerInstanceMutex.ReleaseMutex();
				}

				ServerInstanceMutex.Dispose();
			}
		}

		private HostBuilder GenerateHostBuilder() {
			HostBuilder builder = new HostBuilder();
			builder.UseContentRoot(ContentRootDirectory);
			builder.ConfigureLogging(logging => logging.SetMinimumLevel(IsDebuggingMode ? LogLevel.Trace : LogLevel.Warning));
			//builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddConsole());
			builder.UseNLog();
			builder.ConfigureWebHostDefaults(
				webBuilder => {
					webBuilder.UseWebRoot(WebrootDirectory);
					webBuilder.UseUrls($"http://*:{Port}", $"https://*:{Port + 1}");
					webBuilder.UseKestrel(k => {
						k.AddServerHeader = true;
					});
					webBuilder.UseStartup<Init>();
					webBuilder.UseIIS();
					webBuilder.UseIISIntegration();
				}
			);

			return builder;
		}
	}
}
