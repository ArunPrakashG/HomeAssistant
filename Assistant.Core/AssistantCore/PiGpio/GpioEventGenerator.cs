using Assistant.Extensions;
using Assistant.Log;
using System;
using System.Threading;
using System.Threading.Tasks;
using static Assistant.AssistantCore.Enums;

namespace Assistant.AssistantCore.PiGpio {
	public sealed class GpioEventGenerator {
		private PiController? PiController => Core.PiController;
		private GpioPinController? Controller => PiController?.GetPinController();
		private readonly Logger Logger;

		private GpioEventManager EventManager { get; set; }
		private bool OverrideEventWatcher { get; set; }
		public GpioPinEventConfig EventPinConfig { get; private set; } = new GpioPinEventConfig();
		public bool IsEventRegistered { get; private set; } = false;

		public (int?, Thread?) PollingThreadInfo { get; private set; }

		public (object sender, GpioPinValueChangedEventArgs e) _GpioPinValueChanged { get; private set; }

		public delegate void GpioPinValueChangedEventHandler(object sender, GpioPinValueChangedEventArgs e);

		public event GpioPinValueChangedEventHandler? GpioPinValueChanged;

		private (object sender, GpioPinValueChangedEventArgs e) _GpioPinValue {
			get => _GpioPinValueChanged;
			set {
				GpioPinValueChanged?.Invoke(value.sender, value.e);
				_GpioPinValueChanged = _GpioPinValue;
			}
		}

		public GpioEventGenerator(GpioEventManager manager) {
			EventManager = manager ?? throw new ArgumentNullException(nameof(manager), "The event manager class instance cannot be null!");
			Logger = EventManager.Logger;
		}

		public GpioEventGenerator InitEventGenerator() {
			if(PiController == null) {
				throw new InvalidOperationException("The pin controller is proably malfunctioning.");
			}

			if (!PiController.GetPinController().IsDriverProperlyInitialized) {
				throw new InvalidOperationException("The pin controller isn't properly initialized.");
			}

			return this;
		}

		public void OverridePinPolling() => OverrideEventWatcher = true;

		private void StartPolling() {
			if(PiController == null) {
				Logger.Log("PiController is null. Polling failed.", LogLevels.Warn);
				return;
			}

			if (Controller == null) {
				Logger.Log("Controller is null. Polling failed.", LogLevels.Warn);
				return;
			}

			if (IsEventRegistered) {
				Logger.Log("There already seems to have an event registered on this instance.", LogLevels.Warn);
				return;
			}

			if (EventPinConfig.PinMode == GpioPinMode.Alt01 || EventPinConfig.PinMode == GpioPinMode.Alt02) {
				Logger.Log("Currently only Output/Input polling is supported.", LogLevels.Warn);
				return;
			}

			if (!PiController.GetPinController().SetGpioValue(EventPinConfig.GpioPin, EventPinConfig.PinMode)) {
				throw new InvalidOperationException("Internal error occured. Check if the pin specified is correct.");
			}

			switch (EventPinConfig.PinMode) {
				case GpioPinMode.Input:
					break;
				case GpioPinMode.Output:
					if (!PiController.GetPinController().SetGpioValue(EventPinConfig.GpioPin, GpioPinState.Off)) {
						throw new InvalidOperationException("Internal error occured. Check if the pin specified is correct.");
					}
					break;
				case GpioPinMode.Alt02:
				case GpioPinMode.Alt01:
				default:
					throw new InvalidOperationException("Internal error. The pin mode seems to be invalid. (No modes other than Input/Output is currently supported)");
			}

			bool initialValue = Controller.GpioDigitalRead(EventPinConfig.GpioPin);
			GpioPinState initialPinState = initialValue ? GpioPinState.Off : GpioPinState.On;
			int physicalPinNumber = Controller.GpioPhysicalPinNumber(EventPinConfig.GpioPin);
			_GpioPinValueChanged = (this, new GpioPinValueChangedEventArgs(EventPinConfig.GpioPin, initialPinState, GpioPinState.Off, initialValue, true, EventPinConfig.PinMode, physicalPinNumber));

			Logger.Log($"Started input pin polling for {EventPinConfig.GpioPin}.", LogLevels.Trace);
			IsEventRegistered = true;

			GpioPinValueChangedEventArgs e;
			GpioPinState previousPinState = initialPinState;
			bool previousPinValue = initialValue;

			PollingThreadInfo = Helpers.InBackgroundThread(async () => {
				while (!OverrideEventWatcher) {
					bool currentPinValue = Controller.GpioDigitalRead(EventPinConfig.GpioPin);
					GpioPinState currentPinState = currentPinValue ? GpioPinState.Off : GpioPinState.On;

					switch (EventPinConfig.PinEventState) {
						case GpioPinEventStates.OFF when currentPinState == GpioPinState.Off && previousPinState != currentPinState:
							e = new GpioPinValueChangedEventArgs(EventPinConfig.GpioPin, currentPinState, previousPinState, currentPinValue, previousPinValue, EventPinConfig.PinMode, physicalPinNumber);
							_GpioPinValue = (this, e);
							break;

						case GpioPinEventStates.ON when currentPinState == GpioPinState.On && previousPinState != currentPinState:
							e = new GpioPinValueChangedEventArgs(EventPinConfig.GpioPin, currentPinState, previousPinState, currentPinValue, previousPinValue, EventPinConfig.PinMode, physicalPinNumber);
							_GpioPinValue = (this, e);
							break;

						case GpioPinEventStates.ALL when previousPinState != currentPinState:
							e = new GpioPinValueChangedEventArgs(EventPinConfig.GpioPin, currentPinState, previousPinState, currentPinValue, previousPinValue, EventPinConfig.PinMode, physicalPinNumber);
							_GpioPinValue = (this, e);
							break;
						case GpioPinEventStates.NONE:
							OverrideEventWatcher = true;
							Logger.Log($"Stopping event polling for pin -> {EventPinConfig.GpioPin} ...", LogLevels.Trace);
							break;
						default:
							break;
					}

					previousPinState = currentPinState;
					previousPinValue = currentPinValue;
					await Task.Delay(1).ConfigureAwait(false);
				}

				Logger.Log($"Polling for {EventPinConfig.GpioPin} has been stopped.", LogLevels.Trace);
			}, $"Polling thread {EventPinConfig.GpioPin}", true);
		}

		public bool StartPinPolling(GpioPinEventConfig config) {
			if (config == null) {
				return false;
			}

			if (PiController == null) {
				Logger.Log("PiController is null. Polling failed.", LogLevels.Warn);
				return false;
			}

			if (!PiController.IsControllerProperlyInitialized || !PiController.GetPinController().IsDriverProperlyInitialized) {
				return false;
			}

			if (!PiController.IsValidPin(config.GpioPin)) {
				Logger.Log("The specified pin is invalid.", LogLevels.Warn);
				return false;
			}

			EventPinConfig = config;
			StartPolling();
			return IsEventRegistered;
		}
	}
}