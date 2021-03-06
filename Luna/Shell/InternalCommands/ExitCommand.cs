using Synergy.Extensions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Luna.Shell.InternalCommands {
	public class ExitCommand : IShellCommand, IDisposable {
		public bool HasParameters => true;

		public string CommandName => "Exit Command";

		public bool IsInitSuccess { get; set; }

		public int MaxParameterCount => 2;

		public string CommandDescription => "Exits assistant process.";

		public string CommandKey => "exit";

		public SemaphoreSlim Sync { get; set; }

		public Func<Parameter, bool> OnExecuteFunc { get; set; }

		public void Dispose() {
			IsInitSuccess = false;
			Sync.Dispose();
		}

		public async Task ExecuteAsync(Parameter parameter) {
			if (!IsInitSuccess) {
				return;
			}

			if (parameter.Parameters.Length > MaxParameterCount) {
				ShellIO.Error("Too many arguments.");
				return;
			}

			await Sync.WaitAsync().ConfigureAwait(false);

			try {
				if (OnExecuteFunc != null) {
					if (OnExecuteFunc.Invoke(parameter)) {
						return;
					}
				}

				int exitCode = 0;
				switch (parameter.ParameterCount) {
					case 0:
						ShellIO.Info("Exiting assistant in 5 seconds...");
						Helpers.ScheduleTask(() => Program.CoreInstance.ExitEnvironment(0), TimeSpan.FromSeconds(5));
						return;
					case 1 when !string.IsNullOrEmpty(parameter.Parameters[0]):
						if (!int.TryParse(parameter.Parameters[0], out exitCode)) {
							ShellIO.Error("Couldn't parse exit code argument.");
							return;
						}

						ShellIO.Info($"Exiting assistant with '{exitCode}' exit code in 5 seconds...");
						Helpers.ScheduleTask(() => Program.CoreInstance.ExitEnvironment(exitCode), TimeSpan.FromSeconds(5));
						return;
					case 2 when !string.IsNullOrEmpty(parameter.Parameters[0]) && !string.IsNullOrEmpty(parameter.Parameters[1]):
						if (!int.TryParse(parameter.Parameters[0], out exitCode)) {
							ShellIO.Error("Couldn't parse exit code argument.");
							return;
						}

						if (!int.TryParse(parameter.Parameters[1], out int delay)) {
							ShellIO.Error("Couldn't parse delay argument.");
							return;
						}

						ShellIO.Info($"Exiting assistant with '{exitCode}' exit code in '{delay}' seconds...");
						Helpers.ScheduleTask(() => Program.CoreInstance.ExitEnvironment(exitCode), TimeSpan.FromSeconds(delay));
						return;
					default:
						ShellIO.Error("Command seems to be in incorrect syntax.");
						return;
				}
			}
			catch (Exception e) {
				ShellIO.Exception(e);
				return;
			}
			finally {
				Sync.Release();
			}
		}

		public async Task InitAsync() {
			Sync = new SemaphoreSlim(1, 1);
			IsInitSuccess = true;
		}

		public void OnHelpExec(bool quickHelp) {
			if (quickHelp) {
				ShellIO.Info($"{CommandName} - {CommandKey} | {CommandDescription} | {CommandKey} -[exit_code]");
				return;
			}

			ShellIO.Info($"----------------- { CommandName} | {CommandKey} -----------------");
			ShellIO.Info($"|> {CommandDescription}");
			ShellIO.Info($"Basic Syntax -> ' {CommandKey} '");
			ShellIO.Info($"Advanced -> ' {CommandKey} -[exit_code] '");
			ShellIO.Info($"Advanced With Delay -> ' {CommandKey} -[exit_code] -[delay_in_seconds] '");
			ShellIO.Info($"----------------- ----------------------------- -----------------");
		}

		public bool Parse(Parameter parameter) {
			if (!IsInitSuccess) {
				return false;
			}

			return false;
		}
	}
}
