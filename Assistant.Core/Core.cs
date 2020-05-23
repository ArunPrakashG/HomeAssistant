namespace Assistant.Core {
	using Assistant.Core.Shell;
	using Assistant.Core.Update;
	using Assistant.Core.Watchers;
	using Assistant.Core.Watchers.Interfaces;
	using Assistant.Extensions;
	using Assistant.Extensions.Attributes;
	using Assistant.Gpio;
	using Assistant.Gpio.Drivers;
	using Assistant.Logging;
	using Assistant.Logging.Interfaces;
	using Assistant.Modules;
	using Assistant.Modules.Interfaces.EventInterfaces;
	using Assistant.Rest;
	using Assistant.Sound.Speech;
	using FluentScheduler;
	using RestSharp;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Net;
	using System.Runtime.InteropServices;
	using System.Threading;
	using System.Threading.Tasks;
	using Unosquare.RaspberryIO;
	using static Assistant.Gpio.Enums;
	using static Assistant.Logging.Enums;
	using static Assistant.Modules.ModuleInitializer;

	public class Core {
		private readonly ILogger Logger = new Logger(nameof(Core));
		private readonly IWatcher InternalFileWatcher;
		private readonly IWatcher InternalModuleWatcher;
		private readonly SemaphoreSlim NetworkSync = new SemaphoreSlim(1, 1);
		private readonly CancellationTokenSource KeepAliveToken = new CancellationTokenSource();
		private readonly GpioCore Controller;
		private readonly UpdateManager Updater;
		private readonly CoreConfig Config;
		private readonly ModuleInitializer ModuleLoader;
		private readonly RestCore RestServer;
		private readonly DateTime StartupTime;

		internal readonly bool IsBaseInitiationCompleted;
		internal readonly bool DisableFirstChanceLogWithDebug;
		internal readonly bool InitiationCompleted;

		public bool IsNetworkAvailable { get; internal set; }

		public string AssistantName {
			get => Config.AssistantDisplayName ?? "Home Assistant";
			internal set => Config.AssistantDisplayName = value ?? Config.AssistantDisplayName;
		}

		internal Core(string[] args) {
			OS.Init(true);
			Console.Title = $"Home Assistant Initializing...";
			StartupTime = DateTime.Now;
			File.WriteAllText("version.txt", Constants.Version?.ToString());

			if (File.Exists(Constants.TraceLogPath)) {
				File.Delete(Constants.TraceLogPath);
			}

			JobManager.Initialize(new Registry());

			Config = new CoreConfig(this);
			Config.Load();
			Config.ProgramLastStartup = StartupTime;

			Helpers.SetFileSeperator();
			IsNetworkAvailable = Helpers.IsNetworkAvailable();
			Constants.LocalIP = Helpers.GetLocalIpAddress() ?? "-Invalid-";
			Constants.ExternelIP = Helpers.GetExternalIp() ?? "-Invalid-";

			if (!IsNetworkAvailable) {
				Logger.Log("No Internet connection.", LogLevels.Warn);
				Logger.Log($"Starting {AssistantName} in offline mode...");
			}
			
			Controller = new GpioCore(new AvailablePins(
					Config.OutputModePins,
					Config.InputModePins,
					Constants.BcmGpioPins,
					Config.RelayPins,
					Config.IRSensorPins,
					Config.SoundSensorPins
					), true);
			Updater = new UpdateManager(this);
			ModuleLoader = new ModuleInitializer();
			RestServer = new RestCore();

			JobManager.AddJob(() => SetConsoleTitle(), (s) => s.WithName("ConsoleUpdater").ToRunEvery(1).Seconds());
			Helpers.ASCIIFromText(Config.AssistantDisplayName);
			Logger.WithColor($"X---------------- Starting {AssistantName} V{Constants.Version} ----------------X", ConsoleColor.Blue);
			IsBaseInitiationCompleted = true;
			PostInitiation().Wait();

			InternalFileWatcher = new GenericWatcher(this, "*.json", Constants.ConfigDirectory, false, null, new Dictionary<string, Action<string>>(3) {
				{ "Assistant.json", OnCoreConfigChangeEvent },
				{ "DiscordBot.json", OnDiscordConfigChangeEvent },
				{ "MailConfig.json", OnMailConfigChangeEvent }
			});

			InternalModuleWatcher = new GenericWatcher(this, "*.dll", Constants.ModuleDirectory, false, null, new Dictionary<string, Action<string>>(1) {
				{ "*", OnModuleDirectoryChangeEvent }
			});

			InitiationCompleted = true;
		}

		private async Task PostInitiation() {
			// TODO Init Assistant.Web

			await ModuleLoader.LoadAsync().ConfigureAwait(false);
			await Updater.CheckAndUpdateAsync(true).ConfigureAwait(false);

			IGpioControllerDriver? _driver = default;

			switch (Config.GpioDriverProvider) {
				case GpioDriver.RaspberryIODriver:
					_driver = new RaspberryIODriver();
					break;
				case GpioDriver.SystemDevicesDriver:
					_driver = new SystemDeviceDriver();
					break;
				case GpioDriver.WiringPiDriver:
					_driver = new WiringPiDriver();
					break;
			}

			await Controller.InitController(_driver, OS.IsUnix, Config.PinNumberingScheme).ConfigureAwait(false);

			Interpreter.Pause();
			await Interpreter.InitInterpreterAsync().ConfigureAwait(false);

			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.AssistantStartup, default);
			await TTS.AssistantVoice(TTS.ESPEECH_CONTEXT.AssistantStartup).ConfigureAwait(false);

			//Task moduleLoaderTask = new Task(async () => {
			//	if (!Config.EnableModules) {
			//		return;
			//	}

			//	await ModuleLoader.LoadAsync().ConfigureAwait(false);
			//});

			//Task gpioInitTask = new Task(async () => {
			//	IGpioControllerDriver? _driver = default;

			//	switch (Config.GpioDriverProvider) {
			//		case GpioDriver.RaspberryIODriver:
			//			_driver = new RaspberryIODriver();
			//			break;
			//		case GpioDriver.SystemDevicesDriver:
			//			_driver = new SystemDeviceDriver();
			//			break;
			//		case GpioDriver.WiringPiDriver:
			//			_driver = new WiringPiDriver();
			//			break;
			//	}

			//	await Controller.InitController(_driver, OS.IsUnix, Config.PinNumberingScheme).ConfigureAwait(false);
			//});

			//Task checkAndUpdateTask = new Task(async () => await Updater.CheckAndUpdateAsync(true).ConfigureAwait(false));

			//Task shellInitTask = new Task(async () => {
			//	Interpreter.Pause();
			//	await Interpreter.InitInterpreterAsync().ConfigureAwait(false);
			//});

			//Task endInitTask = new Task(async () => {
			//	ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.AssistantStartup, default);
			//	await TTS.AssistantVoice(TTS.ESPEECH_CONTEXT.AssistantStartup).ConfigureAwait(false);
			//});

			//Task.WaitAll(
			//	checkAndUpdateTask,
			//	moduleLoaderTask,
			//	gpioInitTask,
			//	shellInitTask,
			//	endInitTask
			//);
		}
		
		internal void OnNetworkDisconnected() {
			IsNetworkAvailable = false;
			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.NetworkDisconnected, default);
			Constants.ExternelIP = "Internet connection lost.";
		}
		
		internal void OnNetworkReconnected() {
			IsNetworkAvailable = true;
			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.NetworkReconnected, default);
			Constants.ExternelIP = Helpers.GetExternalIp();
		}

		internal async Task OnExit() {
			Logger.Log("Shutting down...");

			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.AssistantShutdown, default);

			Interpreter.ExitShell();
			await RestServer.Shutdown().ConfigureAwait(false);
			Controller?.Shutdown();
			JobManager.RemoveAllJobs();
			JobManager.Stop();
			InternalFileWatcher.StopWatcher();
			InternalModuleWatcher.StopWatcher();
			ModuleLoader?.OnCoreShutdown();
			Config.ProgramLastShutdown = DateTime.Now;
			Config.Save();

			Logger.Log("Finished exit tasks.", LogLevels.Trace);
		}

		internal async Task Exit(int exitCode = 0) {
			if (exitCode != 0) {
				Logger.Log("Exiting with nonzero error code...", LogLevels.Error);
			}

			if (exitCode == 0) {
				await OnExit().ConfigureAwait(false);
			}

			Logger.Log("Bye, have a good day sir!");
			NLog.NLog.LoggerOnShutdown();
			KeepAliveToken.Cancel();
			Environment.Exit(exitCode);
		}

		internal async Task Restart(int delay = 10) {
			Helpers.ScheduleTask(() => "cd /home/pi/Desktop/HomeAssistant/Helpers/Restarter && dotnet RestartHelper.dll".ExecuteBash(false), TimeSpan.FromSeconds(delay));
			await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
			await Exit(0).ConfigureAwait(false);
		}

		internal async Task SystemShutdown() {
			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.SystemShutdown, default);
			if (GpioCore.IsAllowedToExecute) {
				Logger.Log($"Assistant is running on raspberry pi.", LogLevels.Trace);
				Logger.Log("Shutting down pi...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				await Pi.ShutdownAsync().ConfigureAwait(false);
				return;
			}

			if (Helpers.GetPlatform() == OSPlatform.Windows) {
				Logger.Log($"Assistant is running on a windows system.", LogLevels.Trace);
				Logger.Log("Shutting down system...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/s /t 0") {
					CreateNoWindow = true,
					UseShellExecute = false
				};
				Process.Start(psi);
			}
		}

		internal async Task SystemRestart() {
			ExecuteAsyncEvent<IEvent>(MODULE_EXECUTION_CONTEXT.SystemRestart, default);
			if (GpioCore.IsAllowedToExecute) {
				Logger.Log($"Assistant is running on raspberry pi.", LogLevels.Trace);
				Logger.Log("Restarting pi...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				await Pi.RestartAsync().ConfigureAwait(false);
				return;
			}

			if (Helpers.GetPlatform() == OSPlatform.Windows) {
				Logger.Log($"Assistant is running on a windows system.", LogLevels.Trace);
				Logger.Log("Restarting system...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/r /t 0") {
					CreateNoWindow = true,
					UseShellExecute = false
				};
				Process.Start(psi);
			}
		}

		internal async Task KeepAlive() {
			Logger.Log($"Press {Constants.SHELL_KEY} for shell.", LogLevels.Green);
			while (!KeepAliveToken.Token.IsCancellationRequested) {
				try {
					if (Interpreter.PauseShell) {
						if (!Console.KeyAvailable) {
							continue;
						}

						ConsoleKeyInfo pressedKey = Console.ReadKey(true);

						switch (pressedKey.Key) {
							case Constants.SHELL_KEY:
								Interpreter.Resume();
								continue;

							default:
								continue;
						}
					}
				}
				finally {
					await Task.Delay(1).ConfigureAwait(false);
				}
			}
		}

		private void OnCoreConfigChangeEvent(string? fileName) {
			if (!File.Exists(Constants.CoreConfigPath)) {
				Logger.Log("The core config file has been deleted.", LogLevels.Warn);
				Logger.Log("Fore quitting assistant.", LogLevels.Warn);
				Task.Run(async () => await Exit(0).ConfigureAwait(false));
			}

			Logger.Log("Updating core config as the local config file as been updated...");
			Helpers.InBackgroundThread(Config.Load);
		}

		private void OnDiscordConfigChangeEvent(string? fileName) {
		}

		private void OnMailConfigChangeEvent(string? fileName) {
		}

		private void OnModuleDirectoryChangeEvent(string? absoluteFileName) {
			if (string.IsNullOrEmpty(absoluteFileName)) {
				return;
			}

			string fileName = Path.GetFileName(absoluteFileName);
			string filePath = Path.GetFullPath(absoluteFileName);
			Logger.Log($"An event has been raised on module folder for file > {fileName}", LogLevels.Trace);

			if (!File.Exists(filePath)) {
				ModuleLoader.UnloadFromPath(filePath);
				return;
			}

			Helpers.InBackground(async () => await ModuleLoader.LoadAsync().ConfigureAwait(false));
		}

		private void SetConsoleTitle() {
			string text = $"http://{Constants.LocalIP}:9090/ | {DateTime.Now.ToLongTimeString()} | ";
			text += GpioCore.IsAllowedToExecute ? $"Uptime : {Math.Round(Pi.Info.UptimeTimeSpan.TotalMinutes, 3)} minutes" : null;
			Helpers.SetConsoleTitle(text);
		}

		public GpioCore GetGpioCore() => Controller;

		public UpdateManager GetUpdater() => Updater;

		public CoreConfig GetCoreConfig() => Config;

		public ModuleInitializer GetModuleInitializer() => ModuleLoader;		

		public RestCore GetRestCore() => RestServer;

		internal IWatcher GetFileWatcher() => InternalFileWatcher;

		internal IWatcher GetModuleWatcher() => InternalModuleWatcher;

		/// <summary>
		/// The method sends the current working local ip to an central server which i personally use for such tasks and for authentication etc.
		/// You have to specify such a server manually else contact me personally for my server IP.
		/// We use this so that the mobile controller app of the assistant can connect to the assistant running on the connected local interface.
		/// </summary>
		[Obsolete]
		private void SendLocalIp() {
			Helpers.GetNetworkByHostName("raspberrypi");
			string? localIp = Helpers.GetLocalIpAddress();

			if (string.IsNullOrEmpty(localIp)) {
				return;
			}

			Constants.LocalIP = localIp;
			int maxTry = 3;

			for (int i = 0; i < maxTry; i++) {
				RestClient client = new RestClient($"http://{Config.StatisticsServerIP}/api/v1/assistant/ip?ip={Constants.LocalIP}");
				RestRequest request = new RestRequest(Method.POST);
				request.AddHeader("cache-control", "no-cache");
				IRestResponse response = client.Execute(request);

				if (response.StatusCode != HttpStatusCode.OK) {
					continue;
				}

				Logger.Log($"{Constants.LocalIP} IP request send!", LogLevels.Trace);
				break;
			}
		}
	}
}
