using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BuildToolUtilities;

namespace BuildTool
{
    internal sealed class ActionThread
	{
		public int ExitCode = 0; // Cache the exit code from the command so that the executor can report errors
		public bool bComplete = false; // Set to true only when the local or RPC action is complete
		private readonly Action Action; // Cache the action that this thread is managing

		// For reporting status to the user
		private readonly int JobNumber;
		private readonly int TotalJobs;

		public ActionThread(Action InAction, int InJobNumber, int InTotalJobs)
		{
			Action = InAction;
			JobNumber = InJobNumber;
			TotalJobs = InTotalJobs;
		}

		// Used when debuging Actions outputs all action return values to debug out
		private void ActionDebugOutput(object Sender, DataReceivedEventArgs StringOutputLine)
		{
			string Output = StringOutputLine.Data;
			if (Output == null)
			{
				return;
			}

			Log.TraceInformation(Output);
		}
		
		// The actual function to run in a thread. This is potentially long and blocking
		private void ThreadFunc()
		{
			// thread start time
			Action.StartTime = DateTimeOffset.Now;

			// Create the action's process.
			ProcessStartInfo ActionStartInfo = new ProcessStartInfo
			{
				WorkingDirectory = Action.WorkingDirectory.FullName,
				FileName         = Action.CommandPath.FullName,
				Arguments        = Action.CommandArguments,
				UseShellExecute        = false,
				RedirectStandardInput  = false,
				RedirectStandardOutput = false,
				RedirectStandardError  = false
			};

			// Log command-line used to execute task if debug info printing is enabled.
			Log.TraceVerbose("Executing: {0} {1}", ActionStartInfo.FileName, ActionStartInfo.Arguments);

			// Log summary if wanted.
			if (Action.bShouldOutputStatusDescription)
			{
				string CommandDescription = Action.CommandDescription ?? Path.GetFileName(ActionStartInfo.FileName);
				if (string.IsNullOrEmpty(CommandDescription))
				{
					Log.TraceInformation(Action.StatusDescription);
				}
				else
				{
					Log.TraceInformation("[{0}/{1}] {2} {3}", JobNumber, TotalJobs, CommandDescription, Action.StatusDescription);
				}
			}

			// Try to launch the action's process, and produce a friendly error message if it fails.
			Process ActionProcess = null;
			try
			{
				try
				{
					ActionProcess = new Process { StartInfo = ActionStartInfo, };
					ActionStartInfo.RedirectStandardOutput = true;
					ActionStartInfo.RedirectStandardError = true;
					ActionProcess.OutputDataReceived += new DataReceivedEventHandler(ActionDebugOutput);
					ActionProcess.ErrorDataReceived += new DataReceivedEventHandler(ActionDebugOutput);
					ActionProcess.Start();

					ActionProcess.BeginOutputReadLine();
					ActionProcess.BeginErrorReadLine();
				}
				catch (Exception ex)
				{
					Log.TraceError("Failed to start local process for action: {0} {1}", Action.CommandPath, Action.CommandArguments);
					Log.WriteException(ex, null);
					ExitCode = 1;
					bComplete = true;
					return;
				}

				// wait for process to start
				// NOTE: this may or may not be necessary; seems to depend on whether the system UBT is running on start the process in a timely manner.
				int checkIterations = 0;
				bool haveConfiguredProcess = false;
				do
				{
					if (ActionProcess.HasExited)
					{
						if (haveConfiguredProcess == false)
						{
							Debug.WriteLine("Process for action exited before able to configure!");
						}

						break;
					}

					if (!haveConfiguredProcess)
					{
						try
						{
							ActionProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
							haveConfiguredProcess = true;
						}
						catch (Exception)
						{
						}
						break;
					}

					Thread.Sleep(10);

					checkIterations++;
				} while (checkIterations < 100);

				if (checkIterations == 100)
				{
					throw new BuildException("Failed to configure local process for action: {0} {1}", Action.CommandPath, Action.CommandArguments);
				}

				// block until it's complete
				// @todo iosmerge: UBT had started looking at:	if (Utils.IsValidProcess(Process))
				//    do we need to check that in the thread model?
				ActionProcess.WaitForExit();

				// capture exit code
				ExitCode = ActionProcess.ExitCode;
			}
			finally
			{
				// As the process has finished now, free its resources. On non-Windows platforms,
				// processes depend on POSIX/BSD threading and these are limited per application.
				// Disposing the Process releases these thread resources.
				if (ActionProcess != null)
				{
					ActionProcess.Close();
				}
			}

			Action.EndTime = DateTimeOffset.Now;
			bComplete = true;
		}

