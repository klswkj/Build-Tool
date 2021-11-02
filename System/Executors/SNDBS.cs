using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess; // SNDBS
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	class SNDBS : ActionExecutor
	{
		private FileReference IncludeRewriteRulesFile => 
			FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.SNDBS, Tag.Binary.IncludeRewriteRulesIni);

		[XMLConfigFile]
		public double ProcessorCountMultiplier = 1.0; // Processor count multiplier for local execution. Can be below 1 to reserve CPU for other tasks.

		[XMLConfigFile]
		public int MaxProcessorCount = int.MaxValue; // Maximum processor count for local execution. 

		private int MaxActionsToExecuteInParallel; // The number of actions to execute in parallel is trying to keep the CPU busy enough in presence of I/O stalls.
		
		private int JobNumber; // Unique id for new jobs

		public SNDBS() => XMLConfig.ApplyTo(this);

		public override string OutputName => "SNDBS";

		// Used when debugging Actions outputs all action return values to debug out
		// <param name="sender"> Sending object</param>
		// <param name="e">  Event arguments (In this case, the line of string output)</param>
		static protected void ActionDebugOutput(object SendingObject, DataReceivedEventArgs StringOutputLine)
		{
			string Output = StringOutputLine.Data;
			if (Output == null)
			{
				return;
			}

			Log.TraceInformation(Output);
		}

		internal bool ExecuteLocalActions(List<Action> InLocalActions, Dictionary<Action, ActionThread> InActionThreadDictionary, int TotalNumJobs)
		{
			// Time to sleep after each iteration of the loop in order to not busy wait.
			const float LoopSleepTime = 0.1f;

			bool LocalActionsResult = true;

			int NumUnexecutedActions;
			int NumExecutingActions;

			while (true)
			{
				// Count the number of pending and still executing actions.
				NumUnexecutedActions = 0;
				NumExecutingActions  = 0;

				foreach (Action Action in InLocalActions)
				{
					bool bFoundActionProcess = InActionThreadDictionary.TryGetValue(Action, out ActionThread ActionThread);
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

				// If there aren't any pending actions left, we're done executing.
				if (NumUnexecutedActions == 0)
				{
					break;
				}

				// If there are fewer actions executing than the maximum, look for pending actions that don't have any outdated
				// prerequisites.
				foreach (Action Action in InLocalActions)
				{
					bool bFoundActionProcess = InActionThreadDictionary.TryGetValue(Action, out ActionThread ActionProcess);
					if (bFoundActionProcess == false)
					{
						if (NumExecutingActions < Math.Max(1, MaxActionsToExecuteInParallel))
						{
							// Determine whether there are any prerequisites of the action that are outdated.
							bool bHasOutdatedPrerequisites = false;
							bool bHasFailedPrerequisites = false;
							foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
							{
								if (InLocalActions.Contains(PrerequisiteAction))
								{
									bool bFoundPrerequisiteProcess = InActionThreadDictionary.TryGetValue(PrerequisiteAction, out ActionThread PrerequisiteProcess);
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
								InActionThreadDictionary.Add(Action, null);
							}
							// If there aren't any outdated prerequisites of this action, execute it.
							else if (!bHasOutdatedPrerequisites)
							{
								ActionThread ActionThread = new ActionThread(Action, JobNumber, TotalNumJobs);
								ActionThread.Run();

								InActionThreadDictionary.Add(Action, ActionThread);

								++NumExecutingActions;
								++JobNumber;
							}
						}
					}
				}

				System.Threading.Thread.Sleep(TimeSpan.FromSeconds(LoopSleepTime));
			}

			return LocalActionsResult;
		}

		internal bool ExecuteActions(List<Action> InActions, Dictionary<Action, ActionThread> InActionThreadDictionary)
		{
			// Build the script file that will be executed by SN-DBS
			StreamWriter ScriptFile;
			string ScriptFilename = Path.Combine(BuildTool.EngineDirectory.FullName, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.SNDBSBat);

			FileStream ScriptFileStream = new FileStream(ScriptFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
			ScriptFile = new StreamWriter(ScriptFileStream) { AutoFlush = true };

			int NumScriptedActions = 0;
			List<Action> LocalActions = new List<Action>();
			ActionThread DummyActionThread = new ActionThread(null, 1, 1);
			bool PrintDebugInfo = true;
			foreach (Action Action in InActions)
			{
				bool bFoundActionProcess = InActionThreadDictionary.TryGetValue(Action, out ActionThread ActionProcess);
				if (bFoundActionProcess == false)
				{
					// Determine whether there are any prerequisites of the action that are outdated.
					bool bHasOutdatedPrerequisites = false;
					bool bHasFailedPrerequisites = false;
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						if (InActions.Contains(PrerequisiteAction))
						{
							bool bFoundPrerequisiteProcess = InActionThreadDictionary.TryGetValue(PrerequisiteAction, out ActionThread PrerequisiteProcess);

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
						InActionThreadDictionary.Add(Action, null);
					}
					// If there aren't any outdated prerequisites of this action, execute it.
					else if (!bHasOutdatedPrerequisites)
					{
						if (Action.bCanExecuteRemotely          == false || 
							Action.bCanExecuteRemotelyWithSNDBS == false)
						{
							// Execute locally
							LocalActions.Add(Action);
						}
						else
						{
							// Create a dummy force-included file which references PCH files, so that SN-DBS knows they are dependencies.
							string AdditionalStubIncludes = "";
							if (Action.CommandPath.GetFileName().Equals(Tag.Binary.ClExe, StringComparison.OrdinalIgnoreCase) 
							 || Action.CommandPath.GetFileName().Equals(Tag.Binary.ClFilter, StringComparison.OrdinalIgnoreCase))
							{
								string DummyPCHIncludeFile = Action.DependencyListFile.AbsolutePath.Replace("\"", "").Replace("@", "").Trim();
								DummyPCHIncludeFile = Path.ChangeExtension(DummyPCHIncludeFile, null);

								StringBuilder WrapperContents = new StringBuilder();
								using (StringWriter Writer = new StringWriter(WrapperContents))
								{
									Writer.WriteLine("// PCH dependencies for {0}", DummyPCHIncludeFile);
									Writer.WriteLine(Tag.CppContents.If + "0");
									foreach (FileItem Preqrequisite in Action.PrerequisiteItems)
									{
										if (Preqrequisite.AbsolutePath.EndsWith(Tag.Ext.Pch))
										{
											Writer.WriteLine(Tag.CppContents.Include + "\"{0}\"", Preqrequisite.AbsolutePath.Replace(Tag.Ext.Pch, Tag.Ext.Obj));
										}
									}
									Writer.WriteLine(Tag.CppContents.Endif);
								}

								FileReference DummyPCHIncludeFileDependency = new FileReference(DummyPCHIncludeFile + Tag.Ext.DummyHeader);
								StringUtils.WriteFileIfChanged(DummyPCHIncludeFileDependency, WrapperContents.ToString(), StringComparison.OrdinalIgnoreCase);
								AdditionalStubIncludes = string.Format(Tag.Argument.CompilerOption.MSVC.ForceIncludeName + "\"{0}\"", DummyPCHIncludeFileDependency);
							}

							// Add to script for execution by SN-DBS
							string NewCommandArguments = "\"" + Action.CommandPath + "\"" + " " + Action.CommandArguments + " " + AdditionalStubIncludes;
							ScriptFile.WriteLine(NewCommandArguments);
							InActionThreadDictionary.Add(Action, DummyActionThread);
							Action.StartTime = Action.EndTime = DateTimeOffset.Now;
							Log.TraceInformation("[{0}/{1}] {2} {3}", JobNumber, InActions.Count, Action.CommandDescription, Action.StatusDescription);
							JobNumber++;
							NumScriptedActions++;
							PrintDebugInfo |= Action.bPrintDebugInfo;

							if (Action.DependencyListFile != null && File.Exists(Action.DependencyListFile.AbsolutePath))
							{
								Log.TraceVerbose("Deleting dependency list file {0}", Action.DependencyListFile.AbsolutePath);
								File.Delete(Action.DependencyListFile.AbsolutePath);
							}
						}
					}
				}
			}

			ScriptFile.Flush();
			ScriptFile.Close();
			ScriptFile.Dispose();
			ScriptFile = null;

			if (0 < NumScriptedActions)
			{
                // Create the process
                string SCERoot = Environment.GetEnvironmentVariable(Tag.EnvVar.SCERootDir);
                string SNDBSExecutable = Path.Combine(SCERoot, Tag.Directory.Common, Tag.Directory.SN_DBS, Tag.Directory.Bin, Tag.Binary.DBSBuild);
				
				string VerbosityLevel = PrintDebugInfo ? "-v" : "-q"; 
				DirectoryReference TemplatesDir = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.ExternalTools, Tag.Module.ExternalTool.BuildTool, Tag.Directory.SNDBSTemplates);
				string IncludeRewriteRulesArg = String.Format("--include-rewrite-rules \"{0}\"", IncludeRewriteRulesFile.FullName);

				ProcessStartInfo PSI = new ProcessStartInfo
				(
					SNDBSExecutable, 
					String.Format
					(
						"{0} -p Code -s \"{1}\" -templates \"{2}\" {3}", 
						VerbosityLevel, 
						FileReference.Combine
						(
							BuildTool.EngineDirectory, 
							Tag.Directory.Generated, 
							Tag.Directory.Build, 
							Tag.Binary.SNDBSBat
						).FullName, 
					    TemplatesDir.FullName, 
					    IncludeRewriteRulesArg
					)
				)
				{
					RedirectStandardOutput = true,
					RedirectStandardError  = true,
					UseShellExecute        = false,
					CreateNoWindow         = true,
					WorkingDirectory       = Path.GetFullPath(".")
				};

				Process NewProcess = new Process { StartInfo = PSI };
				NewProcess.OutputDataReceived += new DataReceivedEventHandler(ActionDebugOutput);
				NewProcess.ErrorDataReceived += new DataReceivedEventHandler(ActionDebugOutput);

				DateTimeOffset StartTime = DateTimeOffset.Now;

				NewProcess.Start();
				NewProcess.BeginOutputReadLine();
				NewProcess.BeginErrorReadLine();
				NewProcess.WaitForExit();

				DummyActionThread.bComplete = true;
				int ExitCode = NewProcess.ExitCode;
				if (ExitCode != 0)
				{
					return false;
				}
			}

			// Execute local tasks
			if (0 < LocalActions.Count)
			{
				return ExecuteLocalActions(LocalActions, InActionThreadDictionary, InActions.Count);
			}

			return true;
		}

		public static bool IsAvailable()
		{
			string SCERoot = Environment.GetEnvironmentVariable(Tag.EnvVar.SCERootDir);
			if(SCERoot == null)
			{
				return false;
			}
			if (!File.Exists(Path.Combine(SCERoot, Tag.Directory.Common, Tag.Directory.SN_DBS, Tag.Directory.Bin, Tag.Binary.DBSBuild)))
			{
				return false;
			}

			ServiceController[] services = ServiceController.GetServices();
			foreach (ServiceController service in services)
			{
				if (service.ServiceName.StartsWith(Tag.Binary.SNDBSOutpuName) && service.Status == ServiceControllerStatus.Running)
				{
					return true;
				}
			}
			return false;
		}

		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool SNDBSResult = true;
			if (0 < Actions.Count)
			{
				// Generate any needed templates. Can only generate one per executable, so just use the first Action for reference
				IEnumerable<Action> CommandPaths = Actions.GroupBy(a => a.CommandPath).Select(g => g.FirstOrDefault()).ToList();
				foreach (Action CommandPath in CommandPaths)
				{
					PrepareToolTemplate(CommandPath);
				}

				// Generate include-rewrite-rules.ini.
				GenerateSNDBSIncludeRewriteRules();

				// Use WMI to figure out physical cores, excluding hyper threading.
				int NumCores = Utils.GetPhysicalProcessorCount();
				
				// If we failed to detect the number of cores, default to the logical processor count
				if (NumCores == -1)
				{
					NumCores = System.Environment.ProcessorCount;
				}
				// The number of actions to execute in parallel is trying to keep the CPU busy enough in presence of I/O stalls.
				MaxActionsToExecuteInParallel = 0;
				// The CPU has more logical cores than physical ones, aka uses hyper-threading. 
				if (NumCores < System.Environment.ProcessorCount)
				{
					MaxActionsToExecuteInParallel = (int)(NumCores * ProcessorCountMultiplier);
				}
				// No hyper-threading. Only kicking off a task per CPU to keep machine responsive.
				else
				{
					MaxActionsToExecuteInParallel = NumCores;
				}
				MaxActionsToExecuteInParallel = Math.Min(MaxActionsToExecuteInParallel, MaxProcessorCount);

				JobNumber = 1;
				Dictionary<Action, ActionThread> ActionThreadDictionary = new Dictionary<Action, ActionThread>();

				while (true)
				{
					bool bUnexecutedActions = false;
					foreach (Action Action in Actions)
					{
						bool bFoundActionProcess = ActionThreadDictionary.TryGetValue(Action, out ActionThread ActionThread);
						if (bFoundActionProcess == false)
						{
							bUnexecutedActions = true;
							if(!ExecuteActions(Actions, ActionThreadDictionary))
							{
								return false;
							}
							break;
						}
					}

					if (bUnexecutedActions == false)
					{
						break;
					}
				}

				Log.WriteLineIf(bLogDetailedActionStats, LogEventType.Console, "-------- Begin Detailed Action Stats ----------------------------------------------------------");
				Log.WriteLineIf(bLogDetailedActionStats, LogEventType.Console, "^Action Type^Duration (seconds)^Tool^Task^Using PCH");

				double TotalThreadSeconds = 0;

				// Check whether any of the tasks failed and log action stats if wanted.
				foreach (KeyValuePair<Action, ActionThread> ActionProcess in ActionThreadDictionary)
				{
					Action Action = ActionProcess.Key;
					ActionThread ActionThread = ActionProcess.Value;

					// Check for pending actions, preemptive failure
					if (ActionThread == null)
					{
						SNDBSResult = false;
						continue;
					}
					// Check for executed action but general failure
					if (ActionThread.ExitCode != 0)
					{
						SNDBSResult = false;
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

					// Keep track of total thread seconds spent on tasks.
					TotalThreadSeconds += ThreadSeconds;
				}

				Log.TraceInformation("-------- End Detailed Actions Stats -----------------------------------------------------------");

				// Log total CPU seconds and numbers of processors involved in tasks.
				Log.WriteLineIf(bLogDetailedActionStats,
					LogEventType.Console, "Cumulative thread seconds ({0} processors): {1:0.00}", System.Environment.ProcessorCount, TotalThreadSeconds);
			}

			// Delete the include-rewrite-rules.ini if it was generated.
			if (File.Exists(IncludeRewriteRulesFile.FullName))
			{
				File.Delete(IncludeRewriteRulesFile.FullName);
			}

			return SNDBSResult;
		}

		private void PrepareToolTemplate(Action Action)
		{
			string TemplateFileName = String.Format(Action.CommandPath.GetFileName() + Tag.Ext.SNDBSToolIni);
			FileReference TemplateInput = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.SNDBSTemplates, TemplateFileName);
			
			// If no base template exists, don't try to generate one.
			if (!File.Exists(TemplateInput.FullName))
			{
				return;
			}

			FileReference TemplateOutput = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.ExternalTools, Tag.Module.ExternalTool.BuildTool, Tag.Directory.SNDBSTemplates, TemplateFileName);
			if (!Directory.Exists(TemplateOutput.Directory.FullName))
			{
				Directory.CreateDirectory(TemplateOutput.Directory.FullName);
			}

			string TemplateText = File.ReadAllText(TemplateInput.FullName);
			TemplateText = TemplateText.Replace(Tag.EnvVar.CommandPath, Action.CommandPath.Directory.FullName);
			foreach (DictionaryEntry Variable in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
			{
				string VariableName = String.Format("{{{0}}}", Variable.Key);
				TemplateText = TemplateText.Replace(VariableName, Variable.Value.ToString());
			}
			File.WriteAllText(TemplateOutput.FullName, TemplateText);
		}

		private void GenerateSNDBSIncludeRewriteRules()
		{
			// Get all registered platforms. Most will just use the name as is, but some may have an override so
			// add all distinct entries to the list.
			IEnumerable<BuildTargetPlatform> Platforms = BuildPlatform.GetRegisteredPlatforms();
			List<string> PlatformNames = new List<string>();
			foreach (BuildTargetPlatform Platform in Platforms)
			{
				BuildPlatform PlatformData = BuildPlatform.GetBuildPlatform(Platform);
				if (!PlatformNames.Contains(PlatformData.GetPlatformName()))
				{
					PlatformNames.Add(PlatformData.GetPlatformName());
				}
			}

			if (!Directory.Exists(IncludeRewriteRulesFile.Directory.FullName))
			{
				Directory.CreateDirectory(IncludeRewriteRulesFile.Directory.FullName);
			}

			List<string> IncludeRewriteRulesText = new List<string> { "[computed-include-rules]" };

			{
				IncludeRewriteRulesText.Add(Tag.Regex.CompiledPlatformHeader);
				IEnumerable<string> PlatformExpansions = PlatformNames.Select(p => String.Format("{0}/{0}$1|{0}$1", p));
				IncludeRewriteRulesText.Add(String.Format("expansions1={0}", String.Join("|", PlatformExpansions)));
			}
			{
				IncludeRewriteRulesText.Add(Tag.Regex.CompiledPlatformHeaderWithPrefix);
				IEnumerable<string> PlatformExpansions = PlatformNames.Select(p => String.Format("$1/{0}/{0}$2|$1/{0}$2", p));
				IncludeRewriteRulesText.Add(String.Format("expansions2={0}", String.Join("|", PlatformExpansions)));
			}
			{
				IncludeRewriteRulesText.Add(Tag.Regex.PlatformHeaderName);
				IEnumerable<string> PlatformExpansions = PlatformNames.Select(p => String.Format("{0}/{0}$1|{0}$1", p));
				IncludeRewriteRulesText.Add(String.Format("expansions3={0}", String.Join("|", PlatformExpansions)));
			}
			{
				IncludeRewriteRulesText.Add(Tag.Regex.PlatformHeaderNameWithPrefix);
				IEnumerable<string> PlatformExpansions = PlatformNames.Select(p => String.Format("$1/{0}/{0}$2|$1/{0}$2", p));
				IncludeRewriteRulesText.Add(String.Format("expansions4={0}", String.Join("|", PlatformExpansions)));
			}

			File.WriteAllText(IncludeRewriteRulesFile.FullName, String.Join(Environment.NewLine, IncludeRewriteRulesText));
		}
	}
}
