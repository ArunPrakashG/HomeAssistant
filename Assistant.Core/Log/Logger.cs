using Assistant.AssistantCore;
using Assistant.Extensions;
using Assistant.Modules.Interfaces.LoggerInterfaces;
using System;
using System.Runtime.CompilerServices;
using static Assistant.AssistantCore.Enums;

namespace Assistant.Log {

	public class Logger : ILoggerBase {
		private NLog.Logger? LogModule;

		public string LogIdentifier { get; set; } = string.Empty;

		public string ModuleIdentifier => nameof(Logger);

		public string ModuleAuthor => "Arun Prakash";

		public Version ModuleVersion => new Version("6.0.0.0");

		public Logger(string loggerIdentifier) => RegisterLogger(loggerIdentifier);

		private void RegisterLogger(string? logId) {
			if (string.IsNullOrEmpty(logId)) {
				throw new ArgumentNullException(nameof(logId));
			}

			LogModule = Logging.RegisterLogger(logId);
			LogIdentifier = logId;
		}

		private void LogGenericDebug(string? message, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(message)) {
				LogNullError(nameof(message));
				return;
			}

			LogModule?.Debug($"{previousMethodName}() {message}");
		}

		private void LogGenericError(string? message, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(message)) {
				LogNullError(nameof(message));
				return;
			}

			LogModule?.Error($"{previousMethodName}() {message}");

