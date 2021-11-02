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
	// Options controlling how a target is built
	[Flags]
	public enum BuildOptions
	{
		None            = 0, // Default options
		SkipBuild       = 1, // Don't build anything, just do target setup and terminate
		XGEExport       = 2, // Just output a list of XGE actions; don't build anything
		NoEngineChanges = 4, // Fail if any engine files would be modified by the build
	}

	// Builds a target
	[ToolMode("Build", ToolModeOptions.XmlConfig | 
		ToolModeOptions.BuildPlatforms | 
		ToolModeOptions.SingleInstance | 
		ToolModeOptions.StartPrefetchingEngine | 
		ToolModeOptions.ShowExecutionTime)]
    internal sealed class BuildMode : ToolMode
	{
		// Specifies the file to use for logging.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public string BaseLogFileName;

		// Whether to skip checking for files identified by the junk manifest.
		[XMLConfigFile]
		[CommandLine("-IgnoreJunk")]
		public bool bIgnoreJunk = false;

		// Skip building; just do setup and terminate.
		[CommandLine("-SkipBuild")]
		public bool bSkipBuild = false;

		// Whether we should just export the XGE XML and pretend it succeeded
		[CommandLine("-XGEExport")]
		public bool bXGEExport = false;

		// Do not allow any engine files to be output (used by compile on startup functionality)
		[CommandLine("-NoEngineChanges")]
		public bool bNoEngineChanges = false;

		// Whether we should just export the outdated actions list
		[CommandLine("-WriteOutdatedActions=")]
		public FileReference WriteOutdatedActionsFile = null;

		// Main entry point
		// <returns>One of the values of ECompilationResult</returns>
		// -> From GenerateProjectFiles.bat Arguments : -ProjectFiles
		public override int Execute(CommandLineArguments Arguments)
		{
			Debugger.Break();
			// System.Environment.Exit(1);

			Arguments.ApplyTo(this);

			// Initialize the log system, buffering the output until we can create the log file
			StartupTraceListener StartupListener = new StartupTraceListener();
			Trace.Listeners.Add(StartupListener);

			// Write the command line
			Log.TraceLog("Command line: {0}", Environment.CommandLine);

			// Grab the environment.
			BuildTool.InitialEnvironment = Environment.GetEnvironmentVariables();
			if (BuildTool.InitialEnvironment.Count < 1)
			{
				throw new BuildException("Environment could not be read");
			}

			// Read the XML configuration files
			// insert to HashMap all Inheritances(Base Class Types)
			XMLConfig.ApplyTo(this);

			// Fixup the log path if it wasn't overridden by a config file
			if (BaseLogFileName == null)
			{
				BaseLogFileName = FileReference.Combine(BuildTool.EngineProgramSavedDirectory, "BuildTool", "Log.txt").FullName;
			}

			// Create the log file, and flush the startup listener to it
			if (!Arguments.HasOption("-NoLog") && !Log.HasFileWriter())
			{
				FileReference LogFile = new FileReference(BaseLogFileName);
				foreach(string LogSuffix in Arguments.GetValues("-LogSuffix="))
				{
					LogFile = LogFile.ChangeExtension(null) + "_" + LogSuffix + LogFile.GetExtension();
				}

				TextWriterTraceListener LogTraceListener = Log.AddFileWriter("DefaultLogTraceListener", LogFile);
				StartupListener.CopyTo(LogTraceListener);
			}
			Trace.Listeners.Remove(StartupListener);

			// Create the build configuration object, and read the settings
			BuildConfiguration BuildConfiguration = new BuildConfiguration();
			XMLConfig.ApplyTo(BuildConfiguration);
			Arguments.ApplyTo(BuildConfiguration);

			// Check the root path length isn't too long
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 && BuildConfiguration.MaxRootPathLength < BuildTool.RootDirectory.FullName.Length)
			{
				Log.TraceWarning("Running from a path with a long directory name (\"{0}\" = {1} characters). Root paths shorter than {2} characters are recommended to avoid exceeding maximum path lengths on Windows.", BuildTool.RootDirectory, BuildTool.RootDirectory.FullName.Length, BuildConfiguration.MaxRootPathLength);
			}

			// Parse and build the targets
			try
			{
				// Parse all the target descriptors
				List<BuildTargetDescriptor> TargetDescriptors = BuildTargetDescriptor.ParseCommandLine(Arguments, BuildConfiguration.bUsePrecompiled, BuildConfiguration.bSkipRulesCompile);
				
				// Hack for single file compile;
				// don't build the ShaderCompileWorker target that's added to the command line for generated project files
				if(2 <= TargetDescriptors.Count)
				{
					TargetDescriptors.RemoveAll(x => (x.Name == Tag.Module.Engine.ShaderCompileWorker || x.Name == Tag.Module.Engine.LiveCodingConsole) 
					&& x.SingleFileToCompile != null);
				}

				// Handle remote builds
				for(int Idx = 0; Idx < TargetDescriptors.Count; ++Idx)
				{
					BuildTargetDescriptor TargetDesc = TargetDescriptors[Idx];
					if(RemoteMac.CanRemoteExecutorSupportsPlatform(TargetDesc.Platform))
					{
						FileReference BaseLogFile   = Log.OutputFile ?? new FileReference(BaseLogFileName);
						FileReference RemoteLogFile = FileReference.Combine(BaseLogFile.Directory, BaseLogFile.GetFileNameWithoutExtension() + "_Remote.txt");

						RemoteMac RemoteMac = new RemoteMac(TargetDesc.ProjectFile);

						if(!RemoteMac.Build(TargetDesc, RemoteLogFile))
						{
							return (int)CompilationResult.Unknown;
						}

						TargetDescriptors.RemoveAt(Idx--);
					}
				}

				// Handle local builds
				// TargetDecriptors = > [0] = {DefaultGame Win64 Debug}
				if (0 < TargetDescriptors.Count)
				{
					// Get a set of all the project directories
					HashSet<DirectoryReference> ProjectDirs = new HashSet<DirectoryReference>();
					foreach(BuildTargetDescriptor TargetDesc in TargetDescriptors)
					{
						if(TargetDesc.ProjectFile != null)
						{
							DirectoryReference ProjectDirectory = TargetDesc.ProjectFile.Directory;
							FileMetadataPrefetch.QueueProjectDirectory(ProjectDirectory);
							ProjectDirs.Add(ProjectDirectory);
						}
					}

					// Get all the build options
					BuildOptions Options = BuildOptions.None;
					if(bSkipBuild)
					{
						Options |= BuildOptions.SkipBuild;
					}
					if(bXGEExport)
					{
						Options |= BuildOptions.XGEExport;
					}
					if(bNoEngineChanges)
					{
						Options |= BuildOptions.NoEngineChanges;
					}

					// Create the working set provider
					using (ISourceFileWorkingSet WorkingSet = SourceFileWorkingSet.Create(BuildTool.RootDirectory, ProjectDirs))
					{
						// API_Define
						Build(TargetDescriptors, BuildConfiguration, WorkingSet, Options, WriteOutdatedActionsFile);
					}
				} // End if(0 < TargetDescriptors.Count)
			}
			finally
			{
				// Save all the caches
				SourceFileMetadataCache.SaveAll();
				CppDependencyCache.SaveAll();
			}
			return 0;
		}

		
		// Build a list of targets
		public static void Build
		(
			List<BuildTargetDescriptor> TargetDescriptors, 
			BuildConfiguration          BuildConfiguration, 
			ISourceFileWorkingSet       WorkingSet, 
			BuildOptions                InBuildOptions, 
			FileReference               FileToWriteOutdatedAction
		)
		{
			// Create a makefile for each target
			TargetMakefile[] Makefiles = new TargetMakefile[TargetDescriptors.Count];
			// Target -> [0] = {DefaultGame Win64 Debug}
			for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; ++TargetIdx)
			{
				Makefiles[TargetIdx] = CreateMakefile(BuildConfiguration, TargetDescriptors[TargetIdx], WorkingSet);
			}

			// Export the actions for each target
			for (int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; ++TargetIdx)
			{
				BuildTargetDescriptor TargetDescriptor = TargetDescriptors[TargetIdx];
				foreach(FileReference WriteActionFile in TargetDescriptor.WriteActionFiles)
				{
					Log.TraceInformation("Writing actions to {0}", WriteActionFile);
					ActionGraph.ExportJson(Makefiles[TargetIdx].Actions, WriteActionFile);
				}
			}

			// Execute the build
			if ((InBuildOptions & BuildOptions.SkipBuild) == 0)
			{
				// Make sure that none of the actions conflict with any other (producing output files differently, etc...)
				ActionGraph.CheckForConflicts(Makefiles.SelectMany(x => x.Actions));

				// Check we don't exceed the nominal max path length
				ActionGraph.CheckPathLengths(BuildConfiguration, Makefiles.SelectMany(x => x.Actions));
				
				// Find all the actions to be executed
				HashSet<Action>[] ActionsToExecute = new HashSet<Action>[TargetDescriptors.Count];

				for(int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; ++TargetIdx)
				{
					ActionsToExecute[TargetIdx] = GetActionsForTarget(BuildConfiguration, TargetDescriptors[TargetIdx], Makefiles[TargetIdx]);
				}

				// If there are multiple targets being built, merge the actions together
				List<Action> MergedActionsToExecute;
				if(TargetDescriptors.Count == 1)
				{
					MergedActionsToExecute = new List<Action>(ActionsToExecute[0]);
				}
				else
				{
					MergedActionsToExecute = MergeActionGraphs(TargetDescriptors, ActionsToExecute);
				}

				// Link all the actions together
				ActionGraph.Link(MergedActionsToExecute);

				// Make sure we're not modifying any engine files
				if (0 != (InBuildOptions & BuildOptions.NoEngineChanges))
				{
					List<FileItem> EngineChanges = MergedActionsToExecute.SelectMany
						(x => x.ProducedItems).Where(x => x.FileDirectory.IsUnderDirectory(BuildTool.EngineDirectory)).Distinct().OrderBy(x => x.FullName).ToList();
					
					if (0 < EngineChanges.Count)
					{
						StringBuilder Result = new StringBuilder("Building would modify the following engine files:\n");
						foreach (FileItem EngineChange in EngineChanges)
						{
							Result.AppendFormat("\n{0}", EngineChange.FullName);
						}
						Result.Append("\n\nPlease rebuild from an IDE instead.");
						Log.TraceError("{0}", Result.ToString());
						throw new CompilationResultException(CompilationResult.FailedDueToEngineChange);
					}
				}

				// Make sure the appropriate executor is selected
				foreach (BuildTargetDescriptor TargetDescriptor in TargetDescriptors)
				{
					BuildPlatform BuildPlatform      = BuildPlatform.GetBuildPlatform(TargetDescriptor.Platform);
					BuildConfiguration.bAllowXGE    &= BuildPlatform.CanUseXGE();
					BuildConfiguration.bAllowDistcc &= BuildPlatform.CanUseDistcc();
					BuildConfiguration.bAllowSNDBS  &= BuildPlatform.CanUseSNDBS();
				}

				// Delete produced items that are outdated.
				ActionGraph.DeleteOutdatedProducedItems(MergedActionsToExecute);

				// Save all the action histories now that files have been removed. We have to do this after deleting produced items to ensure that any
				// items created during the build don't have the wrong command line.
				ActionHistory.SaveAll();

				// Create directories for the outdated produced items.
				ActionGraph.CreateDirectoriesForProducedItems(MergedActionsToExecute);

				// Execute the actions
				if ((InBuildOptions & BuildOptions.XGEExport) != 0)
				{
					OutputToolchainInfo(TargetDescriptors, Makefiles);

					// Just export to an XML file
					XGE.ExportActions(MergedActionsToExecute);
				}
				else if(FileToWriteOutdatedAction != null)
				{
					OutputToolchainInfo(TargetDescriptors, Makefiles);

					// Write actions to an output file
					ActionGraph.ExportJson(MergedActionsToExecute, FileToWriteOutdatedAction);
				}
				else
				{
					// Execute the actions
					if(MergedActionsToExecute.Count == 0)
					{
						if (TargetDescriptors.Any(x => !x.bQuiet))
						{
							Log.TraceInformation((TargetDescriptors.Count == 1)? "Target is up to date" : "Targets are up to date");
						}
					}
					else
					{
						if (TargetDescriptors.Any(x => !x.bQuiet))
						{
							Log.TraceInformation("Building {0}...", StringUtils.FormatList(TargetDescriptors.Select(x => x.Name).Distinct()));
						}

						OutputToolchainInfo(TargetDescriptors, Makefiles);

						ActionGraph.ExecuteActions(BuildConfiguration, MergedActionsToExecute);
					}

					// Run the deployment steps
					foreach(TargetMakefile Makefile in Makefiles)
					{
						if (Makefile.bDeployAfterCompile)
						{
							TargetReceipt Receipt = TargetReceipt.Read(Makefile.ReceiptFile);
							Log.TraceInformation("Deploying {0} {1} {2}...", Receipt.TargetName, Receipt.Platform, Receipt.Configuration);

							BuildPlatform.GetBuildPlatform(Receipt.Platform).Deploy(Receipt);
						}
					}
				}
			} // End Execute the build
		}// End BuildMode.Build()

		// Outputs the toolchain used to build each target
		static void OutputToolchainInfo(List<BuildTargetDescriptor> TargetDescriptors, TargetMakefile[] Makefiles)
		{
			List<int> OutputIndices = new List<int>();
			for (int Idx = 0; Idx < TargetDescriptors.Count; ++Idx)
			{
				if (!TargetDescriptors[Idx].bQuiet)
				{
					OutputIndices.Add(Idx);
				}
			}

			if(OutputIndices.Count == 1)
			{
				foreach(string Diagnostic in Makefiles[OutputIndices[0]].Diagnostics)
				{
					Log.TraceInformation("{0}", Diagnostic);
				}
			}
			else
			{
				foreach(int OutputIndex in OutputIndices)
				{
					foreach(string Diagnostic in Makefiles[OutputIndex].Diagnostics)
					{
						Log.TraceInformation("{0}: {1}", TargetDescriptors[OutputIndex].Name, Diagnostic);
					}
				}
			}
		}

        // Creates the makefile for a target. 
        // If an existing, valid makefile already exists on disk, loads that instead.
        private static TargetMakefile CreateMakefile
		(
            BuildConfiguration    BuildConfiguration,
            BuildTargetDescriptor TargetDescriptor,
            ISourceFileWorkingSet WorkingSet
		)
		{
			// Get the path to the makefile for this target
			FileReference MakefileLocation = null;
			if(BuildConfiguration.bUseBuildToolMakefiles && TargetDescriptor.SingleFileToCompile == null)
			{
				MakefileLocation = TargetMakefile.GetLocation(TargetDescriptor.ProjectFile, TargetDescriptor.Name, TargetDescriptor.Platform, TargetDescriptor.Configuration);
			}

			TargetMakefile Makefile = null;

			if(MakefileLocation != null)
			{
                Makefile = TargetMakefile.Load
				(
                    MakefileLocation,
                    TargetDescriptor.ProjectFile,
                    TargetDescriptor.Platform,
                    TargetDescriptor.AdditionalArguments.GetArguments(),
                    out string OutReasonNotLoaded
				);

                if (Makefile == null)
                {
                    Log.TraceInformation("Creating makefile for {0} ({1})", TargetDescriptor.Name, OutReasonNotLoaded);
                }
            }

            // If we have a makefile, execute the pre-build steps and check it's still valid
            bool bHasRunPreBuildScripts = false;

			if(Makefile != null)
			{
				// Execute the scripts. We have to invalidate all cached file info after doing so, because we don't know what may have changed.
				if(0 < Makefile.PreBuildScripts.Length)
				{
					Utils.ExecuteCustomBuildSteps(Makefile.PreBuildScripts);
					DirectoryItem.ResetAllCachedInfo_SLOW();
				}

				// Don't run the pre-build steps again, even if we invalidate the makefile.
				bHasRunPreBuildScripts = true;

				// Check that the makefile is still valid
				if (!TargetMakefile.IsValidForSourceFiles(Makefile, TargetDescriptor.ProjectFile, TargetDescriptor.Platform, WorkingSet, out string Reason))
				{
					Log.TraceInformation("Invalidating makefile for {0} ({1})", TargetDescriptor.Name, Reason);
					Makefile = null;
				}
			}

			// If we couldn't load a makefile, create a new one
			if(Makefile == null)
			{
				// Create the target
				BuildTarget Target;

                Target = BuildTarget.CreateNewBuildTarget(TargetDescriptor, BuildConfiguration.bSkipRulesCompile, BuildConfiguration.bUsePrecompiled);

                // Create the pre-build scripts
                FileReference[] PreBuildScripts = Target.CreatePreBuildScripts();

				// Execute the pre-build scripts
				if(!bHasRunPreBuildScripts)
				{
					Utils.ExecuteCustomBuildSteps(PreBuildScripts);
				}

                // Build the target
                // API_Define
                Makefile = Target.Build(BuildConfiguration, WorkingSet, true, TargetDescriptor.SingleFileToCompile);

                // Save the pre-build scripts onto the makefile
                Makefile.PreBuildScripts = PreBuildScripts;

				// Save the additional command line arguments
				Makefile.AdditionalArguments = TargetDescriptor.AdditionalArguments.GetArguments();

				// Save the environment variables
				foreach (System.Collections.DictionaryEntry EnvironmentVariable in Environment.GetEnvironmentVariables())
				{
					Makefile.EnvironmentVariables.Add(Tuple.Create((string)EnvironmentVariable.Key, (string)EnvironmentVariable.Value));
				}

				// Save the makefile for next time
				if(MakefileLocation != null)
				{
					Makefile.Save(MakefileLocation);
				}
			}
			else
			{
				// Restore the environment variables
				foreach (Tuple<string, string> EnvironmentVariable in Makefile.EnvironmentVariables)
				{
					Environment.SetEnvironmentVariable(EnvironmentVariable.Item1, EnvironmentVariable.Item2);
				}

				// If the target needs UHT to be run, we'll go ahead and do that now
				if (0 < Makefile.UObjectModules.Count)
				{
					const bool bIsGatheringBuild = false;
					const bool bIsAssemblingBuild = true;

					FileReference ModuleInfoFileName = FileReference.Combine(Makefile.ProjectIntermediateDirectory, TargetDescriptor.Name + ".uhtmanifest");
					HeaderToolExecution.ExecuteHeaderToolIfNecessary
					(
						BuildConfiguration,
						TargetDescriptor.ProjectFile,
						TargetDescriptor.Name,
						Makefile.TargetType,
						Makefile.bHasProjectScriptPlugin,
						UObjectModules: Makefile.UObjectModules,
						ModuleInfoFileName: ModuleInfoFileName,
						bIsGatheringBuild: bIsGatheringBuild,
						bIsAssemblingBuild: bIsAssemblingBuild,
						WorkingSet: WorkingSet
					);
				}
			}
			return Makefile;
		}

		// Determine what needs to be built for a target 
		private static HashSet<Action> GetActionsForTarget
		(
			BuildConfiguration    BuildConfiguration, 
			BuildTargetDescriptor TargetDescriptorBeingBuilt, 
			TargetMakefile        TargetMakefileGenerated
		)
		{
			// Create the action graph
			ActionGraph.Link(TargetMakefileGenerated.Actions);

			// Get the hot-reload mode
			HotReloadMode HotReloadMode = TargetDescriptorBeingBuilt.HotReloadMode;

			if(HotReloadMode == HotReloadMode.Default)
			{
				if (0 < TargetDescriptorBeingBuilt.HotReloadModuleNameToSuffix.Count && 
					TargetDescriptorBeingBuilt.ForeignPlugin == null)
				{
					HotReloadMode = HotReloadMode.FromEditor;
				}
				else if (BuildConfiguration.bAllowHotReloadFromIDE && 
					    HotReload.ShouldDoHotReloadFromIDE(BuildConfiguration, TargetDescriptorBeingBuilt))
				{
					HotReloadMode = HotReloadMode.FromIDE;
				}
				else
				{
					HotReloadMode = HotReloadMode.Disabled;
				}
			}

			// Guard against a live coding session for this target being active
			if (BuildConfiguration.bAllowHotReloadFromIDE && 
				HotReloadMode != HotReloadMode.LiveCoding && 
				TargetDescriptorBeingBuilt.ForeignPlugin == null && 
				HotReload.IsLiveCodingSessionActive(TargetMakefileGenerated))
			{
				Debugger.Break();
				throw new BuildException("Unable to start regular build while Live Coding is active. Press Ctrl+Alt+F11 to trigger a Live Coding compile.");
			}

			// Get the root prerequisite actions
			List<Action> PrerequisiteActions = GatherPrerequisiteActions(TargetDescriptorBeingBuilt, TargetMakefileGenerated);

			// Get the path to the hot reload state file for this target
			FileReference HotReloadStateFile = global::BuildTool.HotReloadState.GetLocation(TargetDescriptorBeingBuilt.ProjectFile, TargetDescriptorBeingBuilt.Name, TargetDescriptorBeingBuilt.Platform, TargetDescriptorBeingBuilt.Configuration, TargetDescriptorBeingBuilt.Architecture);

			// Apply the previous hot reload state
			HotReloadState HotReloadState = null;
			if(HotReloadMode == HotReloadMode.Disabled)
			{
				// Make sure we're not doing a partial build from the editor (eg. compiling a new plugin)
				if(TargetDescriptorBeingBuilt.ForeignPlugin == null && TargetDescriptorBeingBuilt.SingleFileToCompile == null)
				{
					// Delete the previous state file
					HotReload.DeleteTemporaryFiles(HotReloadStateFile);
				}
			}
			else
			{
				// Read the previous state file and apply it to the action graph
				if(FileReference.Exists(HotReloadStateFile))
				{
					HotReloadState = HotReloadState.Load(HotReloadStateFile);
				}
				else
				{
					HotReloadState = new HotReloadState();
				}

				// Apply the old state to the makefile
				HotReload.ApplyState(HotReloadState, TargetMakefileGenerated);

				// If we want a specific suffix on any modules, apply that now. We'll track the outputs later, but the suffix has to be forced (and is always out of date if it doesn't exist).
				HotReload.PatchActionGraphWithNames(PrerequisiteActions, TargetDescriptorBeingBuilt.HotReloadModuleNameToSuffix, TargetMakefileGenerated);

				// Re-link the action graph
				ActionGraph.Link(PrerequisiteActions);
			}

			// Create the dependencies cache
			CppDependencyCache CppDependencies = CppDependencyCache.CreateHierarchy(TargetDescriptorBeingBuilt.ProjectFile, TargetDescriptorBeingBuilt.Name, TargetDescriptorBeingBuilt.Platform, TargetDescriptorBeingBuilt.Configuration, TargetMakefileGenerated.TargetType, TargetDescriptorBeingBuilt.Architecture);
			

			// Create the action history
			ActionHistory History = ActionHistory.CreateHierarchy(TargetDescriptorBeingBuilt.ProjectFile, TargetDescriptorBeingBuilt.Name, TargetDescriptorBeingBuilt.Platform, TargetMakefileGenerated.TargetType, TargetDescriptorBeingBuilt.Architecture);
			
			// Plan the actions to execute for the build. For single file compiles, always rebuild the source file regardless of whether it's out of date.
			HashSet<Action> OutTargetActionsToExecute;
			if (TargetDescriptorBeingBuilt.SingleFileToCompile == null)
			{
				OutTargetActionsToExecute = ActionGraph.GetActionsToExecute(TargetMakefileGenerated.Actions, PrerequisiteActions, CppDependencies, History, BuildConfiguration.bIgnoreOutdatedImportLibraries);
			}
			else
			{
				OutTargetActionsToExecute = new HashSet<Action>(PrerequisiteActions);
			}
			
			// Additional processing for hot reload
			if (HotReloadMode == HotReloadMode.LiveCoding)
			{
				// Make sure we're not overwriting any lazy-loaded modules
				if(TargetDescriptorBeingBuilt.LiveCodingModules != null)
				{
					// Read the list of modules that we're allowed to build
					string[] Lines = FileReference.ReadAllLines(TargetDescriptorBeingBuilt.LiveCodingModules);

					// Parse it out into a set of filenames
					HashSet<string> AllowedOutputFileNames = new HashSet<string>(FileReference.Comparer);
					foreach (string Line in Lines)
					{
						string TrimLine = Line.Trim();
						if (0 < TrimLine.Length)
						{
							AllowedOutputFileNames.Add(Path.GetFileName(TrimLine));
						}
					}

					// Find all the binaries that we're actually going to build
					HashSet<FileReference> WillBeBuiltFiles = new HashSet<FileReference>();
					foreach (Action Action in OutTargetActionsToExecute)
					{
						if (Action.Type == ActionType.Link)
						{
							WillBeBuiltFiles.UnionWith(Action.ProducedItems.Where(x => x.HasExtension(Tag.Ext.Exe) || x.HasExtension(Tag.Ext.Dll)).Select(x => x.FileDirectory));
						}
					}

					// Find all the files that will be built that aren't allowed
					List<FileReference> ProtectedOutputFiles = WillBeBuiltFiles.Where(x => !AllowedOutputFileNames.Contains(x.GetFileName())).ToList();
					if (0 < ProtectedOutputFiles.Count)
					{
						FileReference.WriteAllLines(new FileReference(TargetDescriptorBeingBuilt.LiveCodingModules.FullName + Tag.Ext.Out), ProtectedOutputFiles.Select(x => x.ToString()));
						foreach(FileReference ProtectedOutputFile in ProtectedOutputFiles)
						{
							Log.TraceInformation("Module {0} is not currently enabled for Live Coding", ProtectedOutputFile);
						}
						throw new CompilationResultException(CompilationResult.Canceled);
					}
				}

				// Filter the prerequisite actions down to just the compile actions, then recompute all the actions to execute
				PrerequisiteActions = new List<Action>(OutTargetActionsToExecute.Where(x => x.Type == ActionType.Compile));
				OutTargetActionsToExecute = ActionGraph.GetActionsToExecute(TargetMakefileGenerated.Actions, PrerequisiteActions, CppDependencies, History, BuildConfiguration.bIgnoreOutdatedImportLibraries);

				// Update the action graph with these new paths
				Dictionary<FileReference, FileReference> OriginalFileToPatchedFile = new Dictionary<FileReference, FileReference>();
				HotReload.PatchActionGraphForLiveCoding(PrerequisiteActions, OriginalFileToPatchedFile);

				// Get a new list of actions to execute now that the graph has been modified
				OutTargetActionsToExecute = ActionGraph.GetActionsToExecute(TargetMakefileGenerated.Actions, PrerequisiteActions, CppDependencies, History, BuildConfiguration.bIgnoreOutdatedImportLibraries);

				// Output the Live Coding manifest
				if(TargetDescriptorBeingBuilt.LiveCodingManifest != null)
				{
					HotReload.WriteLiveCodingManifest(TargetDescriptorBeingBuilt.LiveCodingManifest, TargetMakefileGenerated.Actions, OriginalFileToPatchedFile);
				}
			}
			else if (HotReloadMode == HotReloadMode.FromEditor || HotReloadMode == HotReloadMode.FromIDE)
			{
				// Patch action history for hot reload when running in assembler mode.  In assembler mode, the suffix on the output file will be
				// the same for every invocation on that makefile, but we need a new suffix each time.

				// For all the hot-reloadable modules that may need a unique suffix appended, build a mapping from output item to all the output items in that module. We can't 
				// apply a suffix to one without applying a suffix to all of them.
				Dictionary<FileItem, FileItem[]> HotReloadItemToDependentItems = new Dictionary<FileItem, FileItem[]>();
				foreach(string HotReloadModuleName in TargetMakefileGenerated.HotReloadModuleNames)
				{
					if (!TargetDescriptorBeingBuilt.HotReloadModuleNameToSuffix.TryGetValue(HotReloadModuleName, out int ModuleSuffix) || ModuleSuffix == -1)
					{
						if (TargetMakefileGenerated.ModuleNameToOutputItems.TryGetValue(HotReloadModuleName, out FileItem[] ModuleOutputItems))
						{
							foreach (FileItem ModuleOutputItem in ModuleOutputItems)
							{
								HotReloadItemToDependentItems[ModuleOutputItem] = ModuleOutputItems;
							}
						}
					}
				}

				// Expand the list of actions to execute to include everything that references any files with a new suffix. Unlike a regular build, we can't ignore
				// dependencies on import libraries under the assumption that a header would change if the API changes, because the dependency will be on a different DLL.
				HashSet<FileItem> FilesRequiringSuffix = new HashSet<FileItem>(OutTargetActionsToExecute.SelectMany(x => x.ProducedItems).Where(x => HotReloadItemToDependentItems.ContainsKey(x)));
				for(int LastNumFilesWithNewSuffix = 0; FilesRequiringSuffix.Count > LastNumFilesWithNewSuffix;)
				{
					LastNumFilesWithNewSuffix = FilesRequiringSuffix.Count;
					foreach(Action PrerequisiteAction in PrerequisiteActions)
					{
						if(!OutTargetActionsToExecute.Contains(PrerequisiteAction))
						{
							foreach(FileItem ProducedItem in PrerequisiteAction.ProducedItems)
							{
								if (HotReloadItemToDependentItems.TryGetValue(ProducedItem, out FileItem[] DependentItems))
								{
									OutTargetActionsToExecute.Add(PrerequisiteAction);
									FilesRequiringSuffix.UnionWith(DependentItems);
								}
							}
						}
					}
				}

				// Build a list of file mappings
				Dictionary<FileReference, FileReference> OldLocationToNewLocation = new Dictionary<FileReference, FileReference>();
				foreach(FileItem FileRequiringSuffix in FilesRequiringSuffix)
				{
					FileReference OldLocation = FileRequiringSuffix.FileDirectory;
					FileReference NewLocation = HotReload.ReplaceSuffix(OldLocation, HotReloadState.NextSuffix);
					OldLocationToNewLocation[OldLocation] = NewLocation;
				}

				// Update the action graph with these new paths
				HotReload.PatchActionGraph(PrerequisiteActions, OldLocationToNewLocation);

				// Get a new list of actions to execute now that the graph has been modified
				OutTargetActionsToExecute = ActionGraph.GetActionsToExecute(TargetMakefileGenerated.Actions, PrerequisiteActions, CppDependencies, History, BuildConfiguration.bIgnoreOutdatedImportLibraries);

				// Build a mapping of all file items to their original
				Dictionary<FileReference, FileReference> HotReloadFileToOriginalFile = new Dictionary<FileReference, FileReference>();
				foreach(KeyValuePair<FileReference, FileReference> Pair in HotReloadState.OriginalFileToHotReloadFile)
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
				foreach(Action Action in OutTargetActionsToExecute)
				{
					foreach(FileItem ProducedItem in Action.ProducedItems)
					{
						if (HotReloadFileToOriginalFile.TryGetValue(ProducedItem.FileDirectory, out FileReference OriginalLocation))
						{
							HotReloadState.OriginalFileToHotReloadFile[OriginalLocation] = ProducedItem.FileDirectory;
							HotReloadState.TemporaryFiles.Add(ProducedItem.FileDirectory);
						}
					}
				}

				// Increment the suffix for the next iteration
				if(0 < OutTargetActionsToExecute.Count)
				{
					++HotReloadState.NextSuffix;
				}

				// Save the new state
				HotReloadState.Save(HotReloadStateFile);

				// Prevent this target from deploying
				TargetMakefileGenerated.bDeployAfterCompile = false;
			}

			return OutTargetActionsToExecute;
		}

		// Determines all the actions that should be executed for a target (filtering for single module/file, etc..)
		static List<Action> GatherPrerequisiteActions(BuildTargetDescriptor TargetDescriptor, TargetMakefile Makefile)
		{
			List<Action> PrerequisiteActions;
			if(TargetDescriptor.SingleFileToCompile != null)
			{
				// If we're just compiling a single file, set the target items to be all the derived items
				FileItem FileToCompile = FileItem.GetItemByFileReference(TargetDescriptor.SingleFileToCompile);
				PrerequisiteActions = Makefile.Actions.Where(x => x.PrerequisiteItems.Contains(FileToCompile)).ToList();
			}
			else if(0 < TargetDescriptor.OnlyModuleNames.Count)
			{
				// Find the output items for this module
				HashSet<FileItem> ModuleOutputItems = new HashSet<FileItem>();
				foreach(string OnlyModuleName in TargetDescriptor.OnlyModuleNames)
				{
					if (!Makefile.ModuleNameToOutputItems.TryGetValue(OnlyModuleName, out FileItem[] OutputItemsForModule))
					{
						throw new BuildException("Unable to find output items for module '{0}'", OnlyModuleName);
					}
					ModuleOutputItems.UnionWith(OutputItemsForModule);
				}
				PrerequisiteActions = ActionGraph.GatherPrerequisiteActions(Makefile.Actions, ModuleOutputItems);
			}
			else
			{
				// Use all the output items from the target
				PrerequisiteActions = ActionGraph.GatherPrerequisiteActions(Makefile.Actions, new HashSet<FileItem>(Makefile.OutputItems));
			}
			return PrerequisiteActions;
		}

		// Merge action graphs for multiple targets into a single set of actions.
		// Sets group names on merged actions to indicate which target they belong to.
		static List<Action> MergeActionGraphs(List<BuildTargetDescriptor> TargetDescriptors, HashSet<Action>[] ActionsToExecute)
		{
			// Set of all output items. Knowing that there are no conflicts in produced items, we use this to eliminate duplicate actions.
			Dictionary<FileItem, Action> OutputItemToProducingAction = new Dictionary<FileItem, Action>();
			for(int TargetIdx = 0; TargetIdx < TargetDescriptors.Count; ++TargetIdx)
			{
				string GroupPrefix = String.Format("{0}-{1}-{2}", TargetDescriptors[TargetIdx].Name, TargetDescriptors[TargetIdx].Platform, TargetDescriptors[TargetIdx].Configuration);
				foreach(Action Action in ActionsToExecute[TargetIdx])
				{
					if (!OutputItemToProducingAction.TryGetValue(Action.ProducedItems[0], out Action ExistingAction))
					{
						OutputItemToProducingAction[Action.ProducedItems[0]] = Action;
						ExistingAction = Action;
					}
					ExistingAction.GroupNames.Add(GroupPrefix);
				}
			}
			return new List<Action>(OutputItemToProducingAction.Values);
		}
	}
}

