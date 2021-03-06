using Luna.ExternalExtensions;
using Luna.ExternalExtensions.Interfaces;
using Luna.Logging;
using Luna.Logging.Interfaces;
using System;
using System.IO;
using System.Runtime.InteropServices;
using static Luna.Logging.Enums;

namespace Luna.Sound {
	public class Sound : IExternal {
		private static readonly ILogger Logger = new Logger(typeof(Sound).Name);
		public static bool IsGloballyMuted = false;
		public static bool IsSoundAllowed => !IsGloballyMuted && Helpers.GetPlatform() == OSPlatform.Linux;

		public Sound(bool isMuted) {
			IsGloballyMuted = isMuted;
		}

		public static void PlayNotification(ENOTIFICATION_CONTEXT context = ENOTIFICATION_CONTEXT.NORMAL, bool redirectOutput = false) {			
			if (Helpers.GetPlatform() != OSPlatform.Linux) {
				Console.Beep();
				Logger.Log("Cannot proceed as the running operating system is unknown.", LogLevels.Trace);
				return;
			}

			if (IsGloballyMuted) {
				Logger.Trace("Notifications are muted globally.");
				return;
			}

			if (!Directory.Exists(Constants.ResourcesDirectory)) {
				Logger.Warning("Resources directory doesn't exist!");
				return;
			}

			switch (context) {
				case ENOTIFICATION_CONTEXT.NORMAL:
					break;
				case ENOTIFICATION_CONTEXT.ALERT:					
					if (!File.Exists(Constants.ALERT_SOUND_PATH)) {
						Logger.Warning("Alert sound file doesn't exist!");
						return;
					}

					if (redirectOutput)
						Helpers.InBackgroundThread(() => Logger.Info($"cd /home/pi/Desktop/HomeAssistant/Assistant.Core/ && play {Constants.ALERT_SOUND_PATH} -q".ExecuteBash(false)));						
					else
						Helpers.InBackgroundThread(() => $"cd /home/pi/Desktop/HomeAssistant/Assistant.Core/ && play {Constants.ALERT_SOUND_PATH} -q".ExecuteBash(false));
					break;
				case ENOTIFICATION_CONTEXT.ERROR:
					break;
			}
		}

		public void RegisterLoggerEvent(object eventHandler) => LoggerExtensions.RegisterLoggerEvent(eventHandler);

		public enum ENOTIFICATION_CONTEXT : byte {
			NORMAL,
			ERROR,
			ALERT
		}
	}
}
