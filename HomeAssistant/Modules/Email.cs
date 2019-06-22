using HomeAssistant.Core;
using HomeAssistant.Extensions;
using HomeAssistant.Log;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HomeAssistant.Modules.Interfaces;
using MimeKit.Cryptography;
using Org.BouncyCastle.Asn1.Pkcs;
using static HomeAssistant.Core.Enums;

namespace HomeAssistant.Modules {
	public class EmailBot : IDisposable {
		public class ReceviedMessageData {

			public IMessageSummary Message { get; set; }

			public uint UniqueId { get; set; }

			public bool MarkAsRead { get; set; }

			public bool MarkedAsDeleted { get; set; }

			public DateTime ArrivedTime { get; set; }
		}

		public string GmailId;
		private string GmailPass;
		private string UniqueAccountId;
		private ImapClient IdleClient;
		private ImapClient HelperClient;
		private int InboxMessagesCount;
		public bool IsAccountLoaded;
		public List<ReceviedMessageData> MessagesArrivedDuringIdle = new List<ReceviedMessageData>();
		private EmailConfig MailConfig;
		private Logger BotLogger;
		private Email EmailHandler;

		private CancellationTokenSource ImapTokenSource { get; set; }
		private CancellationTokenSource ImapCancelTokenSource { get; set; }

		private bool IsIdleCancelRequested { get; set; }

		public async Task<(bool, EmailBot)> RegisterBot(Logger botLogger, Email mailHandler, EmailConfig botConfig, ImapClient coreClient, ImapClient helperClient, string botUniqueId) {
			BotLogger = botLogger ?? throw new ArgumentNullException("botLogger is null");
			MailConfig = botConfig ?? throw new ArgumentNullException("botConfig is null");
			IdleClient = coreClient ?? throw new ArgumentNullException("coreClient is null");
			EmailHandler = mailHandler ?? throw new ArgumentNullException("Email handler is null");
			HelperClient = helperClient ?? throw new ArgumentNullException("helperClient is null");
			UniqueAccountId = botUniqueId ?? throw new ArgumentNullException("botUniqueId is null");
			GmailId = MailConfig.EmailID ?? throw new Exception("email id is either empty or null.");
			GmailPass = MailConfig.EmailPASS ?? throw new Exception("email password is either empty or null");
			IsAccountLoaded = false;

			if (MailConfig.ImapNotifications) {
				if (await InitImapNotifications(false).ConfigureAwait(false)) {
				}
			}

			return (true, this);
		}

		private async Task ReconnectImapClient(bool withImapIdle = true) {
			if (IdleClient != null) {
				if (IdleClient.IsConnected) {
					if (IdleClient.IsIdle) {
						StopImapIdle();
					}
					else {
						lock (IdleClient.SyncRoot) {
							IdleClient.Disconnect(true);
							BotLogger.Log("Disconnected IMAP client (ReconnectImapClient())", LogLevels.Trace);
						}
					}
				}
			}

			IsAccountLoaded = false;
			IdleClient = new ImapClient {
				ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
			};

			BotLogger.Log("Reconnecting to imap server (ReconnectImapClient())", LogLevels.Trace);
			IdleClient.Connect(Constants.GmailHost, Constants.GmailPort, true);
			IdleClient.Authenticate(GmailId, GmailPass);
			IdleClient.Inbox.Open(FolderAccess.ReadWrite);
			if (IdleClient.IsConnected && IdleClient.IsAuthenticated) {
				BotLogger.Log("Re-established the client connection!");
				IsAccountLoaded = true;

				if (withImapIdle) {
					if (await InitImapNotifications(false).ConfigureAwait(false)) {
					}
				}
			}
		}