		// Starts a thread and runs the action in that thread
		public void Run()
		{
			Thread T = new Thread(ThreadFunc);
			T.Start();
		}
	};

	class LocalExecutor : ActionExecutor
	{
		// Processor count multiplier for local execution. Can be below 1 to reserve CPU for other tasks.
		// When using the local executor (not XGE), run a single action on each CPU core.
		// Note that you can set this to a larger value to get slightly faster build times in many cases,
		// but your computer's responsiveness during compiling may be much worse.
		[XMLConfigFile]
		private readonly double ProcessorCountMultiplier = 1.0;

		[XMLConfigFile]
		private readonly int MaxProcessorCount = int.MaxValue;

		public override string OutputName => "Local";

        public LocalExecutor() => XMLConfig.ApplyTo(this);

        // Determines the maximum number of actions to execute in parallel, taking into account the resources available on this machine.
        public virtual int GetMaxActionsToExecuteInParallel()
		{
			int NumLogicalCores  = Utils.GetLogicalProcessorCount(); // Get the number of logical processors
			int NumPhysicalCores = Utils.GetPhysicalProcessorCount(); // Use WMI to figure out physical cores, excluding hyper threading.

			if (NumPhysicalCores == -1)
			{
				NumPhysicalCores = NumLogicalCores;
			}

			// The number of actions to execute in parallel is trying to keep the CPU busy enough in presence of I/O stalls.
			int MaxActionsToExecuteInParallel = 0;
			if (NumPhysicalCores < NumLogicalCores && 
				ProcessorCountMultiplier != 1.0)
			{
				// The CPU has more logical cores than physical ones, aka uses hyper-threading. 
				// Use multiplier if provided
				MaxActionsToExecuteInParallel = (int)(NumPhysicalCores * ProcessorCountMultiplier);
			}
			else if (4 < NumPhysicalCores && NumPhysicalCores < NumLogicalCores)
			{
				// The CPU has more logical cores than physical ones, aka uses hyper-threading. 
				// Use average of logical and physical if we have "lots of cores"
				MaxActionsToExecuteInParallel = Math.Max((int)(NumPhysicalCores + NumLogicalCores) / 2, NumLogicalCores - 4);
			}
			// No hyper-threading. Only kicking off a task per CPU to keep machine responsive.
			else
			{
				MaxActionsToExecuteInParallel = NumPhysicalCores;
			}

#if !NET_CORE
			if (Utils.IsRunningOnMono)
			{
				long PhysicalRAMAvailableMB = (new PerformanceCounter("Mono Memory", "Total Physical Memory").RawValue) / (1024 * 1024);
				// heuristic: give each action at least 1.5GB of RAM (some clang instances will need more) if the total RAM is low, or 1GB on 16+GB machines
				long MinMemoryPerActionMB       = (PhysicalRAMAvailableMB < 16384) ? 3 * 1024 / 2 : 1024;
				int  MaxActionsAffordedByMemory = (int)(Math.Max(1, (PhysicalRAMAvailableMB) / MinMemoryPerActionMB));

				MaxActionsToExecuteInParallel = Math.Min(MaxActionsToExecuteInParallel, MaxActionsAffordedByMemory);
			}
#endif

			MaxActionsToExecuteInParallel = Math.Max(1, Math.Min(MaxActionsToExecuteInParallel, MaxProcessorCount));
			return MaxActionsToExecuteInParallel;
		}

