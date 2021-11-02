using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildToolUtilities;

namespace BuildTool
{
    // The current hot reload mode
    enum HotReloadMode
	{
		Default,
		Disabled,
		FromIDE,
		FromEditor,
		LiveCoding
	}

	// Stores the current hot reload state, tracking temporary files created by previous invocations.
	[Serializable]
    class HotReloadState
	{
		// Suffix to use for the next hot reload invocation
		public int NextSuffix = 1;

		// Map from original filename in the action graph to hot reload file
		public Dictionary<FileReference, FileReference> OriginalFileToHotReloadFile = new Dictionary<FileReference, FileReference>();

		// Set of all temporary files created for hot reload
		public HashSet<FileReference> TemporaryFiles = new HashSet<FileReference>();

		// Adds all the actions into the hot reload state, so we can restore the action graph on next iteration
		// <param name="ActionsToExecute">The actions being executed</param>
		// <param name="OldLocationToNewLocation">Mapping from file from their original location (either a previously hot-reloaded file, or an originally compiled file)</param>
		public void CaptureActions(IEnumerable<Action> ActionsToExecute, Dictionary<FileReference, FileReference> OldLocationToNewLocation)
		{
			// Build a mapping of all file items to their original location
			Dictionary<FileReference, FileReference> HotReloadFileToOriginalFile = new Dictionary<FileReference, FileReference>();

			foreach(KeyValuePair<FileReference, FileReference> Pair in OriginalFileToHotReloadFile)
			{
				HotReloadFileToOriginalFile[Pair.Value] = Pair.Key;
			}

			foreach(KeyValuePair<FileReference, FileReference> Pair in OldLocationToNewLocation)
			{
				if (!HotReloadFileToOriginalFile.TryGetValue(Pair.Key, out FileReference OriginalLocation))
				{
					OriginalLocation = Pair.Key;
				}

				HotReloadFileToOriginalFile[Pair.Value] = OriginalLocation;
			}

			// Now filter out all the hot reload files and update the state
			foreach(Action Action in ActionsToExecute)
			{
				foreach(FileItem ProducedItem in Action.ProducedItems)
				{
					if (HotReloadFileToOriginalFile.TryGetValue(ProducedItem.FileDirectory, out FileReference OriginalLocation))
					{
						OriginalFileToHotReloadFile[OriginalLocation] = ProducedItem.FileDirectory;
						TemporaryFiles.Add(ProducedItem.FileDirectory);
					}
				}
			}
		}

		// Gets the location of the hot-reload state file for a particular target
		public static FileReference GetLocation
		(
			FileReference             ProjectContainingTarget, 
			string                    TargetName, 
			BuildTargetPlatform      PlatformBeingBuilt, 
			TargetConfiguration ConfigurationBeingBuilt, 
			string                    ArchitectureBeingBuilt
		)
		{
			DirectoryReference BaseDir = DirectoryReference.FromFile(ProjectContainingTarget) ?? BuildTool.EngineDirectory;
			return FileReference.Combine
				(BaseDir, BuildTool.GetPlatformGeneratedFolder(PlatformBeingBuilt, ArchitectureBeingBuilt), 
				TargetName, ConfigurationBeingBuilt.ToString(), "HotReload.state");
		}

		// Read the hot reload state from the given location
		public static HotReloadState Load(FileReference Location)
		{
			return BinaryFormatterUtils.Load<HotReloadState>(Location);
		}

		// Writes the state to disk
		public void Save(FileReference Location)
		{
			DirectoryReference.CreateDirectory(Location.Directory);
			BinaryFormatterUtils.Save(Location, this);
		}
	}

    internal static class HotReload
	{
		// Checks whether a live coding session is currently active for a target.
		// If so, we don't want to allow modifying any object files before they're loaded.
		
		// <param name="Makefile">Makefile for the target being built</param>
		// <returns>True if a live coding session is active, false otherwise</returns>
		public static bool IsLiveCodingSessionActive(TargetMakefile MakefileForTargetBeingBuilt)
		{
			// Find the first output executable
			FileReference Executable = MakefileForTargetBeingBuilt.ExecutableFile;

			if(Executable != null)
			{
				// Build the mutex name. This should match the name generated in LiveCodingModule.cpp.
				StringBuilder MutexName = new StringBuilder("Global\\LiveCoding_");

				for(int Idx = 0; Idx < Executable.FullName.Length; ++Idx)
				{
					char Character = Executable.FullName[Idx];
					if(Character == '/' || Character == '\\' || Character == ':')
					{
						MutexName.Append('+');
					}
					else
					{
						MutexName.Append(Character);
					}
				}
				Log.TraceLog("Checking for live coding mutex: {0}", MutexName);

				// Try to open the mutex
#pragma warning disable IDE0018 // Inline variable declaration
				Mutex Mutex;
#pragma warning restore IDE0018 // Inline variable declaration

				if (Mutex.TryOpenExisting(MutexName.ToString(), out Mutex))
				{
					Mutex.Dispose();
					return true;
				}
			}

			return false;
		}

