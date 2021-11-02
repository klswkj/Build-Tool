using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Xml;
using Microsoft.Win32;
using BuildToolUtilities;

namespace BuildTool
{
	class XGE : ActionExecutor
	{
		// Whether to use the no_watchdog_thread option to prevent VS2015 toolchain stalls.
		[XMLConfigFile(Category = "BuildConfiguration")]
		private bool bXGENoWatchdogThread = false;

		// Whether to display the XGE build monitor.
		[XMLConfigFile(Category = "BuildConfiguration")]
		private readonly bool bShowXGEMonitor = false;

		// When enabled, XGE will stop compiling targets after a compile error occurs.
		// Recommended, as it saves computing resources for others.	
		[XMLConfigFile(Category = "BuildConfiguration")]
		private readonly bool bStopXGECompilationAfterErrors = false;

		private const string ProgressMarkupPrefix = "@action";

		public override string OutputName => "XGE";

		public XGE()
		{
			XMLConfig.ApplyTo(this);
		}

		public static bool TryGetXgConsoleExecutable(out string OutXgConsoleExe)
		{
			// Try to get the path from the registry
			if(BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				if (TryGetXgConsoleExecutableFromRegistry(RegistryView.Registry32, out string XgConsoleExe))
				{
					OutXgConsoleExe = XgConsoleExe;
					return true;
				}
				if (TryGetXgConsoleExecutableFromRegistry(RegistryView.Registry64, out XgConsoleExe))
				{
					OutXgConsoleExe = XgConsoleExe;
					return true;
				}
			}

			// Get the name of the XgConsole executable.
			string XgConsole = Tag.Directory.XgConsole;
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				XgConsole = Tag.Binary.XgConsoleExe;
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
			{
				XgConsole = Tag.Directory.IbConsole;
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable(Tag.EnvVar.PATH);
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, XgConsole);
					if(File.Exists(PotentialPath))
					{
						OutXgConsoleExe = PotentialPath;
						return true;
					}
				}
				catch(ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
					Debugger.Break();
				}
			}