		// Executes the specified actions locally.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			// Time to sleep after each iteration of the loop in order to not busy wait.
			const float LoopSleepTime = 0.1f;

			// The number of actions to execute in parallel is trying to keep the CPU busy enough in presence of I/O stalls.
			int MaxActionsToExecuteInParallel = GetMaxActionsToExecuteInParallel();
			Log.TraceInformation("Performing {0} actions ({1} in parallel)", Actions.Count, MaxActionsToExecuteInParallel);

			Dictionary<Action, ActionThread> ActionThreadDictionary = new Dictionary<Action, ActionThread>();
			int JobNumber = 1;

			using (ProgressWriter ProgressWriter = new ProgressWriter("Compiling C++ source code...", false))
			{
				int ProgressValue = 0;

				while (true)
				{
					// Count the number of pending and still executing actions.
					int NumUnexecutedActions = 0;
					int NumExecutingActions  = 0;
					foreach (Action Action in Actions)
					{
						bool bFoundActionProcess = ActionThreadDictionary.TryGetValue(Action, out ActionThread ActionThread);

						if (bFoundActionProcess == false)
						{
							++NumUnexecutedActions;
						}
						else if (ActionThread != null)
						{
							if (ActionThread.bComplete == false)
							{
								++NumUnexecutedActions;
								++NumExecutingActions;
							}
						}
					}

					// Update the current progress
					int NewProgressValue = Actions.Count + 1 - NumUnexecutedActions;
					if (ProgressValue != NewProgressValue)
					{
						ProgressWriter.Write(ProgressValue, Actions.Count + 1);
						ProgressValue = NewProgressValue;
					}

					// If there aren't any pending actions left, we're done executing.
					if (NumUnexecutedActions == 0)
					{
						break;
					}

					// If there are fewer actions executing than the maximum,
					// look for pending actions that don't have any outdated prerequisites.
					foreach (Action Action in Actions)
					{
						bool bFoundActionProcess = ActionThreadDictionary.TryGetValue(Action, out ActionThread ActionProcess);

						if (bFoundActionProcess == false)
						{
							if (NumExecutingActions < Math.Max(1, MaxActionsToExecuteInParallel))
							{
								// Determine whether there are any prerequisites of the action that are outdated.
								bool bHasOutdatedPrerequisites = false;
								bool bHasFailedPrerequisites = false;
								foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
								{
									if (Actions.Contains(PrerequisiteAction))
									{
										bool bFoundPrerequisiteProcess 
											= ActionThreadDictionary.TryGetValue(PrerequisiteAction, out ActionThread PrerequisiteProcess);
										if (bFoundPrerequisiteProcess == true)
										{
											if (PrerequisiteProcess == null)
											{
												bHasFailedPrerequisites = true;
											}
											else if (PrerequisiteProcess.bComplete == false)
											{
												bHasOutdatedPrerequisites = true;
											}
											else if (PrerequisiteProcess.ExitCode != 0)
											{
												bHasFailedPrerequisites = true;
											}
										}
										else
										{
											bHasOutdatedPrerequisites = true;
										}
									}
								}

								// If there are any failed prerequisites of this action, don't execute it.
								if (bHasFailedPrerequisites)
								{
									// Add a null entry in the dictionary for this action.
									ActionThreadDictionary.Add(Action, null);
								}
								// If there aren't any outdated prerequisites of this action, execute it.
								else if (!bHasOutdatedPrerequisites)
								{
									ActionThread ActionThread = new ActionThread(Action, JobNumber++, Actions.Count);
									
									try
									{
										ActionThread.Run();
									}
									catch (Exception ex)
									{
										throw new BuildException(ex, "Failed to start thread for action: {0} {1}\r\n{2}", Action.CommandPath, Action.CommandArguments, ex.ToString());
									}

									ActionThreadDictionary.Add(Action, ActionThread);

									++NumExecutingActions;
								}
							}
						}
					}

					System.Threading.Thread.Sleep(TimeSpan.FromSeconds(LoopSleepTime));
				}
			}