			if (Core.Config.PushBulletLogging && Core.PushbulletClient != null && Core.PushbulletClient.IsBroadcastServiceOnline) {
				Core.PushbulletClient.BroadcastMessage(new PushRequestContent() {
					PushTarget = PushEnums.PushTarget.All,
					PushTitle = $"{Core.AssistantName} [ERROR] LOG",
					PushType = PushEnums.PushType.Note,
					PushBody = $"{previousMethodName}() {message}"
				});
			}
		}

		private void LogGenericException(Exception? exception, [CallerMemberName] string? previousMethodName = null) {
			if (exception == null) {
				return;
			}

			LogModule?.Error($"{previousMethodName}() {exception.GetBaseException().Message}/{exception.GetBaseException().HResult}/{exception.GetBaseException().StackTrace}");

			if (Core.Config.PushBulletLogging && Core.PushbulletClient != null && Core.PushbulletClient.IsBroadcastServiceOnline) {
				Core.PushbulletClient.BroadcastMessage(new PushRequestContent() {
					PushTarget = PushEnums.PushTarget.All,
					PushTitle = $"{Core.AssistantName} [EXCEPTION] LOG",
					PushType = PushEnums.PushType.Note,
					PushBody = $"{previousMethodName}() {exception.GetBaseException().Message}/{exception.GetBaseException().HResult}/{exception.GetBaseException().StackTrace}"
				});
			}
		}

		private void LogGenericInfo(string? message, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(message)) {
				LogNullError(nameof(message));
				return;
			}

			LogModule?.Info($"{message}");
		}

		private void LogGenericTrace(string? message, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(message)) {
				LogNullError(nameof(message));
				return;
			}

			if (Core.Config.Debug) {
				LogGenericInfo($"{previousMethodName}() " + message, previousMethodName);
			}
			else {
				LogModule?.Trace($"{previousMethodName}() {message}");
			}
		}

		private void LogGenericWarning(string? message, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(message)) {
				LogNullError(nameof(message));
				return;
			}

			LogModule?.Warn($"{previousMethodName}() {message}");

			if (Core.Config.PushBulletLogging && Core.PushbulletClient != null && Core.PushbulletClient.IsBroadcastServiceOnline) {
				Core.PushbulletClient.BroadcastMessage(new PushRequestContent() {
					PushTarget = PushEnums.PushTarget.All,
					PushTitle = $"{Core.AssistantName} [WARNING] LOG",
					PushType = PushEnums.PushType.Note,
					PushBody = $"{previousMethodName}() {message}"
				});
			}
		}

		private void LogNullError(string? nullObjectName, [CallerMemberName] string? previousMethodName = null) {
			if (string.IsNullOrEmpty(nullObjectName)) {
				return;
			}

			LogGenericError($"{nullObjectName} | Object is null!", previousMethodName);
		}


		public void Log(Exception? e, LogLevels level = LogLevels.Error, [CallerMemberName] string? previousMethodName = null, [CallerLineNumber] int callermemberlineNo = 0, [CallerFilePath] string? calledFilePath = null) {
			if (e == null) {
				return;
			}

			switch (level) {
				case Enums.LogLevels.Error:
					if (Core.Config.Debug) {
						LogGenericError($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + $"{e.Message} | {e.StackTrace}", previousMethodName);

					}
					else {
						LogGenericError($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + $"{e.Message} | {e.TargetSite}", previousMethodName);
					}

					DiscordLogToChannel($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + $"{e.Message} | {e.StackTrace}");
					break;

				case Enums.LogLevels.Fatal:
					LogGenericException(e, previousMethodName);
					break;

				case Enums.LogLevels.DebugException:
					LogGenericError($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + $"{e.Message} | {e.StackTrace}", previousMethodName);
					DiscordLogToChannel($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + $"{e.Message} | {e.StackTrace}");
					break;

				default:
					goto case Enums.LogLevels.Error;
			}
		}


		public void Log(string? message, LogLevels level = LogLevels.Info, [CallerMemberName] string? previousMethodName = null, [CallerLineNumber] int callermemberlineNo = 0, [CallerFilePath] string? calledFilePath = null) {
			switch (level) {
				case Enums.LogLevels.Trace:
					LogGenericTrace($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] {message}", previousMethodName);
					break;

				case Enums.LogLevels.Debug:
					LogGenericDebug(message, previousMethodName);
					break;

				case Enums.LogLevels.Info:
					LogGenericInfo(message, previousMethodName);
					break;

				case Enums.LogLevels.Warn:
					LogGenericWarning($"[{Helpers.GetFileName(calledFilePath)} | {callermemberlineNo}] " + message, previousMethodName);

					if (!string.IsNullOrEmpty(message) || !string.IsNullOrWhiteSpace(message)) {
						DiscordLogToChannel($"{message}");
					}

					break;

				case Enums.LogLevels.Ascii:
					Console.ForegroundColor = ConsoleColor.Green;
					Console.WriteLine(message);
					Console.ResetColor();
					LogGenericTrace(message);
					break;

				case Enums.LogLevels.UserInput:
					Console.WriteLine(@">>> " + message);
					LogGenericTrace(message);
					break;

				case Enums.LogLevels.ServerResult:
					Console.ForegroundColor = ConsoleColor.Cyan;
					Console.WriteLine(@"> " + message);
					Console.ResetColor();
					LogGenericTrace(message);
					break;

				case Enums.LogLevels.Custom:
					Console.WriteLine(message);
					LogGenericTrace(message, previousMethodName);
					break;

				case Enums.LogLevels.Success:
					Console.ForegroundColor = ConsoleColor.DarkMagenta;
					LogGenericInfo(message, previousMethodName);
					Console.ResetColor();
					break;

				default:
					goto case Enums.LogLevels.Info;
			}
#pragma warning restore CS8604 // Possible null reference argument.
		}

		public void DiscordLogToChannel(string message) {
			if (Helpers.IsNullOrEmpty(message)) {
				return;
			}

			if (!Core.CoreInitiationCompleted || !Core.IsNetworkAvailable) {
				return;
			}

			Log("Logging to discord is currently turned off. [WIP]", LogLevels.Info);

			//if (Core.ModuleLoader != null && Core.ModuleLoader.Modules != null && Core.ModuleLoader.Modules.OfType<IDiscordClient>().Count() > 0) {
			//	foreach (IDiscordClient bot in Core.ModuleLoader.Modules.OfType<IDiscordClient>()) {
			//		if (bot.IsServerOnline && bot.BotConfig.EnableDiscordBot &&
			//			bot.Module.BotConfig.DiscordLogChannelID != 0 && bot.Module.BotConfig.DiscordLog) {
			//			Helpers.InBackgroundThread(async () => {
			//				await bot.Module.LogToChannel(message).ConfigureAwait(false);
			//			});
			//		}
			//	}
			//}
		}

		public void InitLogger(string logId) => RegisterLogger(logId);

		public void ShutdownLogger() => Logging.LoggerOnShutdown();
	}
}