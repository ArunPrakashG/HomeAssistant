
//    _  _  ___  __  __ ___     _   ___ ___ ___ ___ _____ _   _  _ _____
//   | || |/ _ \|  \/  | __|   /_\ / __/ __|_ _/ __|_   _/_\ | \| |_   _|
//   | __ | (_) | |\/| | _|   / _ \\__ \__ \| |\__ \ | |/ _ \| .` | | |
//   |_||_|\___/|_|  |_|___| /_/ \_\___/___/___|___/ |_/_/ \_\_|\_| |_|
//

//MIT License

//Copyright(c) 2019 Arun Prakash
//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.

using Assistant.Extensions;
using Assistant.Log;
using Google.Cloud.Speech.V1;
using RestSharp;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assistant.AssistantCore {

	public class TTSService {
		private static readonly Logger Logger = new Logger("GOOGLE-SPEECH");
		private static readonly SemaphoreSlim SpeechSemaphore = new SemaphoreSlim(1, 1);
		private static readonly SemaphoreSlim SpeechDownloadSemaphore = new SemaphoreSlim(1, 1);

		public TTSService() {
		}

		private static void SpeechToTextFromFile(string filePath) {
			SpeechClient speech = SpeechClient.Create();

			RecognizeResponse response = speech.Recognize(new RecognitionConfig() {
				Encoding = RecognitionConfig.Types.AudioEncoding.Flac,
				SampleRateHertz = 16000,
				LanguageCode = LanguageCodes.Malayalam.India
			}, RecognitionAudio.FromFile(filePath));

			foreach (SpeechRecognitionResult result in response.Results) {
				foreach (SpeechRecognitionAlternative alternative in result.Alternatives) {
					Logger.Log(alternative.Transcript, Enums.LogLevels.Info);
				}
			}
		}

		public static async Task<bool> SpeakText(this string text, bool enableAlert = false) {
			if (Core.Config.MuteAssistant || !Helpers.IsRaspberryEnvironment()) {
				return false;
			}

			if (Helpers.IsNullOrEmpty(text)) {
				Logger.Log("The text is empty or null!", Enums.LogLevels.Error);
				return false;
			}

			await SpeechSemaphore.WaitAsync().ConfigureAwait(false);

			if (!Directory.Exists(Constants.TextToSpeechDirectory)) {
				Directory.CreateDirectory(Constants.TextToSpeechDirectory);
			}

			if (File.Exists(Constants.TTSAlertFilePath) && enableAlert) {
				string executeResult = $"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.ResourcesDirectory} && play {Constants.TTSAlertFileName} -q".ExecuteBash();
				Logger.Log(executeResult, Enums.LogLevels.Trace);
				await Task.Delay(1000).ConfigureAwait(false);
			}

			string fileName = GetSpeechFile(text);

			if (Helpers.IsNullOrEmpty(fileName)) {
				SpeechSemaphore.Release();
				return false;
			}

			string playingResult = $"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {fileName} -q".ExecuteBash();
			Logger.Log(playingResult, Enums.LogLevels.Trace);
			await Task.Delay(500).ConfigureAwait(false);
			SpeechSemaphore.Release();			
			return true;
		}

		public static async Task AssistantVoice(Enums.SpeechContext context) {
			if (Core.Config.MuteAssistant || !Helpers.IsRaspberryEnvironment()) {
				return;
			}

			string playingResult;
			switch (context) {
				case Enums.SpeechContext.AssistantStartup:
					if (!File.Exists(Constants.StartupSpeechFilePath) && Core.CoreInitiationCompleted) {
						string textToSpeak = $"Hello sir! Your assistant is up and running!";
						await SpeakText(textToSpeak).ConfigureAwait(false);
						break;
					}

					playingResult = $"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {Constants.StartupFileName} -q".ExecuteBash();
					Logger.Log(playingResult, Enums.LogLevels.Trace);
					break;
				case Enums.SpeechContext.AssistantShutdown:
					if (!File.Exists(Constants.ShutdownSpeechFilePath) && Core.CoreInitiationCompleted) {
						string textToSpeak = $"Sir, your assistant shutting down! Have a nice day!";
						await SpeakText(textToSpeak).ConfigureAwait(false);
						break;
					}

					playingResult = $"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {Constants.ShutdownFileName} -q".ExecuteBash();
					Logger.Log(playingResult, Enums.LogLevels.Trace);
					break;
				case Enums.SpeechContext.NewEmaiNotification:
					if (!File.Exists(Constants.NewMailSpeechFilePath) && Core.CoreInitiationCompleted) {
						string textToSpeak = $"Sir, you recevied a new email!";
						await SpeakText(textToSpeak).ConfigureAwait(false);
						break;
					}

					playingResult = $"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {Constants.NewMailFileName} -q".ExecuteBash();
					Logger.Log(playingResult, Enums.LogLevels.Trace);
					break;
				default:
					break;
			}
		}

		private static void SpeakText(string text, Enums.SpeechContext context, bool disableTTSalert = true) {
			if (Core.Config.MuteAssistant) {
				return;
			}

			if (Core.IsUnknownOs) {
				Logger.Log("TTS service disabled as we are running on unknown OS.", Enums.LogLevels.Warn);
				return;
			}

			if (!Core.IsNetworkAvailable) {
				Logger.Log("Network is unavailable. TTS won't run.", Enums.LogLevels.Warn);
				return;
			}

			if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text)) {
				Logger.Log("Text is null! line 33, TTSService.cs", Enums.LogLevels.Error);
				return;
			}

			if (!Directory.Exists(Constants.TextToSpeechDirectory)) {
				Directory.CreateDirectory(Constants.TextToSpeechDirectory);
			}

			if (File.Exists(Constants.TTSAlertFilePath) && !disableTTSalert) {
				Helpers.ExecuteCommand($"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.ResourcesDirectory} && play {Constants.TTSAlertFileName} -q", false);
			}

			byte[] result;
			switch (context) {
				case Enums.SpeechContext.AssistantStartup:
					if (!File.Exists(Constants.StartupSpeechFilePath)) {
						Logger.Log($"{Core.AssistantName} startup tts sound doesn't exist, downloading the sound...", Enums.LogLevels.Trace);

						result = Helpers.GetUrlToBytes($"http://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&q={text}&tl=En-us", Method.GET, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/74.0.3729.169 Safari/537.36");
						Logger.Log("Fetched voice file bytes.", Enums.LogLevels.Trace);

						if (result.Length <= 0 || result == null) {
							Logger.Log("result returned as null!", Enums.LogLevels.Error);
							return;
						}

						Logger.Log($"Writting to file => {Constants.StartupSpeechFilePath}", Enums.LogLevels.Trace);
						Helpers.WriteBytesToFile(result, Constants.StartupSpeechFilePath);
					}

					if (File.Exists(Constants.StartupSpeechFilePath)) {
						Task.Delay(500).Wait();
						Helpers.ExecuteCommand($"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {Constants.StartupFileName} -q", false);
					}
					else {
						Logger.Log("An error occured, either download failed, or the file doesn't exist!", Enums.LogLevels.Error);
						return;
					}
					break;

				case Enums.SpeechContext.NewEmaiNotification:
					if (!File.Exists(Constants.NewMailSpeechFilePath)) {
						Logger.Log($"{Core.AssistantName} startup tts sound doesn't exist, downloading the sound...", Enums.LogLevels.Trace);

						result = Helpers.GetUrlToBytes($"http://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&q={text}&tl=En-us", Method.GET, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/74.0.3729.169 Safari/537.36");
						Logger.Log("Fetched voice file bytes.", Enums.LogLevels.Trace);

						if (result.Length <= 0 || result == null) {
							Logger.Log("result returned as null!", Enums.LogLevels.Error);
							return;
						}

						Logger.Log($"Writting to file => {Constants.NewMailSpeechFilePath}", Enums.LogLevels.Trace);
						Helpers.WriteBytesToFile(result, Constants.NewMailSpeechFilePath);
					}

					if (File.Exists(Constants.NewMailSpeechFilePath)) {
						Helpers.ExecuteCommand($"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {Constants.NewMailFileName} -q", false);
					}
					else {
						Logger.Log("An error occured, either download failed, or the file doesn't exist!", Enums.LogLevels.Error);
						return;
					}
					break;

				case Enums.SpeechContext.Custom:
					result = Helpers.GetUrlToBytes($"http://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&q={text}&tl=En-us", Method.GET, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/74.0.3729.169 Safari/537.36");
					Logger.Log("Fetched voice file bytes.", Enums.LogLevels.Trace);

					if (result.Length <= 0 || result == null) {
						Logger.Log("result returned as null!", Enums.LogLevels.Error);
						return;
					}

					string fileName = $"{DateTime.Now.Ticks}.mp3";

					Logger.Log($"Writting to file => {fileName}", Enums.LogLevels.Trace);
					Helpers.WriteBytesToFile(result, Constants.TextToSpeechDirectory + "/" + fileName);
					Task.Delay(200).Wait();
					if (File.Exists(Constants.TextToSpeechDirectory + "/" + fileName)) {
						Helpers.ExecuteCommand($"cd /home/pi/Desktop/HomeAssistant/AssistantCore/{Constants.TextToSpeechDirectory} && play {fileName} -q", false);
						Logger.Log($"Played the file {fileName} sucessfully", Enums.LogLevels.Trace);
					}
					else {
						Logger.Log("An error occured, either download failed, or the file doesn't exist!", Enums.LogLevels.Error);
						return;
					}
					break;
			}
		}

		private static string GetSpeechFile(string text, string lang = "En-us", string encoding = "UTF-8") {
			if (Helpers.IsNullOrEmpty(text)) {
				return null;
			}

			if (!Core.IsNetworkAvailable) {
				return null;
			}

			SpeechDownloadSemaphore.Wait();
			RestClient client = new RestClient($"http://translate.google.com/translate_tts?ie={encoding}&client=tw-ob&q={text}&tl={lang}");
			RestRequest request = new RestRequest(Method.GET);
			client.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/74.0.3729.169 Safari/537.36";
			request.AddHeader("cache-control", "no-cache");
			byte[] result;
			IRestResponse response = client.Execute(request);

			if (response.StatusCode != HttpStatusCode.OK) {
				Logger.Log("Failed to download. Status Code: " + response.StatusCode + "/" + response.ResponseStatus);
				SpeechDownloadSemaphore.Release();
				return null;
			}

			result = response.RawBytes;

			if (result.Length <= 0 || result == null) {
				Logger.Log("result returned as null!", Enums.LogLevels.Error);
				SpeechDownloadSemaphore.Release();
				return null;
			}

			string fileName = $"{DateTime.Now.Ticks}.mp3";

			Helpers.WriteBytesToFile(result, Constants.TextToSpeechDirectory + "/" + fileName);
			if (!File.Exists(Constants.TextToSpeechDirectory + "/" + fileName)) {
				Logger.Log("An error occured.", Enums.LogLevels.Warn);
				SpeechDownloadSemaphore.Release();
				return null;
			}

			SpeechDownloadSemaphore.Release();
			return fileName;
		}
	}
}