		private async Task<bool> InitImapNotifications(bool idleReconnect) {
			if (!MailConfig.Enabled) {
				BotLogger.Log("This account has been disabled in the config file.");
				return false;
			}

			if (!MailConfig.ImapNotifications) {
				BotLogger.Log("Imap notification service is disabled on this account.");
				return false;
			}

			InboxMessagesCount = 0;

			if (IdleClient == null) {
				BotLogger.Log("Fatal error has occured. Idle client isn't initiated.", LogLevels.Error);
				return false;
			}

			if (!IdleClient.IsConnected || !IdleClient.IsAuthenticated) {
				BotLogger.Log("Account isn't connected or authenticated. failure in registration.", LogLevels.Warn);
				return false;
			}

			InboxMessagesCount = IdleClient.Inbox.Count;
			BotLogger.Log($"There are {InboxMessagesCount} messages in inbox folder. Starting imap idle...", LogLevels.Trace);
			IsAccountLoaded = true;
			IsIdleCancelRequested = false;

			IdleClient.Inbox.MessageExpunged += OnMessageExpunged;
			IdleClient.Inbox.CountChanged += OnInboxCountChanged;

			ImapTokenSource?.Dispose();
			ImapCancelTokenSource?.Dispose();
			ImapTokenSource = new CancellationTokenSource();
			ImapCancelTokenSource = new CancellationTokenSource();

			ImapTokenSource.Token.Register(async () => {
				if (!IsIdleCancelRequested) {
					BotLogger.Log("Restarting imap idle as connection got disconnected", LogLevels.Trace);
					IdleClient.Inbox.MessageExpunged -= OnMessageExpunged;
					IdleClient.Inbox.CountChanged -= OnInboxCountChanged;
					await InitImapNotifications(true).ConfigureAwait(false);
				}
			});

			Helpers.InBackground(async () => {
				if (IdleClient.Capabilities.HasFlag(ImapCapabilities.Idle)) {
					ImapTokenSource.CancelAfter(TimeSpan.FromMinutes(9));
					try {
						lock (IdleClient.SyncRoot) {
							BotLogger.Log($"Mail notification service started for {GmailId}", idleReconnect ? LogLevels.Trace : LogLevels.Info);
							IdleClient.Idle(ImapTokenSource.Token, ImapCancelTokenSource.Token);
						}
					}
					catch (OperationCanceledException) {
						BotLogger.Log("Idle cancelled.", LogLevels.Trace);
						//idle cancelled
					}
					catch (IOException) {
						if (Tess.Config.EnableImapIdleWorkaround) {
							BotLogger.Log("Starting mobile hotspot workaround...", LogLevels.Warn);
							if (!IsIdleCancelRequested) {
								BotLogger.Log("Restarting imap idle as connection got disconnected", LogLevels.Trace);
								await ReconnectImapClient(true).ConfigureAwait(false);
							}
						}
						//mobile hotspot error
					}
				}
				else {
					BotLogger.Log("Mail server doesn't support imap idle.", LogLevels.Warn);
					return;
				}
			}, true);

			await Task.Delay(500).ConfigureAwait(false);
			return true;
		}

		public void StopImapIdle() {
			if (ImapCancelTokenSource != null) {
				ImapCancelTokenSource.Cancel();
				IsIdleCancelRequested = true;
				BotLogger.Log("IDLE token cancel requested.", LogLevels.Trace);
			}
		}

		private async void OnInboxCountChanged(object sender, EventArgs e) {
			ImapFolder folder = (ImapFolder) sender;
			BotLogger.Log($"The number of messages in {folder.Name} has changed.", LogLevels.Trace);

			if (folder.Count <= InboxMessagesCount) {
				return;
			}

			BotLogger.Log($"{folder.Count - InboxMessagesCount} new message(s) have arrived.");

			if (!MailConfig.MuteNotifications && MailConfig.ImapNotifications) {
				Helpers.PlayNotification(NotificationContext.Imap);
			}

			StopImapIdle();

			if (Monitor.TryEnter(IdleClient.SyncRoot, TimeSpan.FromSeconds(20)) == true) {
				await ReconnectImapClient(false).ConfigureAwait(false);
				await OnMessageArrived().ConfigureAwait(false);
				if (await InitImapNotifications(false).ConfigureAwait(false)) {
				}
			}
			else {
				BotLogger.Log("failed to secure the lock object at the given time of 20 seconds.", LogLevels.Error);
				return;
			}
		}