			Log.WriteLineIf(bLogDetailedActionStats, LogEventType.Console, "-------- Begin Detailed Action Stats ----------------------------------------------------------");
			Log.WriteLineIf(bLogDetailedActionStats, LogEventType.Console, "^Action Type^Duration (seconds)^Tool^Task^Using PCH");

			double TotalThreadSeconds = 0;

			// Check whether any of the tasks failed and log action stats if wanted.
			bool   bSuccess                   = true;

			double TotalBuildProjectTime      = 0.0f;
			double TotalCompileTime           = 0.0f;
			double TotalCreateAppBundleTime   = 0.0f;
			double TotalGenerateDebugInfoTime = 0.0f;
			double TotalLinkTime              = 0.0f;
			double TotalOtherActionsTime      = 0.0f;

			foreach (KeyValuePair<Action, ActionThread> ActionProcess in ActionThreadDictionary)
			{
				Action Action = ActionProcess.Key;
				ActionThread ActionThread = ActionProcess.Value;

				// Check for pending actions, preemptive failure
				if (ActionThread == null)
				{
					bSuccess = false;
					continue;
				}

				// Check for executed action but general failure
				if (ActionThread.ExitCode != 0)
				{
					bSuccess = false;
				}

				// Log CPU time, tool and task.
				double ThreadSeconds = Action.Duration.TotalSeconds;

				Log.WriteLineIf
				(
					bLogDetailedActionStats,
					LogEventType.Console,

					"^{0}^{1:0.00}^{2}^{3}",
					Action.Type.ToString(),
					ThreadSeconds,
					Action.CommandPath.GetFileName(),
					Action.StatusDescription
				);

				// Update statistics
				switch (Action.Type)
				{
					case ActionType.BuildProject:
						TotalBuildProjectTime += ThreadSeconds;
						break;

					case ActionType.Compile:
						TotalCompileTime += ThreadSeconds;
						break;

					case ActionType.CreateAppBundle:
						TotalCreateAppBundleTime += ThreadSeconds;
						break;

					case ActionType.GenerateDebugInfo:
						TotalGenerateDebugInfoTime += ThreadSeconds;
						break;

					case ActionType.Link:
						TotalLinkTime += ThreadSeconds;
						break;

					default:
						TotalOtherActionsTime += ThreadSeconds;
						break;
				}

				// Keep track of total thread seconds spent on tasks.
				TotalThreadSeconds += ThreadSeconds;
			}

			Log.WriteLineIf(bLogDetailedActionStats, LogEventType.Console, "-------- End Detailed Actions Stats -----------------------------------------------------------");

			// Log total CPU seconds and numbers of processors involved in tasks.
			Log.WriteLineIf(bLogDetailedActionStats,
				LogEventType.Console, "Cumulative thread seconds ({0} processors): {1:0.00}", System.Environment.ProcessorCount, TotalThreadSeconds);

			// Log detailed stats
			Log.WriteLineIf
			(
				bLogDetailedActionStats,
				LogEventType.Console,

				"Cumulative action seconds ({0} processors): {1:0.00} building projects, {2:0.00} compiling, {3:0.00} creating app bundles, {4:0.00} generating debug info, {5:0.00} linking, {6:0.00} other",
				System.Environment.ProcessorCount,
				TotalBuildProjectTime,
				TotalCompileTime,
				TotalCreateAppBundleTime,
				TotalGenerateDebugInfoTime,
				TotalLinkTime,
				TotalOtherActionsTime
			);

			return bSuccess;
		}
	};
}