		// Checks if the editor is currently running and this is a hot-reload
		public static bool ShouldDoHotReloadFromIDE(BuildConfiguration BuildConfiguration, BuildTargetDescriptor TargetDesc)
		{
			// Check if Hot-reload is disabled globally for this project
			ConfigHierarchy Hierarchy = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, DirectoryReference.FromFile(TargetDesc.ProjectFile), TargetDesc.Platform);
			if (Hierarchy.TryGetValue(Tag.ConfigSection.BuildConfiguration, Tag.ConfigKey.bAllowHotReloadFromIDE, out bool bAllowHotReloadFromIDE) 
				&& !bAllowHotReloadFromIDE)
			{
				return false;
			}

			if (!BuildConfiguration.bAllowHotReloadFromIDE)
			{
				return false;
			}

			// Check if we're using LiveCode instead
			ConfigHierarchy EditorPerProjectHierarchy = ConfigCache.ReadHierarchy(ConfigHierarchyType.EditorPerProjectUserSettings, DirectoryReference.FromFile(TargetDesc.ProjectFile), TargetDesc.Platform);
			
			if (EditorPerProjectHierarchy.GetBool("/Script/LiveCoding.LiveCodingSettings", "bEnabled", out bool bEnableLiveCode) && bEnableLiveCode)
			{
				return false;
			}

			bool bIsRunning = false;