			OutXgConsoleExe = null;
			return false;
		}

		private static bool TryGetXgConsoleExecutableFromRegistry(RegistryView View, out string OutXgConsoleExe)
		{
			try
			{
				using(RegistryKey BaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, View))
				{
					using (RegistryKey Key = BaseKey.OpenSubKey(Tag.Registry.Software + Tag.Registry.XoreaxIncrediBuildBuilder, false))
					{
						if(Key != null)
						{
							string Folder = Key.GetValue(Tag.Registry.Key.Folder, null) as string;
							if(Folder.HasValue())
							{
								string FileName = Path.Combine(Folder, Tag.Binary.XgConsoleExe);
								if(File.Exists(FileName))
								{
									OutXgConsoleExe = FileName;
									return true;
								}
							}
						}
					}
				}
			}
			catch(Exception Ex)
			{
				Log.WriteException(Ex, null);
			}

			OutXgConsoleExe = null;
			return false;
		}

		public static bool IsAvailable()
		{
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			return TryGetXgConsoleExecutable(out string XgConsoleExe);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
		}

		// precompile the Regex needed to parse the XGE output (the ones we want are of the form "File (Duration at +time)"
		//private static Regex XGEDurationRegex = new Regex(@"(?<Filename>.*) *\((?<Duration>[0-9:\.]+) at [0-9\+:\.]+\)", RegexOptions.ExplicitCapture);

		public static void ExportActions(List<Action> ActionsToExecute)
		{
			for(int FileNum = 0; ; ++FileNum)
			{
				string OutFile = Path.Combine(BuildTool.EngineDirectory.FullName, Tag.Directory.Generated, Tag.Directory.Build, $"{Tag.OutputFile.BuildToolExport}.{FileNum.ToString("D3")}{Tag.Ext.XgeXML}");
				if(!File.Exists(OutFile))
				{
					ExportActions(ActionsToExecute, OutFile);
					break;
				}
			}
		}

		public static void ExportActions(List<Action> ActionsToExecute, string OutFile)
		{
			WriteTaskFile(ActionsToExecute, OutFile, ProgressWriter.bWriteMarkup, bXGEExport: true);
			Log.TraceInformation("XGEEXPORT: Exported '{0}'", OutFile);
		}

		public override bool ExecuteActions(List<Action> ActionsToExecute, bool bLogDetailedActionStats)
		{
			bool XGEResult = true;

			// Batch up XGE execution by actions with the same output event handler.
			List<Action> ActionBatch = new List<Action> { ActionsToExecute[0] };

			for (int ActionIndex = 1; ActionIndex < ActionsToExecute.Count && XGEResult; ++ActionIndex)
			{
				Action CurrentAction = ActionsToExecute[ActionIndex];
				ActionBatch.Add(CurrentAction);
			}

			if (0 < ActionBatch.Count && XGEResult)
			{
				XGEResult = ExecuteActionBatch(ActionBatch);
				ActionBatch.Clear();
			}

			return XGEResult;
		}

        private bool ExecuteActionBatch(List<Action> Actions)
		{
			bool XGEResult = true;
			if (0 < Actions.Count)
			{
				// Write the actions to execute to a XGE task file.
				string XGETaskFilePath = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated, Tag.Directory.Build, Tag.OutputFile.XGETaskXML).FullName;
				WriteTaskFile(Actions, XGETaskFilePath, ProgressWriter.bWriteMarkup, false);

				XGEResult = ExecuteTaskFileWithProgressMarkup(XGETaskFilePath, Actions.Count);
			}

			return XGEResult;
		}

        // Writes a XGE task file containing the specified actions to the specified file path.
        private static void WriteTaskFile(List<Action> InActions, string TaskFilePath, bool bProgressMarkup, bool bXGEExport)
		{
			Dictionary<string, string> ExportEnv = new Dictionary<string, string>();

			List<Action> Actions = InActions;
            if (bXGEExport)
            {
                IDictionary CurrentEnvironment = Environment.GetEnvironmentVariables();
                foreach (System.Collections.DictionaryEntry Pair in CurrentEnvironment)
                {
                    if (!BuildTool.InitialEnvironment.Contains(Pair.Key) ||
                (string)(BuildTool.InitialEnvironment[Pair.Key]) != (string)(Pair.Value))
                    {
                        ExportEnv.Add((string)(Pair.Key), (string)(Pair.Value));
                    }
                }
            }

            XmlDocument XGETaskDocument = new XmlDocument();

			// <BuildSet FormatVersion="1">...</BuildSet>
			XmlElement BuildSetElement = XGETaskDocument.CreateElement(Tag.XML.Element.BuildSet);
			XGETaskDocument.AppendChild(BuildSetElement);
			BuildSetElement.SetAttribute(Tag.XML.Attribute.FormatVersion, "1");

			// <Environments>...</Environments>
			XmlElement EnvironmentsElement = XGETaskDocument.CreateElement(Tag.XML.Element.Environments);
			BuildSetElement.AppendChild(EnvironmentsElement);

			// <Environment Name="Default">...</CompileEnvironment>
			XmlElement EnvironmentElement = XGETaskDocument.CreateElement(Tag.XML.Element.Environment);
			EnvironmentsElement.AppendChild(EnvironmentElement);
			EnvironmentElement.SetAttribute(Tag.XML.Attribute.Name, "Default");

			// <Tools>...</Tools>
			XmlElement ToolsElement = XGETaskDocument.CreateElement(Tag.XML.Element.Tools);
			EnvironmentElement.AppendChild(ToolsElement);

			if (0 < ExportEnv.Count)
			{
				// <Variables>...</Variables>
				XmlElement VariablesElement = XGETaskDocument.CreateElement(Tag.XML.Element.Variables);
				EnvironmentElement.AppendChild(VariablesElement);

				foreach (KeyValuePair<string, string> Pair in ExportEnv)
				{
					// <Variable>...</Variable>
					XmlElement VariableElement = XGETaskDocument.CreateElement(Tag.XML.Element.Variable);
					VariablesElement.AppendChild(VariableElement);
					VariableElement.SetAttribute(Tag.XML.Attribute.Name, Pair.Key);
					VariableElement.SetAttribute(Tag.XML.Attribute.Value, Pair.Value);
				}
			}

			for (int ActionIndex = 0; ActionIndex < Actions.Count; ++ActionIndex)
			{
				Action Action = Actions[ActionIndex];

				// <Tool ... />
				XmlElement ToolElement = XGETaskDocument.CreateElement(Tag.XML.Element.Tool);
				ToolsElement.AppendChild(ToolElement);
				ToolElement.SetAttribute(Tag.XML.Attribute.Name, Tag.XML.Element.Tool + ActionIndex);
				ToolElement.SetAttribute(Tag.XML.Attribute.AllowRemote, Action.bCanExecuteRemotely.ToString());

				// The XGE documentation says that 'AllowIntercept' must be set to 'true' for all tools where 'AllowRemote' is enabled
				ToolElement.SetAttribute(Tag.XML.Attribute.AllowIntercept, Action.bCanExecuteRemotely.ToString());

				string OutputPrefix = "";
				if (bProgressMarkup)
				{
					OutputPrefix += ProgressMarkupPrefix;
				}
				if (Action.bShouldOutputStatusDescription)
				{
					OutputPrefix += Action.StatusDescription;
				}
				if (0 < OutputPrefix.Length)
				{
					ToolElement.SetAttribute(Tag.XML.Attribute.OutputPrefix, OutputPrefix);
				}
				if(0 < Action.GroupNames.Count)
				{
					ToolElement.SetAttribute(Tag.XML.Attribute.GroupPrefix, String.Format("** For {0} **", String.Join(" + ", Action.GroupNames)));
				}

				ToolElement.SetAttribute(Tag.XML.Attribute.Params, Action.CommandArguments);
				ToolElement.SetAttribute(Tag.XML.Attribute.Path, Action.CommandPath.FullName);
				ToolElement.SetAttribute(Tag.XML.Attribute.SkipIfProjectFailed, "true");

				if (Action.bIsGCCCompiler)
				{
					ToolElement.SetAttribute(Tag.XML.Attribute.AutoReserveMemory, Tag.Ext.GCCPch);
				}
				else
				{
					ToolElement.SetAttribute(Tag.XML.Attribute.AutoReserveMemory, Tag.Ext.Pch);
				}
				ToolElement.SetAttribute
				(
					Tag.XML.Attribute.OutputFileMasks,
					string.Join
					(
						",",
						Action.ProducedItems.ConvertAll<string> (delegate(FileItem ProducedItem) 
						{ return ProducedItem.FileDirectory.GetFileName(); } ).ToArray()
					)
				);

				if(Action.Type == ActionType.Link)
				{
					ToolElement.SetAttribute(Tag.XML.Attribute.AutoRecover, "Unexpected PDB error; OK (0)");
				}
			}

			// <Project Name="Default" Env="Default">...</Project>
			XmlElement ProjectElement = XGETaskDocument.CreateElement(Tag.XML.Element.Project);
			BuildSetElement.AppendChild(ProjectElement);
			ProjectElement.SetAttribute(Tag.XML.Attribute.Name, "Default");
			ProjectElement.SetAttribute(Tag.XML.Attribute.Env, "Default");

			for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
			{
				Action Action = Actions[ActionIndex];

				// <Task ... />
				XmlElement TaskElement = XGETaskDocument.CreateElement(Tag.XML.Element.Task);
				ProjectElement.AppendChild(TaskElement);
				TaskElement.SetAttribute(Tag.XML.Attribute.SourceFile, "");
				if (!Action.bShouldOutputStatusDescription)
				{
					// If we were configured to not output a status description, then we'll instead
					// set 'caption' text for this task, so that the XGE coordinator has something
					// to display within the progress bars.  For tasks that are outputting a
					// description, XGE automatically displays that text in the progress bar, so we
					// only need to do this for tasks that output their own progress.
					TaskElement.SetAttribute(Tag.XML.Attribute.Caption, Action.StatusDescription);
				}
				TaskElement.SetAttribute(Tag.XML.Attribute.Name, string.Format("Action{0}", ActionIndex));
				TaskElement.SetAttribute(Tag.XML.Attribute.Tool, string.Format("Tool{0}", ActionIndex));
				TaskElement.SetAttribute(Tag.XML.Attribute.WorkingDir, Action.WorkingDirectory.FullName);
				TaskElement.SetAttribute(Tag.XML.Attribute.SkipIfProjectFailed, "true");
				TaskElement.SetAttribute(Tag.XML.Attribute.AllowRestartOnLocal, "true");

				// Create a semi-colon separated list of the other tasks this task depends on the results of.
				List<string> DependencyNames = new List<string>();
				foreach(Action PrerequisiteAction in Action.PrerequisiteActions)
				{
					if (Actions.Contains(PrerequisiteAction))
					{
						DependencyNames.Add(string.Format("Action{0}", Actions.IndexOf(PrerequisiteAction)));
					}
				}

				if (0 < DependencyNames.Count)
				{
					TaskElement.SetAttribute(Tag.XML.Attribute.DependsOn, string.Join(";", DependencyNames.ToArray()));
				}
			}

			// Write the XGE task XML to a temporary file.
			using (FileStream OutputFileStream = new FileStream(TaskFilePath, FileMode.Create, FileAccess.Write))
			{
				XGETaskDocument.Save(OutputFileStream);
			}
		}

        /*
		enum ExecutionResult
		{
			Unavailable,
			TasksFailed,
			TasksSucceeded,
		}
		*/
        // Executes the tasks in the specified file.
        // <param name="TaskFilePath">- The path to the file containing the tasks to execute in XGE XML format.</param>
        private bool ExecuteTaskFile(string TaskFilePath, DataReceivedEventHandler OutputEventHandler, int ActionCount)
		{
			// A bug in the UCRT can cause XGE to hang on VS2015 builds. Figure out if this hang is likely to effect this build and workaround it if able.
			// @todo: There is a KB coming that will fix this. Once that KB is available, test if it is present. Stalls will not be a problem if it is.
			//
			// Stalls are possible. However there is a workaround in XGE build 1659 and newer that can avoid the issue.
			string XGEVersion = (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64) ? 
				(string)Registry.GetValue(Tag.Registry.HKeyLocalMachine + Tag.Registry.Software + Tag.Registry.Wow6432Node + Tag.Registry.XoreaxIncrediBuildBuilder, "Version", null) : null;
			
			if (XGEVersion != null)
			{
				if (Int32.TryParse(XGEVersion, out int XGEBuildNumber))
				{
					// Per Xoreax support, subtract 1001000 from the registry value to get the build number of the installed XGE.
					if (1659 <= XGEBuildNumber - 1001000)
					{
						bXGENoWatchdogThread = true;
					}
					// @todo: Stalls are possible and we don't have a workaround. What should we do? Most people still won't encounter stalls, we don't really
					// want to disable XGE on them if it would have worked.
				}
			}

			if (!TryGetXgConsoleExecutable(out string XgConsolePath))
			{
				throw new BuildException("Unable to find xgConsole executable.");
			}

			ProcessStartInfo XGEStartInfo = new ProcessStartInfo
			(
				XgConsolePath,
				string.Format
				(
					@"""{0}"" /Rebuild /NoWait {1} /NoLogo {2} /ShowAgent /ShowTime {3}",
					TaskFilePath,
					bStopXGECompilationAfterErrors ? "/StopOnErrors" : "",
					bXGENoWatchdogThread ? "/no_watchdog_thread" : ""
				)
			)
			{
				UseShellExecute = false
			};

			// Use the IDE-integrated Incredibuild monitor to display progress.
			XGEStartInfo.Arguments += " /UseIdeMonitor";

			// Optionally display the external XGE monitor.
			if (bShowXGEMonitor)
			{
				XGEStartInfo.Arguments += " /OpenMonitor";
			}

			try
			{
				// Start the process, redirecting stdout/stderr if requested.
				Process XGEProcess = new Process { StartInfo = XGEStartInfo };
				bool bShouldRedirectOuput = OutputEventHandler != null;

				if (bShouldRedirectOuput)
				{
					XGEStartInfo.RedirectStandardError = true;
					XGEStartInfo.RedirectStandardOutput = true;
					XGEProcess.EnableRaisingEvents = true;
					XGEProcess.OutputDataReceived += OutputEventHandler;
					XGEProcess.ErrorDataReceived += OutputEventHandler;
				}

				XGEProcess.Start();

				if (bShouldRedirectOuput)
				{
					XGEProcess.BeginOutputReadLine();
					XGEProcess.BeginErrorReadLine();
				}

				Log.TraceInformation
				(
					"Distributing {0} action{1} to XGE",
					ActionCount,
					ActionCount == 1 ? "" : "s"
				);

				// Wait until the process is finished and return whether it all the tasks successfully executed.
				XGEProcess.WaitForExit();
				return XGEProcess.ExitCode == 0;
			}
			catch (Exception Ex)
			{
				Log.WriteException(Ex, null);
				return false;
			}
		}

        // Executes the tasks in the specified file, parsing progress markup as part of the output.
        private bool ExecuteTaskFileWithProgressMarkup(string TaskFilePath, int NumActions)
		{
			using (ProgressWriter Writer = new ProgressWriter("Compiling C++ source files...", false))
			{
				int NumCompletedActions = 0;

				// Create a wrapper delegate that will parse the output actions
				void EventHandlerWrapper(object Sender, DataReceivedEventArgs Args)
				{
					if (Args.Data != null)
					{
						string Text = Args.Data;
						if (Text.StartsWith(ProgressMarkupPrefix))
						{
							Writer.Write(++NumCompletedActions, NumActions);

							// Strip out anything that is just an XGE timer. Some programs don't output anything except the progress text.
							Text = Args.Data.Substring(ProgressMarkupPrefix.Length);
							if (Text.StartsWith(" (") && Text.EndsWith(")"))
							{
								return;
							}
						}
						Log.TraceInformation(Text);
					}
				}

				// Run through the standard XGE executor
				return ExecuteTaskFile(TaskFilePath, EventHandlerWrapper, NumActions);
			}
		}
	}
}