		private async Task OnMessageArrived() {
			while (true) {
				if (!IdleClient.IsIdle) {
					BotLogger.Log("Client imap idling has been successfully stopped!", LogLevels.Trace);
					break;
				}

				BotLogger.Log("Waiting for client to shutdown idling connection...", LogLevels.Trace);
			}

			List<IMessageSummary> messages;
			if ((IdleClient != null && IdleClient.IsConnected) && IdleClient.Inbox.Count > InboxMessagesCount) {
				if (!IdleClient.IsConnected) {
					return;
				}

				lock (IdleClient.Inbox.SyncRoot) {
					messages = IdleClient.Inbox.Fetch(InboxMessagesCount, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId).ToList();
					BotLogger.Log("New message(s) have been fetched to local cache.", LogLevels.Trace);
				}

				IMessageSummary latestMessage = null;

                BotLogger.Log("Searching for latest message in local cache...", LogLevels.Trace);
				foreach (IMessageSummary msg in messages) {
					if (MessagesArrivedDuringIdle.Count <= 0) {
						latestMessage = msg;
						BotLogger.Log("Processed latest message. (No message in local cache, new message is the latest message)", LogLevels.Trace);
						BotLogger.Log($"{latestMessage.Envelope.Sender.FirstOrDefault()?.Name} / {latestMessage.Envelope.Subject}");
						Helpers.InBackgroundThread(
							() => TTSService.SpeakText($"You got an email from {latestMessage.Envelope.Sender.FirstOrDefault()?.Name} with subject {latestMessage.Envelope.Subject}",
								SpeechContext.Custom), "TTS Service");
						break;
					}

					foreach (ReceviedMessageData msgdata in MessagesArrivedDuringIdle) {
						if (msg.UniqueId.Id != msgdata.UniqueId) {
							latestMessage = msg;
							BotLogger.Log("Processed latest message.", LogLevels.Trace);
							BotLogger.Log(
								$"{latestMessage.Envelope.Sender.FirstOrDefault()?.Name} / {latestMessage.Envelope.Subject}");
							IMessageSummary message = latestMessage;
							Helpers.InBackgroundThread(
								() => TTSService.SpeakText(
									$"You got an email from {message.Envelope.Sender.FirstOrDefault()?.Name} with subject {message.Envelope.Subject}",
									SpeechContext.Custom), "TTS Service");
							break;
						}
					}
				}

				await Task.Delay(500).ConfigureAwait(false);

				foreach (IMessageSummary message in messages) {
					MessagesArrivedDuringIdle.Add(new ReceviedMessageData {
						UniqueId = message.UniqueId.Id,
						Message = message,
						MarkAsRead = false,
						MarkedAsDeleted = false,
						ArrivedTime = DateTime.Now
					});
					BotLogger.Log("updated local arrived message(s) cache with a new message.", LogLevels.Trace);
				}

				if ((!string.IsNullOrEmpty(MailConfig.AutoReplyText) || !string.IsNullOrWhiteSpace(MailConfig.AutoReplyText)) && latestMessage != null) {
					MimeMessage msg = IdleClient.Inbox.GetMessage(latestMessage.UniqueId);

					if (msg == null) {
						return;
					}

					AutoReplyEmail(msg, MailConfig.AutoReplyText);
					BotLogger.Log($"Successfully send auto reply message to {msg.Sender.Address}");
				}
			}
			else {
				BotLogger.Log("Either idle client not connected or no new messages.", LogLevels.Trace);
			}
		}

		private void AutoReplyEmail(MimeMessage msg, string replyBody) {
			_ = replyBody ?? throw new ArgumentNullException("Body is null!");
			_ = msg ?? throw new ArgumentNullException("Message is null!");

			try {
				string ReplyTextFormat = $"Reply to Previous Message with Subject: {msg.Subject}\n{replyBody}\n\n\nThank you, have a good day!";
				BotLogger.Log($"Sending Auto Reply to {msg.Sender.Address}");
				MailMessage Message = new MailMessage();
				SmtpClient SmtpServer = new SmtpClient("smtp.gmail.com");
				Message.From = new MailAddress(MailConfig.EmailID);
				Message.To.Add(msg.Sender.Address);
				Message.Subject = $"RE: {msg.Subject}";
				Message.Body = ReplyTextFormat;
				SmtpServer.Port = 587;
				SmtpServer.Credentials = new NetworkCredential(GmailId, GmailPass);
				SmtpServer.EnableSsl = true;
				SmtpServer.Send(Message);
				SmtpServer.Dispose();
				Message.Dispose();
				BotLogger.Log($"Successfully Send Auto-Reply to {msg.From.Mailboxes.First().Address}");
			}
			catch (ArgumentNullException) {
				BotLogger.Log($"One or more arguments are null or empty. cannot send auto reply email to {msg.Sender.Address}", LogLevels.Warn);
			}
			catch (InvalidOperationException) {
				BotLogger.Log($"Invalid operation exception thrown. cannot send auto reply email to {msg.Sender.Address}", LogLevels.Error);
			}
			catch (SmtpException) {
				BotLogger.Log("SMTP Exception throw, please check the credentials if they are correct.", LogLevels.Warn);
			}
			catch (Exception e) {
				BotLogger.Log(e);
			}
		}