			// @todo ubtmake: Kind of cheating here to figure out if an editor target.  At this point we don't have access to the actual target description, and
			// this code must be able to execute before we create or load module rules DLLs so that hot reload can work with bUseUBTMakefiles
			if (TargetDesc.Name.EndsWith("Editor", StringComparison.OrdinalIgnoreCase))
			{
				string EditorBaseFileName = "Editor";
				if (TargetDesc.Configuration != TargetConfiguration.Development)
				{
					EditorBaseFileName = String.Format("{0}-{1}-{2}", EditorBaseFileName, TargetDesc.Platform, TargetDesc.Configuration);
				}

				FileReference EditorLocation;
				if (TargetDesc.Platform == BuildTargetPlatform.Win64)
				{
					EditorLocation = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Binaries, Tag.Directory.Win64, String.Format("{0}.exe", EditorBaseFileName));
				}
				else if (TargetDesc.Platform == BuildTargetPlatform.Mac)
				{
					EditorLocation = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Binaries, Tag.Directory.Mac, String.Format("{0}" + Tag.Directory.AppFolder + "{0}", EditorBaseFileName));
				}
				else if (TargetDesc.Platform == BuildTargetPlatform.Linux)
				{
					EditorLocation = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Binaries, Tag.Directory.Linux, EditorBaseFileName);
				}
				else
				{
					throw new BuildException("Unknown editor filename for this platform");
				}


                DirectoryReference EditorRunsDir = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated, Tag.Directory.EditorRuns);
				
                if (!DirectoryReference.Exists(EditorRunsDir))
                {
                    return false;
                }

                if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
                {
                    foreach (FileReference EditorInstanceFile in DirectoryReference.EnumerateFiles(EditorRunsDir))
                    {
                        if (!int.TryParse(EditorInstanceFile.GetFileName(), out int ProcessId))
                        {
                            FileReference.Delete(EditorInstanceFile);
                            continue;
                        }

                        Process RunningProcess;
                        try
                        {
                            RunningProcess = Process.GetProcessById(ProcessId);
                        }
                        catch
                        {
                            RunningProcess = null;
                        }

                        if (RunningProcess == null)
                        {
                            FileReference.Delete(EditorInstanceFile);
                            continue;
                        }

                        FileReference MainModuleFile;
                        try
                        {
                            MainModuleFile = new FileReference(RunningProcess.MainModule.FileName);
                        }
                        catch
                        {
                            MainModuleFile = null;
                        }

                        if (!bIsRunning && EditorLocation == MainModuleFile)
                        {
                            bIsRunning = true;
                        }
                    }
                }
                else if(BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
                {
                    FileInfo[] EditorRunsFiles = new DirectoryInfo(EditorRunsDir.FullName).GetFiles();
                    BuildHostPlatform.ProcessInfo[] Processes = BuildHostPlatform.Current.GetProcesses();

                    foreach (FileInfo File in EditorRunsFiles)
                    {
                        BuildHostPlatform.ProcessInfo Proc = null;
                        if (!int.TryParse(File.Name, out int PID) ||
                            (Proc = Processes.FirstOrDefault(P => P.PID == PID)) == default(BuildHostPlatform.ProcessInfo))
                        {
                            // Delete stale files (it may happen if editor crashes).
                            File.Delete();
                            continue;
                        }

                        // Don't break here to allow clean-up of other stale files.
                        if (!bIsRunning)
                        {
                            // Otherwise check if the path matches.
                            bIsRunning = new FileReference(Proc.ProcessBinaryName) == EditorLocation;
                        }
                    }
                }
            }

            return bIsRunning;
		}

		// Delete all temporary files created by previous hot reload invocations
		public static void DeleteTemporaryFiles(FileReference HotReloadStateFile)
		{
			if(FileReference.Exists(HotReloadStateFile))
			{
				// Try to load the state file. If it fails, we'll just warn and continue.
				HotReloadState State = null;
				try
				{
					State = HotReloadState.Load(HotReloadStateFile);
				}
				catch(Exception Ex)
				{
					Log.TraceWarning("Unable to read hot reload state file: {0}", HotReloadStateFile);
					Log.WriteException(Ex, null);
					return;
				}

				// Delete all the output files
				foreach(FileReference Location in State.TemporaryFiles.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
				{
					if(FileReference.Exists(Location))
					{
						try
						{
							FileReference.Delete(Location);
						}
						catch(Exception Ex)
						{
							throw new BuildException(Ex, "Unable to delete hot-reload file: {0}", Location);
						}
						Log.TraceInformation("Deleted hot-reload file: {0}", Location);
					}
				}

				// Delete the state file itself
				try
				{
					FileReference.Delete(HotReloadStateFile);
				}
				catch(Exception Ex)
				{
					throw new BuildException(Ex, "Unable to delete hot-reload state file: {0}", HotReloadStateFile);
				}
			}
		}

		// Apply a saved hot reload state to a makefile
		public static void ApplyState(HotReloadState HotReloadState, TargetMakefile Makefile)
		{
			// Update the action graph to produce these new files
			HotReload.PatchActionGraph(Makefile.Actions, HotReloadState.OriginalFileToHotReloadFile);

			// Update the module to output file mapping
			foreach(string HotReloadModuleName in Makefile.HotReloadModuleNames)
			{
				FileItem[] ModuleOutputItems = Makefile.ModuleNameToOutputItems[HotReloadModuleName];
				for(int Idx = 0; Idx < ModuleOutputItems.Length; ++Idx)
				{
					if (HotReloadState.OriginalFileToHotReloadFile.TryGetValue(ModuleOutputItems[Idx].FileDirectory, out FileReference NewLocation))
					{
						ModuleOutputItems[Idx] = FileItem.GetItemByFileReference(NewLocation);
					}
				}
			}
		}

		// Replaces a hot reload suffix in a filename.
		public static FileReference ReplaceSuffix(FileReference File, int DigitSuffix)
		{
			string FileName = File.GetFileName();

			// Find the end of the target and module name
			int HyphenIdx = FileName.IndexOf('-');
			if (HyphenIdx == -1)
			{
				throw new BuildException("");
			}

			int NameEndIdx = HyphenIdx + 1;

			while(NameEndIdx < FileName.Length && FileName[NameEndIdx] != '.' && FileName[NameEndIdx] != '-')
			{
				++NameEndIdx;
			}

			// Strip any existing suffix
			if(NameEndIdx + 1 < FileName.Length && Char.IsDigit(FileName[NameEndIdx + 1]))
			{
				int SuffixEndIdx = NameEndIdx + 2;

				while(SuffixEndIdx < FileName.Length && Char.IsDigit(FileName[SuffixEndIdx]))
				{
					++SuffixEndIdx;
				}

				if(SuffixEndIdx == FileName.Length || 
					FileName[SuffixEndIdx] == '-' || 
					FileName[SuffixEndIdx] == '.')
				{
					FileName = FileName.Substring(0, NameEndIdx) + FileName.Substring(SuffixEndIdx);
				}
			}

			string NewFileName = String.Format("{0}-{1:D4}{2}", FileName.Substring(0, NameEndIdx), DigitSuffix, FileName.Substring(NameEndIdx));

			return FileReference.Combine(File.Directory, NewFileName);
		}

		// Replaces a base filename within a string.
		// Ensures that the filename is not a substring of another longer string
		// (eg. replacing "Foo" will match "Foo.Bar" but not "FooBar" or "BarFooBar"). 
		static string ReplaceBaseFileName(string Text, string OldFileName, string NewFileName)
		{
			int StartIdx = 0;
			for(;;)
			{
				int Idx = Text.IndexOf(OldFileName, StartIdx, StringComparison.OrdinalIgnoreCase);
				if(Idx == -1)
				{
					break;
				}
				else if((Idx == 0 || !IsBaseFileNameCharacter(Text[Idx - 1])) && (Idx + OldFileName.Length == Text.Length || !IsBaseFileNameCharacter(Text[Idx + OldFileName.Length])))
				{
					Text = Text.Substring(0, Idx) + NewFileName + Text.Substring(Idx + OldFileName.Length);
					StartIdx = Idx + NewFileName.Length;
				}
				else
				{
					StartIdx = Idx + 1;
				}
			}
			return Text;
		}

		// Determines if a character should be treated as part of a base filename, when updating strings for hot reload
		static bool IsBaseFileNameCharacter(char Character)
		{
			return Char.IsLetterOrDigit(Character) || Character == '_';
		}

		// Patches a set of actions for use with live coding. The new action list will output object files to a different location.
		public static void PatchActionGraphForLiveCoding(IEnumerable<Action> Actions, Dictionary<FileReference, FileReference> OriginalFileToPatchedFile)
		{
			foreach (Action Action in Actions)
			{
				if(Action.Type == ActionType.Compile)
				{
					if(!Action.CommandPath.GetFileName().Equals(Tag.Binary.ClFilter, StringComparison.OrdinalIgnoreCase))
					{
						throw new BuildException("Unable to patch action graph - unexpected executable in compile action ({0})", Action.CommandPath);
					}

					List<string> Arguments = StringUtils.ParseArgumentList(Action.CommandArguments);

					// Find the index of the cl-filter argument delimiter
					int DelimiterIdx = Arguments.IndexOf("--");
					if(DelimiterIdx == -1)
					{
						throw new BuildException("Unable to patch action graph - missing '--' delimiter to cl-filter");
					}

					// Fix the dependencies path
					const string DependenciesPrefix = "-dependencies=";

					int DependenciesIdx = 0;
					for(;;DependenciesIdx++)
					{
						if(DependenciesIdx == DelimiterIdx)
						{
							throw new BuildException("Unable to patch action graph - missing '{0}' argument to cl-filter", DependenciesPrefix);
						}
						else if(Arguments[DependenciesIdx].StartsWith(DependenciesPrefix, StringComparison.OrdinalIgnoreCase))
						{
							break;
						}
					}

					FileReference OldDependenciesFile = new FileReference(Arguments[DependenciesIdx].Substring(DependenciesPrefix.Length));
					FileItem OldDependenciesFileItem = Action.ProducedItems.First(x => x.FileDirectory == OldDependenciesFile);
					Action.ProducedItems.Remove(OldDependenciesFileItem);

					FileReference NewDependenciesFile = OldDependenciesFile.ChangeExtension(".lc.response");
					FileItem NewDependenciesFileItem = FileItem.GetItemByFileReference(NewDependenciesFile);
					Action.ProducedItems.Add(NewDependenciesFileItem);

					Arguments[DependenciesIdx] = DependenciesPrefix + NewDependenciesFile.FullName;

					// Fix the response file
					int ResponseFileIdx = DelimiterIdx + 1;
					for (; ; ResponseFileIdx++)
					{
						if (ResponseFileIdx == Arguments.Count)
						{
							throw new BuildException("Unable to patch action graph - missing response file argument to cl-filter");
						}
						else if (Arguments[ResponseFileIdx].StartsWith("@", StringComparison.Ordinal))
						{
							break;
						}
					}

					FileReference OldResponseFile = new FileReference(Arguments[ResponseFileIdx].Substring(1));
					FileReference NewResponseFile = new FileReference(OldResponseFile.FullName + Tag.Ext.Lc);

					const string OutputFilePrefix = "/Fo";

					string[] ResponseLines = FileReference.ReadAllLines(OldResponseFile);
					for(int Idx = 0; Idx < ResponseLines.Length; ++Idx)
					{
						string ResponseLine = ResponseLines[Idx];
						if(ResponseLine.StartsWith(OutputFilePrefix, StringComparison.Ordinal))
						{
							FileReference OldOutputFile = new FileReference(ResponseLine.Substring(3).Trim('\"'));
							FileItem OldOutputFileItem = Action.ProducedItems.First(x => x.FileDirectory == OldOutputFile);
							Action.ProducedItems.Remove(OldOutputFileItem);

							FileReference NewOutputFile = OldOutputFile.ChangeExtension(Tag.Ext.LcObj);
							FileItem NewOutputFileItem = FileItem.GetItemByFileReference(NewOutputFile);
							Action.ProducedItems.Add(NewOutputFileItem);

							OriginalFileToPatchedFile[OldOutputFile] = NewOutputFile;

							ResponseLines[Idx] = OutputFilePrefix + "\"" + NewOutputFile.FullName + "\"";
							break;
						}
					}
					FileReference.WriteAllLines(NewResponseFile, ResponseLines);

					Arguments[ResponseFileIdx] = "@" + NewResponseFile.FullName;

					// Update the final arguments
					Action.CommandArguments = StringUtils.FormatCommandLine(Arguments);
				}
			}
		}

		// Patch the action graph for hot reloading, mapping files according to the given dictionary.
		public static void PatchActionGraph(IEnumerable<Action> Actions, Dictionary<FileReference, FileReference> OriginalFileToHotReloadFile)
		{
			// Gather all of the response files for link actions.  We're going to need to patch 'em up after we figure out new
			// names for all of the output files and import libraries
			List<string> ResponseFilePaths = new List<string>();

			// Same as Response files but for all of the link.sh files for link actions.
			// Only used on BuildHostPlatform Linux
			List<string> LinkScriptFilePaths = new List<string>();

			// Keep a map of the original file names and their new file names, so we can fix up response files after
			Dictionary<string, string> OriginalFileNameAndNewFileNameList_NoExtensions = new Dictionary<string, string>();

			// Finally, we'll keep track of any file items that we had to create counterparts for change file names, so we can fix those up too
			Dictionary<FileItem, FileItem> AffectedOriginalFileItemAndNewFileItemMap = new Dictionary<FileItem, FileItem>();

			foreach (Action Action in Actions.Where((Action) => Action.Type == ActionType.Link))
			{
				// Assume that the first produced item (with no extension) is our output file name
				if (!OriginalFileToHotReloadFile.TryGetValue(Action.ProducedItems[0].FileDirectory, out FileReference HotReloadFile))
				{
					continue;
				}

				string OriginalFileNameWithoutExtension = StringUtils.GetFilenameWithoutAnyExtensions(Action.ProducedItems[0].AbsolutePath);
				string NewFileNameWithoutExtension      = StringUtils.GetFilenameWithoutAnyExtensions(HotReloadFile.FullName);

				// Find the response file in the command line.  We'll need to make a copy of it with our new file name.
				string ResponseFileExtension  = ".response";
				int    ResponseExtensionIndex = Action.CommandArguments.IndexOf(ResponseFileExtension, StringComparison.InvariantCultureIgnoreCase);

				if (ResponseExtensionIndex != -1)
				{
					int ResponseFilePathIndex = Action.CommandArguments.LastIndexOf("@\"", ResponseExtensionIndex);
					if (ResponseFilePathIndex == -1)
					{
						throw new BuildException("Couldn't find response file path in action's command arguments when hot reloading");
					}

					string OriginalResponseFilePathWithoutExtension 
						= Action.CommandArguments.Substring(ResponseFilePathIndex + 2, ResponseExtensionIndex - (ResponseFilePathIndex + 2));
					string OriginalResponseFilePath = OriginalResponseFilePathWithoutExtension + ResponseFileExtension;

					string NewResponseFilePath = ReplaceBaseFileName(OriginalResponseFilePath, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);

					// Copy the old response file to the new path
					if(String.Compare(OriginalResponseFilePath, NewResponseFilePath, StringComparison.OrdinalIgnoreCase) != 0)
					{
						File.Copy(OriginalResponseFilePath, NewResponseFilePath, overwrite: true);
					}

					// Keep track of the new response file name.  We'll have to do some edits afterwards.
					ResponseFilePaths.Add(NewResponseFilePath);
				}

				// Find the *.link.sh file in the command line.
				// We'll need to make a copy of it with our new file name.
				// Only currently used on Linux
				if (BuildPlatform.IsPlatformInGroup(BuildHostPlatform.Current.Platform, BuildPlatformGroup.Unix))
				{
					string LinkScriptFileExtension = ".link.sh";
					int LinkScriptExtensionIndex   = Action.CommandArguments.IndexOf(LinkScriptFileExtension, StringComparison.InvariantCultureIgnoreCase);

					if (LinkScriptExtensionIndex != -1)
					{
						// We expect the script invocation to be quoted
						int LinkScriptFilePathIndex = Action.CommandArguments.LastIndexOf("\"", LinkScriptExtensionIndex);
						if (LinkScriptFilePathIndex == -1)
						{
							throw new BuildException("Couldn't find link script file path in action's command arguments when hot reloading. Is the path quoted?");
						}

						string OriginalLinkScriptFilePathWithoutExtension 
							= Action.CommandArguments.Substring(LinkScriptFilePathIndex + 1, LinkScriptExtensionIndex - (LinkScriptFilePathIndex + 1));
						string OriginalLinkScriptFilePath = OriginalLinkScriptFilePathWithoutExtension + LinkScriptFileExtension;

						string NewLinkScriptFilePath = ReplaceBaseFileName(OriginalLinkScriptFilePath, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);

						// Copy the old response file to the new path
						File.Copy(OriginalLinkScriptFilePath, NewLinkScriptFilePath, overwrite: true);

						// Keep track of the new response file name.  We'll have to do some edits afterwards.
						LinkScriptFilePaths.Add(NewLinkScriptFilePath);
					}

					// Update this action's list of prerequisite items too
					for (int ItemIndex = 0; ItemIndex < Action.PrerequisiteItems.Count; ++ItemIndex)
					{
						FileItem OriginalPrerequisiteItem = Action.PrerequisiteItems[ItemIndex];
						string NewPrerequisiteItemFilePath = ReplaceBaseFileName(OriginalPrerequisiteItem.AbsolutePath, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);

						if (OriginalPrerequisiteItem.AbsolutePath != NewPrerequisiteItemFilePath)
						{
							// OK, the prerequisite item's file name changed so we'll update it to point to our new file
							FileItem NewPrerequisiteItem = FileItem.GetItemByPath(NewPrerequisiteItemFilePath);
							Action.PrerequisiteItems[ItemIndex] = NewPrerequisiteItem;

							// Keep track of it so we can fix up dependencies in a second pass afterwards
							AffectedOriginalFileItemAndNewFileItemMap.Add(OriginalPrerequisiteItem, NewPrerequisiteItem);

							ResponseExtensionIndex = OriginalPrerequisiteItem.AbsolutePath.IndexOf(ResponseFileExtension, StringComparison.InvariantCultureIgnoreCase);
							if (ResponseExtensionIndex != -1)
							{
								string OriginalResponseFilePathWithoutExtension = OriginalPrerequisiteItem.AbsolutePath.Substring(0, ResponseExtensionIndex);
								string OriginalResponseFilePath = OriginalResponseFilePathWithoutExtension + ResponseFileExtension;

								string NewResponseFilePath = ReplaceBaseFileName(OriginalResponseFilePath, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);

								// Copy the old response file to the new path
								File.Copy(OriginalResponseFilePath, NewResponseFilePath, overwrite: true);

								// Keep track of the new response file name.  We'll have to do some edits afterwards.
								ResponseFilePaths.Add(NewResponseFilePath);
							}
						}
					}
				}

				// Update this action's list of produced items too
				for (int ItemIndex = 0; ItemIndex < Action.ProducedItems.Count; ++ItemIndex)
				{
					FileItem OriginalProducedItem = Action.ProducedItems[ItemIndex];

					string NewProducedItemFilePath = ReplaceBaseFileName(OriginalProducedItem.AbsolutePath, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);
					if (OriginalProducedItem.AbsolutePath != NewProducedItemFilePath)
					{
						// OK, the produced item's file name changed so we'll update it to point to our new file
						FileItem NewProducedItem = FileItem.GetItemByPath(NewProducedItemFilePath);
						Action.ProducedItems[ItemIndex] = NewProducedItem;

						// Keep track of it so we can fix up dependencies in a second pass afterwards
						AffectedOriginalFileItemAndNewFileItemMap.Add(OriginalProducedItem, NewProducedItem);
					}
				}

				// Fix up the list of items to delete too
				for(int Idx = 0; Idx < Action.DeleteItems.Count; ++Idx)
				{
					if (AffectedOriginalFileItemAndNewFileItemMap.TryGetValue(Action.DeleteItems[Idx], out FileItem NewItem))
					{
						Action.DeleteItems[Idx] = NewItem;
					}
				}

				// The status description of the item has the file name, so we'll update it too
				Action.StatusDescription = ReplaceBaseFileName(Action.StatusDescription, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);

				// Keep track of the file names, so we can fix up response files afterwards.
				if(!OriginalFileNameAndNewFileNameList_NoExtensions.ContainsKey(OriginalFileNameWithoutExtension))
				{
					OriginalFileNameAndNewFileNameList_NoExtensions[OriginalFileNameWithoutExtension] = NewFileNameWithoutExtension;
				}
				else if(OriginalFileNameAndNewFileNameList_NoExtensions[OriginalFileNameWithoutExtension] != NewFileNameWithoutExtension)
				{
					throw new BuildException("Unexpected conflict in renaming files; {0} maps to {1} and {2}", OriginalFileNameWithoutExtension, OriginalFileNameAndNewFileNameList_NoExtensions[OriginalFileNameWithoutExtension], NewFileNameWithoutExtension);
				}
			}

			// Do another pass and update any actions that depended on the original file names that we changed
			foreach (Action Action in Actions)
			{
				for (int ItemIndex = 0; ItemIndex < Action.PrerequisiteItems.Count; ++ItemIndex)
				{
					FileItem OriginalFileItem = Action.PrerequisiteItems[ItemIndex];

					if (AffectedOriginalFileItemAndNewFileItemMap.TryGetValue(OriginalFileItem, out FileItem NewFileItem))
					{
						// OK, looks like we need to replace this file item because we've renamed the file
						Action.PrerequisiteItems[ItemIndex] = NewFileItem;
					}
				}
			}


			if (0 < OriginalFileNameAndNewFileNameList_NoExtensions.Count)
			{
				// Update all the paths in link actions
				foreach (Action Action in Actions.Where((Action) => Action.Type == ActionType.Link))
				{
					foreach (KeyValuePair<string, string> FileNameTuple in OriginalFileNameAndNewFileNameList_NoExtensions)
					{
						string OriginalFileNameWithoutExtension = FileNameTuple.Key;
						string NewFileNameWithoutExtension      = FileNameTuple.Value;

						Action.CommandArguments = ReplaceBaseFileName(Action.CommandArguments, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);
					}
				}

				foreach (string ResponseFilePath in ResponseFilePaths)
				{
					// Load the file up
					string FileContents = StringUtils.ReadAllText(ResponseFilePath);

					// Replace all of the old file names with new ones
					foreach (KeyValuePair<string, string> FileNameTuple in OriginalFileNameAndNewFileNameList_NoExtensions)
					{
						string OriginalFileNameWithoutExtension = FileNameTuple.Key;
						string NewFileNameWithoutExtension      = FileNameTuple.Value;

						FileContents = ReplaceBaseFileName(FileContents, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);
					}

					// Overwrite the original file
					File.WriteAllText(ResponseFilePath, FileContents, new System.Text.UTF8Encoding(false));
				}

				if (BuildPlatform.IsPlatformInGroup(BuildHostPlatform.Current.Platform, BuildPlatformGroup.Unix))
				{
					foreach (string LinkScriptFilePath in LinkScriptFilePaths)
					{
						// Load the file up
						string FileContents = StringUtils.ReadAllText(LinkScriptFilePath);

						// Replace all of the old file names with new ones
						foreach (KeyValuePair<string, string> FileNameTuple in OriginalFileNameAndNewFileNameList_NoExtensions)
						{
							string OriginalFileNameWithoutExtension = FileNameTuple.Key;
							string NewFileNameWithoutExtension = FileNameTuple.Value;

							FileContents = ReplaceBaseFileName(FileContents, OriginalFileNameWithoutExtension, NewFileNameWithoutExtension);
						}

						// Overwrite the original file
						File.WriteAllText(LinkScriptFilePath, FileContents, new System.Text.UTF8Encoding(false));
					}
				}
			}

			// Update the action that writes out the module manifests
			foreach(Action Action in Actions)
			{
				if(Action.Type == ActionType.WriteMetadata)
				{
					string Arguments = Action.CommandArguments;

					// Find the argument for the metadata file
					const string InputArgument = "-Input=";

					int InputIdx = Arguments.IndexOf(InputArgument);
					if(InputIdx == -1)
					{
						throw new Exception("Missing -Input= argument to WriteMetadata command when patching action graph.");
					}

					int FileNameIdx = InputIdx + InputArgument.Length;
					if(Arguments[FileNameIdx] == '\"')
					{
						++FileNameIdx;
					}

					int FileNameEndIdx = FileNameIdx;
					while(FileNameEndIdx < Arguments.Length && 
						(Arguments[FileNameEndIdx] != ' ' || Arguments[FileNameIdx - 1] == '\"') && 
						Arguments[FileNameEndIdx] != '\"')
					{
						++FileNameEndIdx;
					}

					// Read the metadata file
					FileReference TargetInfoFile = new FileReference(Arguments.Substring(FileNameIdx, FileNameEndIdx - FileNameIdx));
					if(!FileReference.Exists(TargetInfoFile))
					{
						throw new Exception(String.Format("Unable to find metadata file to patch action graph ({0})", TargetInfoFile));
					}

					// Update the module names
					WriteMetadataTargetInfo TargetInfo = BinaryFormatterUtils.Load<WriteMetadataTargetInfo>(TargetInfoFile);
					foreach (KeyValuePair<FileReference, ModuleManifest> FileNameToVersionManifest in TargetInfo.FileToManifest)
					{
						KeyValuePair<string, string>[] ManifestEntries = FileNameToVersionManifest.Value.ModuleNameToFileName.ToArray();
						foreach (KeyValuePair<string, string> Manifest in ManifestEntries)
						{
							FileReference OriginalFile = FileReference.Combine(FileNameToVersionManifest.Key.Directory, Manifest.Value);

							if (OriginalFileToHotReloadFile.TryGetValue(OriginalFile, out FileReference HotReloadFile))
							{
								FileNameToVersionManifest.Value.ModuleNameToFileName[Manifest.Key] = HotReloadFile.GetFileName();
							}
						}
					}

					// Write the hot-reload metadata file and update the argument list
					FileReference HotReloadTargetInfoFile = FileReference.Combine(TargetInfoFile.Directory, "Metadata-HotReload.dat");
					BinaryFormatterUtils.SaveIfDifferent(HotReloadTargetInfoFile, TargetInfo);

					Action.PrerequisiteItems.RemoveAll(x => x.FileDirectory == TargetInfoFile);
					Action.PrerequisiteItems.Add(FileItem.GetItemByFileReference(HotReloadTargetInfoFile));

					Action.CommandArguments = Arguments.Substring(0, FileNameIdx) + HotReloadTargetInfoFile + Arguments.Substring(FileNameEndIdx);
				}
			}
		}

		// Patches a set of actions to use a specific list of suffixes for each module name
		public static void PatchActionGraphWithNames(List<Action> PrerequisiteActions, Dictionary<string, int> ModuleNameToSuffix, TargetMakefile Makefile)
		{
			if(0 < ModuleNameToSuffix.Count)
			{
				Dictionary<FileReference, FileReference> OldLocationToNewLocation = new Dictionary<FileReference, FileReference>();
				foreach(string HotReloadModuleName in Makefile.HotReloadModuleNames)
				{
					if (ModuleNameToSuffix.TryGetValue(HotReloadModuleName, out int ModuleSuffix))
					{
						FileItem[] ModuleOutputItems = Makefile.ModuleNameToOutputItems[HotReloadModuleName];
						foreach (FileItem ModuleOutputItem in ModuleOutputItems)
						{
							FileReference OldLocation = ModuleOutputItem.FileDirectory;
							FileReference NewLocation = HotReload.ReplaceSuffix(OldLocation, ModuleSuffix);
							OldLocationToNewLocation[OldLocation] = NewLocation;
						}
					}
				}
				HotReload.PatchActionGraph(PrerequisiteActions, OldLocationToNewLocation);
			}
		}
		
		// Writes a manifest containing all the information needed to create a live coding patch
		public static void WriteLiveCodingManifest
		(
            FileReference ManifestFileToWrite,
            List<Action> Actions,
            Dictionary<FileReference, FileReference> OriginalFileToPatchedFile
		)
		{
			// Find all the output object files
			HashSet<FileItem> ObjectFiles = new HashSet<FileItem>();
			foreach(Action Action in Actions)
			{
				if(Action.Type == ActionType.Compile)
				{
					ObjectFiles.UnionWith(Action.ProducedItems.Where(x => x.HasExtension(Tag.Ext.Obj)));
				}
			}

			// Write the output manifest
			using (JsonWriter Writer = new JsonWriter(ManifestFileToWrite))
			{
				Writer.WriteObjectStart();

				Action LinkAction = Actions.FirstOrDefault(x => x.Type == ActionType.Link && 
				x.ProducedItems.Any(y => y.HasExtension(Tag.Ext.Exe) || y.HasExtension(Tag.Ext.Dll)));
				
				if(LinkAction != null)
				{
					FileReference LinkerPath = LinkAction.CommandPath;

					if(0 == String.Compare(LinkerPath.GetFileName(), Tag.Binary.LinkFilter, StringComparison.OrdinalIgnoreCase))
					{
						string[] Arguments = CommandLineArguments.Split(LinkAction.CommandArguments);
						for(int Idx = 0; Idx + 1 < Arguments.Length; Idx++)
						{
							if(Arguments[Idx] == "--")
							{
								LinkerPath = new FileReference(Arguments[Idx + 1]);
								break;
							}
						}
					}

					Writer.WriteValue("LinkerPath", LinkerPath.FullName);
				}

				Writer.WriteObjectStart("LinkerEnvironment");

				foreach (System.Collections.DictionaryEntry Entry in Environment.GetEnvironmentVariables())
				{
					Writer.WriteValue(Entry.Key.ToString(), Entry.Value.ToString());
				}

				Writer.WriteObjectEnd();

				Writer.WriteArrayStart("Modules");
				foreach(Action Action in Actions)
				{
					if(Action.Type == ActionType.Link)
					{
						FileItem OutputFile = Action.ProducedItems.FirstOrDefault(x => x.HasExtension(Tag.Ext.Exe) || x.HasExtension(Tag.Ext.Dll));
						if(OutputFile != null && Action.PrerequisiteItems.Any(x => OriginalFileToPatchedFile.ContainsKey(x.FileDirectory)))
						{
							Writer.WriteObjectStart();
							Writer.WriteValue("Output", OutputFile.FileDirectory.FullName);

							Writer.WriteArrayStart("Inputs");
							foreach(FileItem InputFile in Action.PrerequisiteItems)
							{
								if (OriginalFileToPatchedFile.TryGetValue(InputFile.FileDirectory, out FileReference PatchedFile))
								{
									Writer.WriteValue(PatchedFile.FullName);
								}
							}
							Writer.WriteArrayEnd();

							Writer.WriteObjectEnd();
						}
					}
				}

				Writer.WriteArrayEnd();
				Writer.WriteObjectEnd();
			}
		}
	}
}
