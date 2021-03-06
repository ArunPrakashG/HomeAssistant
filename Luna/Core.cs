namespace Luna {
	using Figgle;
	using FluentScheduler;
	using JsonCommandLine;
	using Luna.CommandLine;
	using Luna.Gpio;
	using Luna.Gpio.Drivers;
	using Luna.Logging;
	using Luna.Modules;
	using Luna.Modules.Interfaces;
	using Luna.Server;
	using Luna.Shell;
	using Luna.Watchers;
	using Synergy.Extensions;
	using System;
	using System.Diagnostics;
	using System.IO;
	using System.Reflection;
	using System.Runtime.InteropServices;
	using System.Threading;
	using System.Threading.Tasks;
	using static Luna.Gpio.Enums;

	public class Core {
		private readonly InternalLogger Logger;
		private readonly CancellationTokenSource KeepAliveToken = new CancellationTokenSource();
		private readonly ConfigWatcher InternalConfigWatcher;
		private readonly ModuleWatcher InternalModuleWatcher;
		private readonly GpioCore Controller;
		private readonly ModuleLoader ModuleLoader;
		private readonly RestCore RestServer;
		private readonly PinsWrapper Pins;

		internal readonly bool DisableFirstChanceLogWithDebug;
		internal readonly CoreConfig Config;

		public readonly bool InitiationCompleted;
		public readonly bool IsBaseInitiationCompleted;
		public static bool IsNetworkAvailable => Helpers.IsNetworkAvailable();
		internal static readonly Stopwatch RuntimeSpanCounter;

		internal static bool IsMuted { get; private set; }
		internal static bool NoUpdates { get; private set; }

		public static bool IsUpdatesAllowed { get; private set; }

		static Core() {
			RuntimeSpanCounter = new Stopwatch();
			JobManager.Initialize();
		}

		internal Core(string[] args) {
			Console.Title = $"Initializing...";
			Logger = InternalLogger.GetOrCreateLogger<Core>(this, nameof(Core));
			OS.Init(true);
			RuntimeSpanCounter.Restart();
			File.WriteAllText("version.txt", Constants.Version?.ToString());

			ParseStartupArguments();

			if (File.Exists(Constants.TraceLogFile)) {
				File.Delete(Constants.TraceLogFile);
			}

			Config = new CoreConfig(this);
			Config.LoadAsync().Wait();
			IsUpdatesAllowed = !NoUpdates && Config.AutoUpdates;
			Config.LocalIP = Helpers.GetLocalIpAddress()?.ToString() ?? "-Invalid-";
			Config.PublicIP = Helpers.GetPublicIP()?.ToString() ?? "-Invalid-";

			if (!IsNetworkAvailable) {
				Logger.Warn("No Internet connection.");
				Logger.Info($"Starting offline mode...");
			}

			Pins = new PinsWrapper(
					Config.GpioConfiguration.OutputModePins,
					Config.GpioConfiguration.InputModePins,
					Constants.BcmGpioPins,
					Config.GpioConfiguration.RelayPins,
					Config.GpioConfiguration.InfraredSensorPins,
					Config.GpioConfiguration.SoundSensorPins
			);

			Controller = new GpioCore(Pins, this, Config.GpioConfiguration.GpioSafeMode);
			ModuleLoader = new ModuleLoader();
			RestServer = new RestCore(Config.RestServerPort, Config.Debug);

			JobManager.AddJob(() => SetConsoleTitle(), (s) => s.WithName("ConsoleUpdater").ToRunEvery(1).Seconds());
			Logger.CustomLog(FiggleFonts.Ogre.Render("LUNA"), ConsoleColor.Green);
			Logger.CustomLog($"---------------- Starting Luna v{Constants.Version} ----------------", ConsoleColor.Blue);
			IsBaseInitiationCompleted = true;
			PostInitiation().Wait();
			InternalConfigWatcher = new ConfigWatcher(this);
			InternalModuleWatcher = new ModuleWatcher(this);
			InitiationCompleted = true;
		}

		private void ParseStartupArguments() {
			using (CommandLineParser parser = new CommandLineParser(Environment.CommandLine)) {
				if (!parser.IsJsonType) {
					return;
				}

				Arguments arguments = parser.Parse();

				if (arguments.ArgumentsExist) {
					foreach (CommandLineArgument arg in arguments.ArgumentCollection) {
						if (string.IsNullOrEmpty(arg.BaseCommand)) {
							continue;
						}
						CancellationTokenSource waitToken;
						switch (arg.BaseCommand) {
							case "silent_start":
								IsMuted = true;
								break;
							case "no_update":
								NoUpdates = true;
								break;
							case "cold_start":
								CoreConfig.ColdStartup = true;
								break;

							// commands just for testing
							case "start_delayed" when arg.ParameterCount >= 1:
								foreach(var param in arg.Parameters) {
									switch (param.Key) {
										case "delay":
											waitToken = new CancellationTokenSource(TimeSpan.FromMinutes(double.Parse(param.Value)));
											Logger.Info($"Waiting for {param.Value} minute(s) before continuing with code execution...");
											while (waitToken.IsCancellationRequested) {
												Task.Delay(5).Wait();
											}

											Logger.Info("Continuing...");
											break;
										default:
											continue;
									}
								}
								break;
							case "start_delayed":
								waitToken = new CancellationTokenSource(TimeSpan.FromMinutes(1));
								Logger.Info($"Waiting for 1 minute before continuing with code execution...");

								while (waitToken.IsCancellationRequested) {
									Task.Delay(5).Wait();
								}

								Logger.Info("Continuing...");
								break;

							default:
								break;
						}
					}
				}
			}
		}

		private async Task PostInitiation() {
			async void moduleLoaderAction() => await ModuleLoader.LoadAsync(Config.EnableModules).ConfigureAwait(false);

			async void gpioControllerInitAction() {
				GpioControllerDriver? driver = default;

				switch (Config.GpioConfiguration.GpioDriverProvider) {
					case GpioDriver.RaspberryIODriver:
						driver = new RaspberryIODriver(new InternalLogger(nameof(RaspberryIODriver)), Pins, Controller.GetPinConfig(), Config.GpioConfiguration.PinNumberingScheme);
						break;
					case GpioDriver.SystemDevicesDriver:
						driver = new SystemDeviceDriver(new InternalLogger(nameof(SystemDeviceDriver)), Pins, Controller.GetPinConfig(), Config.GpioConfiguration.PinNumberingScheme);
						break;
					case GpioDriver.WiringPiDriver:
						driver = new WiringPiDriver(new InternalLogger(nameof(WiringPiDriver)), Pins, Controller.GetPinConfig(), Config.GpioConfiguration.PinNumberingScheme);
						break;
				}

				await Controller.InitController(driver, Config.GpioConfiguration.PinNumberingScheme).ConfigureAwait(false);
			}

			async void restServerInitAction() => await RestServer.InitServerAsync().ConfigureAwait(false);

			void endStartupAction() {
				ModuleLoader.ExecuteActionOnType<IEvent>((e) => e.OnStarted());
				ModuleLoader.ExecuteActionOnType<IAsyncEvent>(async (e) => await e.OnStarted().ConfigureAwait(false));
			}

			Parallel.Invoke(new ParallelOptions() { MaxDegreeOfParallelism = 10 },
				moduleLoaderAction,
				gpioControllerInitAction,
				restServerInitAction,
				endStartupAction
			);

			Interpreter.Pause();
			await Interpreter.InitInterpreterAsync().ConfigureAwait(false);
		}

		internal void OnExit() {
			Logger.Info("Shutting down...");

			Parallel.Invoke(
				new ParallelOptions() {
					MaxDegreeOfParallelism = 10
				},

				async () => await RestServer.ShutdownServer().ConfigureAwait(false),
				() => ModuleLoader.ExecuteActionOnType<IEvent>((e) => e.OnShutdownRequested()),
				() => ModuleLoader.ExecuteActionOnType<IAsyncEvent>(async (e) => await e.OnShutdownRequested().ConfigureAwait(false)),
				() => Interpreter.ExitShell(),
				() => RestServer.Dispose(),
				() => Controller.Dispose(),
				() => JobManager.RemoveAllJobs(),
				() => JobManager.Stop(),
				() => InternalConfigWatcher.StopWatcher(),
				() => InternalModuleWatcher.StopWatcher(),
				() => ModuleLoader?.OnCoreShutdown(),
				async () => await Config.SaveAsync().ConfigureAwait(false)
			);

			Logger.Trace("Finished exit tasks.");
		}

		internal void ExitEnvironment(int exitCode = 0) {
			if (exitCode != 0) {
				Logger.Warn("Exiting with nonzero error code...");
			}

			if (exitCode == 0) {
				OnExit();
			}

			InternalLogManager.LoggerOnShutdown();
			KeepAliveToken.Cancel();
			Environment.Exit(exitCode);
		}

		internal async Task Restart(int delay = 10) {
			CommandLineArgument restartArg = new CommandLineArgument("restart");
			restartArg.TryAddParameter("exePath", Assembly.GetExecutingAssembly().Location);

			string args = restartArg.BuildAsArgument();

			using(LunaExternalProcessSession exProcess = new LunaExternalProcessSession(OSPlatform.Linux)) {
				var result = exProcess.Run($"dotnet Luna.External.dll {args}", false);
			}

			Helpers.ScheduleTask(() => "cd /home/pi/Desktop/HomeAssistant/Helpers/Restarter && dotnet RestartHelper.dll".ExecuteBash(false), TimeSpan.FromSeconds(delay));
			await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
			ExitEnvironment(0);
		}

		internal async Task KeepAlive() {
			Logger.CustomLog($"Press {Constants.ShellKeyChar} for shell.", ConsoleColor.Green);
			while (!KeepAliveToken.Token.IsCancellationRequested) {
				try {
					if (Interpreter.PauseShell) {
						if (!Console.KeyAvailable) {
							continue;
						}

						ConsoleKeyInfo pressedKey = Console.ReadKey(true);

						switch (pressedKey.Key) {
							case Constants.ShellKey:
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
				Logger.Warn("The core config file has been deleted.");
				Logger.Warn("Fore quitting assistant.");
				ExitEnvironment(0);
			}

			Logger.Warn("Updating core config as the local config file as been updated...");
			Helpers.InBackgroundThread(async () => await Config.LoadAsync().ConfigureAwait(false));
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
			Logger.Trace($"An event has been raised on module folder for file > {fileName}");

			if (!File.Exists(filePath)) {
				ModuleLoader.UnloadFromPath(filePath);
				return;
			}

			Helpers.InBackground(async () => await ModuleLoader.LoadAsync(Config.EnableModules).ConfigureAwait(false));
		}

		private void SetConsoleTitle() {
			string text = $"Luna v{Constants.Version} | https://{Config.LocalIP}:{Config.RestServerPort}/ | {DateTime.Now.ToLongTimeString()} | Uptime : {Math.Round(RuntimeSpanCounter.Elapsed.TotalMinutes, 3)} minutes";			
			Helpers.SetConsoleTitle(text);
		}

		internal GpioCore GetGpioCore() => Controller;

		internal CoreConfig GetCoreConfig() => Config;

		internal ModuleLoader GetModuleInitializer() => ModuleLoader;

		internal WatcherBase GetFileWatcher() => InternalConfigWatcher;

		internal WatcherBase GetModuleWatcher() => InternalModuleWatcher;
	}
}
