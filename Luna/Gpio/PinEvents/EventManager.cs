using Luna.Gpio.Controllers;
using Luna.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Luna.Gpio.PinEvents {
	public class EventManager {
		private readonly InternalLogger Logger = new InternalLogger(nameof(EventManager));
		private readonly Dictionary<int, Generator> Events = new Dictionary<int, Generator>();
		private readonly GpioCore Core;

		internal EventManager(GpioCore _core) => Core = _core;

		internal async Task<bool> RegisterEvent(PinEventConfiguration config) {
			if (!PinController.IsValidPin(config.GpioPin)) {
				Logger.Warn("The specified pin is invalid.");
				return false;
			}

			if (Events.ContainsKey(config.GpioPin)) {
				return false;
			}

			Generator gen = new Generator(Core, config, Logger);

			while (!gen.Config.IsEventRegistered) {
				await Task.Delay(1).ConfigureAwait(false);
			}

			Events.Add(config.GpioPin, gen);
			return gen.Config.IsEventRegistered;
		}

		internal void StopAllEventGenerators() {
			foreach (KeyValuePair<int, Generator> pair in Events) {
				StopEventGeneratorForPin(pair.Key);
			}
		}

		internal void StopEventGeneratorForPin(int pin) {
			if (!PinController.IsValidPin(pin)) {
				return;
			}

			if (!Events.TryGetValue(pin, out Generator? generator) || generator == null) {
				return;
			}

			generator.OverrideEventPolling();
			Logger.Trace($"Stopped pin polling for '{pin}' pin");
		}
	}
}