		private void OnMessageExpunged(object sender, MessageEventArgs e) {
			if (e.Index >= InboxMessagesCount) {
				return;
			}

			InboxMessagesCount--;
			BotLogger.Log("A message has been expunged, inbox messages count has been reduced by one.", LogLevels.Trace);
		}

		public void Dispose() {
			StopImapIdle();
			ImapCancelTokenSource?.Cancel();
            BotLogger.Log("Waiting for IDLE Client to release the SyncRoot lock...");
			if (Monitor.TryEnter(IdleClient.SyncRoot, TimeSpan.FromSeconds(20)) == true) {
				if (IdleClient != null) {
					if (IdleClient.IsConnected) {
						if (IdleClient.IsIdle) {
							StopImapIdle();
						}
						else {
							lock (IdleClient.SyncRoot) {
								IdleClient.Disconnect(true);
								BotLogger.Log("Disconnected imap client.", LogLevels.Trace);
							}
						}
					}
				}

				IsAccountLoaded = false;

				if (IdleClient != null) {
					IdleClient.Dispose();
				}
			}

			if (HelperClient != null) {
				HelperClient.Dispose();
			}

			ImapCancelTokenSource?.Dispose();
			ImapTokenSource?.Dispose();
			EmailHandler.RemoveBotFromCollection(UniqueAccountId);
			BotLogger.Log("Disposed current bot instance of " + UniqueAccountId, LogLevels.Trace);
		}
	}

	public class Email : IModuleBase {
		private readonly Logger Logger = new Logger("EMAIL-HANDLER");
		public string ModuleIdentifier { get; set; } = nameof(Email);
		public Version ModuleVersion { get; set; } = new Version("4.9.0.0");
		public ConcurrentDictionary<string, EmailBot> EmailClientCollection { get; set; } = new ConcurrentDictionary<string, EmailBot>();

		public (bool, ConcurrentDictionary<string, EmailBot>) InitEmailBots() {
			if (Tess.Config.EmailDetails.Count <= 0 || !Tess.Config.EmailDetails.Any()) {
				Logger.Log("No email IDs found in global config. cannot start Email Module...", LogLevels.Trace);
				return (false, new ConcurrentDictionary<string, EmailBot>());
			}

			EmailClientCollection.Clear();
			int loadedAccounts = 0;

			try {
				foreach (KeyValuePair<string, EmailConfig> entry in Tess.Config.EmailDetails) {
					if (string.IsNullOrEmpty(entry.Value.EmailID) || string.IsNullOrWhiteSpace(entry.Value.EmailPASS)) {
						continue;
					}

					string uniqueId = entry.Key;
					int connectionTry = 1;
					int maxConnectionRetry = 5;

					while (true) {
						if (connectionTry > maxConnectionRetry) {
							Logger.Log(
								$"Connection to gmail account {entry.Value.EmailID} failed after {maxConnectionRetry} attempts. cannot proceed for this account.",
								LogLevels.Error);
							return (false, null);
						}

						try {
							Logger BotLogger = new Logger(entry.Value.EmailID?.Split('@')?.FirstOrDefault()?.Trim());
							Logger.Log($"Loaded {entry.Value.EmailID} mail account to processing state.",
								LogLevels.Trace);

							ImapClient BotClient;
							if (Tess.Config.Debug) {
								BotClient = new ImapClient(new ProtocolLogger(uniqueId + ".txt")) {
									ServerCertificateValidationCallback =
										(sender, certificate, chain, sslPolicyErrors) => true
								};
							}
							else {
								BotClient = new ImapClient() {
									ServerCertificateValidationCallback =
										(sender, certificate, chain, sslPolicyErrors) => true
								};
							}

							BotClient.Connect(Constants.GmailHost, Constants.GmailPort, true);
							BotClient.Authenticate(entry.Value.EmailID, entry.Value.EmailPASS);
							BotClient.Inbox.Open(FolderAccess.ReadWrite);

							if (BotClient.IsConnected && BotClient.IsAuthenticated) {
								Logger.Log($"Connected and authenticated IDLE-CLIENT for {entry.Value.EmailID}",
									LogLevels.Trace);
							}

							Task.Delay(500).Wait();

							ImapClient BotHelperClient = new ImapClient {
								ServerCertificateValidationCallback =
									(sender, certificate, chain, sslPolicyErrors) => true
							};

							BotHelperClient.Connect(Constants.GmailHost, Constants.GmailPort, true);
							BotHelperClient.Authenticate(entry.Value.EmailID, entry.Value.EmailPASS);
							BotHelperClient.Inbox.Open(FolderAccess.ReadWrite);

							if (BotHelperClient.IsConnected && BotHelperClient.IsAuthenticated) {
								Logger.Log($"Connected and authenticated HELPER-CLIENT for {entry.Value.EmailID}",
									LogLevels.Trace);
							}

							EmailBot Bot = new EmailBot();
							(bool, EmailBot) registerResult =
								Bot.RegisterBot(BotLogger, this, entry.Value, BotClient, BotHelperClient, uniqueId).Result;

							if (registerResult.Item1) {
								AddBotToCollection(uniqueId, Bot);
							}

							if ((registerResult.Item2.IsAccountLoaded || registerResult.Item1) &&
							    EmailClientCollection.ContainsKey(uniqueId)) {
								Logger.Log($"Connected and authenticated {registerResult.Item2.GmailId} account!");
								loadedAccounts++;
								continue;
							}
							else {
								connectionTry++;
								continue;
							}
						}
						catch (AuthenticationException) {
							Logger.Log(
								$"Account password must be incorrect. we will retry to connect. ({connectionTry++}/{maxConnectionRetry})");
						}
						catch (SocketException) {
							Logger.Log(
								$"Network connectivity problem occured, we will retry to connect. ({connectionTry++}/{maxConnectionRetry})",
								LogLevels.Warn);
						}
						catch (OperationCanceledException) {
							Logger.Log(
								$"An operation has been cancelled, we will retry to connect. ({connectionTry++}/{maxConnectionRetry})",
								LogLevels.Warn);
						}
						catch (IOException) {
							Logger.Log(
								$"IO exception occured. we will retry to connect. ({connectionTry++}/{maxConnectionRetry})",
								LogLevels.Warn);
						}
						catch (Exception e) {
							Logger.Log(e, LogLevels.Error);
						}
					}
				}
			}
			catch (Exception e) {
				Logger.Log(e);
				return (false, EmailClientCollection);
			}

			Logger.Log($"Loaded {loadedAccounts} accounts out of {Tess.Config.EmailDetails.Count} accounts in config.", LogLevels.Trace);
			return (true, EmailClientCollection);
		}

		public void DisposeEmailClient (string botUniqueId) {
			if (EmailClientCollection.Count <= 0 || EmailClientCollection == null) {
				return;
			}

			foreach (KeyValuePair<string, EmailBot> pair in EmailClientCollection) {
				if (pair.Key.Equals(botUniqueId)) {
					pair.Value.Dispose();
					Logger.Log($"Disposed {pair.Value.GmailId} email account.");
				}
			}
		}

		public void DisposeAllEmailBots () {
			if (EmailClientCollection.Count <= 0 || EmailClientCollection == null) {
				return;
			}

			foreach (KeyValuePair<string, EmailBot> pair in EmailClientCollection) {
				if (pair.Value.IsAccountLoaded) {
					pair.Value.Dispose();
					Logger.Log($"Disposed {pair.Value.GmailId} email account.");
				}
			}
			EmailClientCollection.Clear();
		}

		public void AddBotToCollection(string uniqueId, EmailBot bot) {
			if (EmailClientCollection.ContainsKey(uniqueId)) {
				Logger.Log("Deleting duplicate entry of current instance.", LogLevels.Trace);
				EmailClientCollection.TryRemove(uniqueId, out _);
			}

			EmailClientCollection.TryAdd(uniqueId, bot);
			Logger.Log("Added current instance to client collection.", LogLevels.Trace);
		}

		public void RemoveBotFromCollection(string uniqueId) {
			if (EmailClientCollection.ContainsKey(uniqueId)) {
				Logger.Log("Remove current bot instance from collection.", LogLevels.Trace);
				EmailClientCollection.TryRemove(uniqueId, out _);
			}
		}

		public (bool, Email) InitModuleService<Email>() {
			
		}

		public bool InitModuleShutdown() {
			
		}
	}
}
