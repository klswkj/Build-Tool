using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// This enum has to be compatible with the one defined in the
	// \Engine\Source\Runtime\Core\Public\Misc\ComplilationResult.h 
	// to keep communication between HeaderTool, UBT and Editor compiling processes valid.
	enum CompilationResult
	{
		Succeeded               = 0, // Compilation succeeded
		Canceled                = 1, // Build was canceled, this is used on the engine side only
		UpToDate                = 2, // All targets were up to date, used only with -canskiplink
		CrashOrAssert           = 3, // The process has most likely crashed. This is what Engine returns in case of an assert
		FailedDueToHeaderChange = 4, // Compilation failed because generated code changed which was not supported
		FailedDueToEngineChange = 5, // Compilation failed due to the engine modules needing to be rebuilt
		OtherCompilationError   = 6, // Compilation failed due to compilation errors
		Unsupported             = 7, // Compilation is not supported in the current build
		Unknown                 = 8  // Unknown error
	}

	static class CompilationResultExtensions
	{
		public static bool Succeeded(this CompilationResult Result)
		{
			return Result == CompilationResult.Succeeded || Result == CompilationResult.UpToDate;
		}
	}

	class CompilationResultException : BuildException
	{
		public readonly CompilationResult Result;

		public CompilationResultException(CompilationResult Result)
			: base("Error: {0}", Result)
		{
			this.Result = Result;
		}
	}

	// Type of module. Mirrored in UHT as EBuildModuleType.
	// This should be sorted by the order in which we expect modules to be built.
	enum UHTModuleType
	{
		Program,
		EngineRuntime,
		EngineUncooked,
		EngineDeveloper,
		EngineEditor,
		EngineThirdParty,
		GameRuntime,
		GameUncooked,
		GameDeveloper,
		GameEditor,
		GameThirdParty,
	}
	static class UHTModuleTypeExtensions
	{
		public static bool IsProgramModule(this UHTModuleType ModuleType) 
			=> ModuleType == UHTModuleType.Program;
		public static bool IsEngineModule(this UHTModuleType ModuleType) 
			=> ModuleType == UHTModuleType.EngineRuntime   ||
			   ModuleType == UHTModuleType.EngineDeveloper || 
			   ModuleType == UHTModuleType.EngineEditor    || 
			   ModuleType == UHTModuleType.EngineThirdParty;
		public static bool IsGameModule(this UHTModuleType ModuleType) 
			=> ModuleType == UHTModuleType.GameRuntime   || 
			   ModuleType == UHTModuleType.GameDeveloper || 
			   ModuleType == UHTModuleType.GameEditor    || 
			   ModuleType == UHTModuleType.GameThirdParty;
		public static UHTModuleType? EngineModuleTypeFromHostType(ModuleHostType ModuleType)
		{
			switch (ModuleType)
			{
				case ModuleHostType.Program:
					return UHTModuleType.Program;

				case ModuleHostType.Runtime:
				case ModuleHostType.RuntimeNoCommandlet:
				case ModuleHostType.RuntimeAndProgram:
				case ModuleHostType.CookedOnly:
				case ModuleHostType.ServerOnly:
				case ModuleHostType.ClientOnly:
				case ModuleHostType.ClientOnlyNoCommandlet:
					return UHTModuleType.EngineRuntime;

				case ModuleHostType.Developer:
				case ModuleHostType.DeveloperTool:
					return UHTModuleType.EngineDeveloper;

				case ModuleHostType.Editor:
				case ModuleHostType.EditorNoCommandlet:
				case ModuleHostType.EditorAndProgram:
					return UHTModuleType.EngineEditor;

				case ModuleHostType.UncookedOnly:
					return UHTModuleType.EngineUncooked;
				default:
					return null;
			}
		}
		public static UHTModuleType? GameModuleTypeFromHostType(ModuleHostType ModuleType)
		{
			switch (ModuleType)
			{
				case ModuleHostType.Program:
					return UHTModuleType.Program;
				case ModuleHostType.Runtime:
				case ModuleHostType.RuntimeNoCommandlet:
				case ModuleHostType.RuntimeAndProgram:
				case ModuleHostType.CookedOnly:
				case ModuleHostType.ServerOnly:
				case ModuleHostType.ClientOnly:
				case ModuleHostType.ClientOnlyNoCommandlet:
					return UHTModuleType.GameRuntime;
				case ModuleHostType.Developer:
				case ModuleHostType.DeveloperTool:
					return UHTModuleType.GameDeveloper;
				case ModuleHostType.Editor:
				case ModuleHostType.EditorNoCommandlet:
				case ModuleHostType.EditorAndProgram:
					return UHTModuleType.GameEditor;
				case ModuleHostType.UncookedOnly:
					return UHTModuleType.GameUncooked;
				default:
					return null;
			}
		}
	}

	// Information about a module that needs to be passed to HeaderTool for code generation
	class HeaderToolModuleInfo
	{
		public string ModuleName;
		public string ModuleType;
		public FileReference ModuleRulesFile;        // Path to the module rules file
		public DirectoryItem GeneratedCodeDirectory; // Directory containing generated code
		public string GeneratedCPPFilenameBase;      // Base (i.e. extensionless) path+filename of the .gen files

		public DirectoryReference[] ModuleDirectories;     // Paths to all potential module source directories (with platform extension directories added in)
		public List<FileItem> PublicUObjectClassesHeaders; // Public UObject headers found in the Classes directory (legacy)
		public List<FileItem> PublicUObjectHeaders;        // Public headers with UObjects
		public List<FileItem> PrivateUObjectHeaders;       // Private headers with UObjects
		public EGeneratedCodeVersion GeneratedCodeVersion; // Version of code generated by UHT
		public bool bIsReadOnly;                           // Whether this module is read-only

		public HeaderToolModuleInfo
		(
			string                ModuleName, 
			FileReference         ModuleRulesFile, 
			DirectoryReference[]  ModuleDirectories, 
			UHTModuleType         ModuleType, 
			DirectoryItem         GeneratedCodeDirectory, 
			EGeneratedCodeVersion GeneratedCodeVersion, 
			bool                  bIsReadOnly
		)
		{
			this.ModuleName = ModuleName;
			this.ModuleRulesFile = ModuleRulesFile;
			this.ModuleDirectories = ModuleDirectories;
			this.ModuleType = ModuleType.ToString();
			this.PublicUObjectClassesHeaders = new List<FileItem>();
			this.PublicUObjectHeaders = new List<FileItem>();
			this.PrivateUObjectHeaders = new List<FileItem>();
			this.GeneratedCodeDirectory = GeneratedCodeDirectory;
			this.GeneratedCodeVersion = GeneratedCodeVersion;
			this.bIsReadOnly = bIsReadOnly;
		}

		// # Keep Sync with read between write.

		public HeaderToolModuleInfo(BinaryArchiveReader Reader)
		{
			ModuleName                  = Reader.ReadString();
			ModuleRulesFile             = Reader.ReadFileReference();
			ModuleDirectories           = Reader.ReadArray<DirectoryReference>(Reader.ReadDirectoryReference);
			ModuleType                  = Reader.ReadString();
			PublicUObjectClassesHeaders = Reader.ReadList(() => Reader.ReadFileItem());
			PublicUObjectHeaders        = Reader.ReadList(() => Reader.ReadFileItem());
			PrivateUObjectHeaders       = Reader.ReadList(() => Reader.ReadFileItem());
			GeneratedCPPFilenameBase    = Reader.ReadString();
			GeneratedCodeDirectory      = Reader.ReadDirectoryItem();
			GeneratedCodeVersion        = (EGeneratedCodeVersion)Reader.ReadInt();
			bIsReadOnly                 = Reader.ReadBool();
		}

		public void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteString(ModuleName);
			Writer.WriteFileReference(ModuleRulesFile);
			Writer.WriteArray<DirectoryReference>(ModuleDirectories, Writer.WriteDirectoryReference);
			Writer.WriteString(ModuleType);
			Writer.WriteList(PublicUObjectClassesHeaders, Item => Writer.WriteFileItem(Item));
			Writer.WriteList(PublicUObjectHeaders, Item => Writer.WriteFileItem(Item));
			Writer.WriteList(PrivateUObjectHeaders, Item => Writer.WriteFileItem(Item));
			Writer.WriteString(GeneratedCPPFilenameBase);
			Writer.WriteDirectoryItem(GeneratedCodeDirectory);
			Writer.WriteInt((int)GeneratedCodeVersion);
			Writer.WriteBool(bIsReadOnly);
		}

		public override string ToString() => ModuleName;
	}

	
	// This MUST be kept in sync with
	// EGeneratedBodyVersion enum and ToGeneratedBodyVersion function in Header tool defined in GeneratedCodeVersion.h.
	public enum EGeneratedCodeVersion
	{
		None,
		V1,
		V2,
		VLatest = V2
	};

	internal struct UHTManifest
	{
		public sealed class Module
		{
			public string Name;
			public string ModuleType;
			public string BaseDirectory;
			public string IncludeBase;     // The include path which all UHT-generated includes should be relative to
			public string OutputDirectory;
			public string GeneratedCPPFilenameBase;
			public List<string> ClassesHeaders;
			public List<string> PublicHeaders;
			public List<string> PrivateHeaders;
			public bool SaveExportedHeaders;
			public EGeneratedCodeVersion UHTGeneratedCodeVersion;

			public Module(HeaderToolModuleInfo Info)
			{
				Name                     = Info.ModuleName;
				ModuleType               = Info.ModuleType;
				BaseDirectory            = Info.ModuleDirectories[0].FullName;
				IncludeBase              = Info.ModuleDirectories[0].ParentDirectory.FullName;
				OutputDirectory          = Path.GetDirectoryName(Info.GeneratedCPPFilenameBase);
				GeneratedCPPFilenameBase = Info.GeneratedCPPFilenameBase;
				ClassesHeaders           = Info.PublicUObjectClassesHeaders.Select((Header) => Header.AbsolutePath).ToList();
				PublicHeaders            = Info.PublicUObjectHeaders       .Select((Header) => Header.AbsolutePath).ToList();
				PrivateHeaders           = Info.PrivateUObjectHeaders      .Select((Header) => Header.AbsolutePath).ToList();
				SaveExportedHeaders      = !Info.bIsReadOnly;
				UHTGeneratedCodeVersion  = Info.GeneratedCodeVersion;
			}

            public override string ToString() => Name;
        }

		public bool IsGameTarget;               // True if the current target is a game target
		public string RootLocalPath;            // The engine path on the local machine
		public string TargetName;               // Name of the target currently being compiled
		public string ExternalDependenciesFile; // File to contain additional dependencies that the generated code depends on
		public List<UHTManifest.Module> Modules;

		public UHTManifest(string InTargetName, TargetType InTargetType, string InRootLocalPath, string InExternalDependenciesFile, List<Module> InModules)
		{
			IsGameTarget             = (InTargetType != TargetType.Program);
			RootLocalPath            = InRootLocalPath;
			TargetName               = InTargetName;
			ExternalDependenciesFile = InExternalDependenciesFile;
			Modules                  = InModules;
		}
	}

	class UHTModuleHeaderInfo
	{
		public DirectoryItem SourceFolder;
		public List<FileItem> HeaderFiles;
		public bool bUsePrecompiled;

		public UHTModuleHeaderInfo(DirectoryItem SourceFolder, List<FileItem> HeaderFiles, bool bUsePrecompiled)
		{
			this.SourceFolder = SourceFolder;
			this.HeaderFiles = HeaderFiles;
			this.bUsePrecompiled = bUsePrecompiled;
		}

		public UHTModuleHeaderInfo(BinaryArchiveReader Reader)
		{
			SourceFolder = Reader.ReadDirectoryItem();
			HeaderFiles = Reader.ReadList(() => Reader.ReadFileItem());
			bUsePrecompiled = Reader.ReadBool();
		}

		public void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteDirectoryItem(SourceFolder);
			Writer.WriteList(HeaderFiles, Item => Writer.WriteFileItem(Item));
			Writer.WriteBool(bUsePrecompiled);
		}
	}

	// This handles all running of the HeaderTool
	class HeaderToolExecution
	{
		public static void SetupUObjectModules
		(
			IEnumerable<BuildModuleCPP> ModulesToGenerateHeadersFor,
			BuildTargetPlatform        Platform,
			UProjectDescriptor          ProjectDescriptor,
			List<HeaderToolModuleInfo>         UObjectModules,
			List<UHTModuleHeaderInfo>   UObjectModuleHeaders,
			EGeneratedCodeVersion       GeneratedCodeVersion,
			// bool bIsAssemblingBuild,
			SourceFileMetadataCache     MetadataCache
		)
		{
			// Find the type of each module
			Dictionary<BuildModuleCPP, UHTModuleType> ModuleToType = new Dictionary<BuildModuleCPP, UHTModuleType>();
			foreach(BuildModuleCPP Module in ModulesToGenerateHeadersFor)
			{
				ModuleToType[Module] = GetModuleType(Module.ModuleRule, ProjectDescriptor);
			}

			// Sort modules by type, then by dependency
			List<BuildModuleCPP> ModuleCPPsSortedByType = ModulesToGenerateHeadersFor.OrderBy(c => ModuleToType[c]).ToList();
			StableTopologicalSort(ModuleCPPsSortedByType);

			// Create the info for each module in parallel
			HeaderToolModuleInfo[] UHTModuleInfos = new HeaderToolModuleInfo[ModuleCPPsSortedByType.Count];
			using(ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
			{
				ReadOnlyHashSet<string> ExcludedFolders = BuildPlatform.GetBuildPlatform(Platform, true).GetExcludedFolderNames();
				for(int Idx = 0; Idx < ModuleCPPsSortedByType.Count; ++Idx)
				{
					BuildModuleCPP Module = ModuleCPPsSortedByType[Idx];

					HeaderToolModuleInfo Info 
						= new UHTModuleInfo
						(
							Module.ModuleRuleFileName, 
							Module.RulesFile, 
							Module.ModuleDirectories, 
							ModuleToType[Module], 
							DirectoryItem.GetItemByDirectoryReference(Module.GeneratedCodeDirectory), 
							GeneratedCodeVersion, 
							Module.ModuleRule.bUsePrecompiled
						);
					UHTModuleInfos[Idx] = Info;

					Queue.Enqueue(() => SetupUObjectModule(Info, ExcludedFolders, MetadataCache, Queue));
				}
			}
			
			// Filter out all the modules with reflection data
			for(int Idx = 0; Idx < ModuleCPPsSortedByType.Count; ++Idx)
			{
				BuildModuleCPP IterModuleCPP     = ModuleCPPsSortedByType[Idx];
				HeaderToolModuleInfo    IterHeaderToolModuleInfo = UHTModuleInfos[Idx];

				if (0 < IterHeaderToolModuleInfo.PublicUObjectClassesHeaders.Count || 
					0 < IterHeaderToolModuleInfo.PrivateUObjectHeaders.Count       || 
					0 < IterHeaderToolModuleInfo.PublicUObjectHeaders.Count )
				{
					// Set a flag indicating that we need to add the generated headers directory
					IterModuleCPP.bAddGeneratedCodeIncludePath = true;

					// If we've got this far and there are no source files then it's likely we're installed and ignoring
					// engine files, so we don't need a .gen.cpp either
					IterHeaderToolModuleInfo.GeneratedCPPFilenameBase 
						= Path.Combine(IterModuleCPP.GeneratedCodeDirectory.FullName, IterHeaderToolModuleInfo.ModuleName) + ".gen";

					if (!IterModuleCPP.ModuleRule.bUsePrecompiled)
					{
						IterModuleCPP.GeneratedCodeWildcard = Path.Combine(IterModuleCPP.GeneratedCodeDirectory.FullName, "*.gen.cpp");
					}

					UObjectModules.Add(IterHeaderToolModuleInfo);

					DirectoryItem ModuleDirectoryItem = DirectoryItem.GetItemByDirectoryReference(IterModuleCPP.ModuleDirectory);

					List<FileItem> ReflectedHeaderFiles = new List<FileItem>();

					ReflectedHeaderFiles.AddRange(IterHeaderToolModuleInfo.PublicUObjectHeaders);
					ReflectedHeaderFiles.AddRange(IterHeaderToolModuleInfo.PublicUObjectClassesHeaders);
					ReflectedHeaderFiles.AddRange(IterHeaderToolModuleInfo.PrivateUObjectHeaders);

					UObjectModuleHeaders.Add
					(
						new UHTModuleHeaderInfo
						(
							ModuleDirectoryItem, 
							ReflectedHeaderFiles, 
							IterModuleCPP.ModuleRule.bUsePrecompiled
						)
					);
				}
				else
				{
					// Remove any stale generated code directory
					if(IterModuleCPP.GeneratedCodeDirectory != null && 
						!IterModuleCPP.ModuleRule.bUsePrecompiled)
					{
						if (DirectoryReference.Exists(IterModuleCPP.GeneratedCodeDirectory))
						{
							Directory.Delete(IterModuleCPP.GeneratedCodeDirectory.FullName, true);
						}
					}
				}
			}
		}

		// Builds and runs the header tool and touches the header directories.
		// Performs any early outs if headers need no changes, given the UObject modules, tool path, game name, and configuration
		public static void ExecuteHeaderToolIfNecessary
		(
			BuildConfiguration BuildConfiguration,
			FileReference ProjectFile,
			string TargetName,
			TargetType TargetType,
			bool bHasProjectScriptPlugin,
			List<HeaderToolModuleInfo> UObjectModules,
			FileReference ModuleInfoFileName,
			bool bIsGatheringBuild,
			bool bIsAssemblingBuild,
			ISourceFileWorkingSet WorkingSet
		)
        {
            if (ProgressWriter.bWriteMarkup)
            {
                Log.WriteLine(LogEventType.Console, "@progress push 5%");
            }

            Log.WriteLine(LogEventType.Console | LogEventType.Log, "Generating code.");

            // We never want to try to execute the header tool when we're already trying to build it!
            bool bIsBuildingUHT = TargetName.Equals(Tag.Module.ExternalTool.HeaderTool, StringComparison.InvariantCultureIgnoreCase);

            string RootLocalPath = BuildTool.RootDirectory.FullName;

            TargetConfiguration UHTConfig = BuildConfiguration.bForceDebugHeaderTool ? TargetConfiguration.Debug : TargetConfiguration.Development;

            // Figure out the receipt path
            FileReference HeaderToolReceipt = GetHeaderToolReceiptFile(ProjectFile, UHTConfig, bHasProjectScriptPlugin);

            // check if UHT is out of date
            DateTime HeaderToolTimestampUtc = DateTime.MaxValue;
            bool bHaveHeaderTool = !bIsBuildingUHT && GetHeaderToolTimestampUtc(HeaderToolReceipt, out HeaderToolTimestampUtc);

            // ensure the headers are up to date
            bool bUHTNeedsToRun = false;
            if (!bHaveHeaderTool)
            {
                bUHTNeedsToRun = true;
            }
            else if (BuildConfiguration.bForceHeaderGeneration)
            {
                bUHTNeedsToRun = true;
            }
            else if (AreGeneratedCodeFilesOutOfDate(BuildConfiguration, UObjectModules, HeaderToolTimestampUtc, bIsGatheringBuild, bIsAssemblingBuild))
            {
                bUHTNeedsToRun = true;
            }

            // Check we're not using a different version of UHT
            FileReference ToolInfoFile = ModuleInfoFileName.ChangeExtension(Tag.Ext.HeaderToolPath);
            if (!bUHTNeedsToRun)
            {
                if (!FileReference.Exists(ToolInfoFile))
                {
                    bUHTNeedsToRun = true;
                }
                else if (FileReference.ReadAllText(ToolInfoFile) != HeaderToolReceipt.FullName)
                {
                    bUHTNeedsToRun = true;
                }
            }

            // Get the file containing dependencies for the generated code
            FileReference ExternalDependenciesFile = ModuleInfoFileName.ChangeExtension(Tag.Ext.Dependencies);
            if (AreExternalDependenciesOutOfDate(ExternalDependenciesFile))
            {
                bUHTNeedsToRun = true;
                bHaveHeaderTool = false; // Force UHT to build until dependency checking is fast enough to run all the time
            }

            // @todo BuildTool make: Optimization: Ideally we could avoid having to generate this data in the case where UHT doesn't even need to run!  Can't we use the existing copy?  (see below use of Manifest)

            List<UHTManifest.Module> Modules = new List<UHTManifest.Module>();
            foreach (HeaderToolModuleInfo UObjectModule in UObjectModules)
            {
                Modules.Add(new UHTManifest.Module(UObjectModule));
            }
            UHTManifest Manifest = new UHTManifest(TargetName, TargetType, RootLocalPath, ExternalDependenciesFile.FullName, Modules);

            if (!bIsBuildingUHT && bUHTNeedsToRun)
            {
                // Always build HeaderTool if header regeneration is required, unless we're running within an installed ecosystem or hot-reloading
                if ((!BuildTool.IsEngineInstalled() || bHasProjectScriptPlugin) &&
                    !BuildConfiguration.bDoNotBuildUHT &&
                    !(bHaveHeaderTool && !bIsGatheringBuild && bIsAssemblingBuild)) // If running in "assembler only" mode, we assume UHT is already up to date for much faster iteration!
                {
                    // If it is out of date or not there it will be built.
                    // If it is there and up to date, it will add 0.8 seconds to the build time.

                    // Which desktop platform do we need to compile UHT for?
                    BuildTargetPlatform Platform = BuildHostPlatform.Current.Platform;

                    // NOTE: We force Development configuration for UHT so that it runs quickly, even when compiling debug, unless we say so explicitly
                    TargetConfiguration Configuration;
                    if (BuildConfiguration.bForceDebugHeaderTool)
                    {
                        Configuration = TargetConfiguration.Debug;
                    }
                    else
                    {
                        Configuration = TargetConfiguration.Development;
                    }

                    // Get the default architecture
                    string Architecture = BuildPlatform.GetBuildPlatform(Platform).GetDefaultArchitecture(null);

                    // Add UHT plugins to UBT command line as external plugins
                    FileReference ScriptProjectFile = null;
                    if (bHasProjectScriptPlugin && ProjectFile != null)
                    {
                        ScriptProjectFile = ProjectFile;
                    }

                    // Create the target descriptor
                    BuildTargetDescriptor TargetDescriptor
                        = new BuildTargetDescriptor(ScriptProjectFile, Tag.Module.ExternalTool.HeaderTool, Platform, Configuration, Architecture, null) { bQuiet = true };

                    BuildMode.Build(new List<BuildTargetDescriptor> { TargetDescriptor }, BuildConfiguration, WorkingSet, BuildOptions.None, null);
                }


				string ActualTargetName = String.IsNullOrEmpty(TargetName) ?
#error "MyEngine" : TargetName;
				Log.TraceInformation("Parsing headers for {0}", ActualTargetName);

                FileReference HeaderToolPath = GetHeaderToolPath(HeaderToolReceipt);
                if (!FileReference.Exists(HeaderToolPath))
                {
                    throw new BuildException("Unable to generate headers because HeaderTool binary was not found ({0}).", HeaderToolPath);
                }

                // Disable extensions when serializing to remove the $type fields
                Directory.CreateDirectory(ModuleInfoFileName.Directory.FullName);
                System.IO.File.WriteAllText
                (
                    ModuleInfoFileName.FullName,
                    FastJSON.JSON.Instance.ToJSON(Manifest, new FastJSON.JSONParameters { UseExtensions = false })
                );

                string CmdLine = (ProjectFile != null) ? "\"" + ProjectFile.FullName + "\"" : TargetName;
                CmdLine += " \"" + ModuleInfoFileName + "\" -LogCmds=\"loginit warning, logexit warning, logdatabase error\" -Unattended -WarningsAsErrors";

                if (Log.OutputFile != null)
                {
                    string LogFileName = Log.OutputFile.GetFileNameWithoutExtension();
                    LogFileName = (LogFileName.StartsWith("UBT") ? "UHT" + LogFileName.Substring(3) : LogFileName + "_UHT") + ".txt";
                    LogFileName = FileReference.Combine(Log.OutputFile.Directory, LogFileName).ToString();

                    CmdLine += " -abslog=\"" + LogFileName + "\"";
                }

                if (BuildTool.IsEngineInstalled())
                {
                    CmdLine += " -installed";
                }

                if (BuildConfiguration.bFailIfGeneratedCodeChanges)
                {
                    CmdLine += " -FailIfGeneratedCodeChanges";
                }

                Log.TraceInformation("  Running HeaderTool on BuildTool : {0}", CmdLine);

                CompilationResult UHTResult = (CompilationResult)RunExternalNativeExecutable(GetHeaderToolPath(HeaderToolReceipt), CmdLine);

                if (UHTResult != CompilationResult.Succeeded)
                {
                    // On Linux and Mac, the shell will return 128+signal number exit codes if UHT gets a signal (e.g. crashes or is interrupted)
                    if ((BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac) &&
                        128 <= (int)(UHTResult))
                    {
                        // SIGINT is 2, so 128 + SIGINT is 130
                        UHTResult = ((int)(UHTResult) == 130) ? CompilationResult.Canceled : CompilationResult.CrashOrAssert;
                    }

                    if ((BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64) &&
                        (int)(UHTResult) < 0)
                    {
                        Log.TraceError(String.Format("HeaderTool failed with exit code 0x{0:X} - check that engine prerequisites are installed.", (int)UHTResult));
                    }

                    Debugger.Break();
                    throw new CompilationResultException(UHTResult);
                }

                // Update the tool info file
                DirectoryReference.CreateDirectory(ToolInfoFile.Directory);
                FileReference.WriteAllText(ToolInfoFile, HeaderToolReceipt.FullName);

                // Now that UHT has successfully finished generating code, we need to update all cached FileItems in case their last write time has changed.
                // Otherwise UBT might not detect changes UHT made.
                ResetCachedHeaderInfo(UObjectModules);
            }
            else
            {
                Log.TraceVerbose("Generated code is up to date.");
            }

            // touch the directories
            UpdateDirectoryTimestamps(UObjectModules);
        }

        private static void SetupUObjectModule
		(
			HeaderToolModuleInfo           ModuleInfo,
			ReadOnlyHashSet<string> ExcludedFolders,
			SourceFileMetadataCache MetadataCache,
			ThreadPoolWorkQueue     ThreadQueue
		)
		{
			foreach (DirectoryReference ModuleDirectory in ModuleInfo.ModuleDirectories)
			{
				DirectoryItem ModuleDirectoryItem = DirectoryItem.GetItemByDirectoryReference(ModuleDirectory);

				List<FileItem> HeaderFiles = new List<FileItem>();
				FindHeaders(ModuleDirectoryItem, ExcludedFolders, HeaderFiles);

				foreach (FileItem HeaderFile in HeaderFiles)
				{
					ThreadQueue.Enqueue(() => SetupUObjectModuleHeader(ModuleInfo, HeaderFile, MetadataCache));
				}
			}
		}

		// Gets the path to the receipt for UHT
		private static FileReference GetHeaderToolReceiptFile(FileReference ProjectFile, TargetConfiguration Configuration, bool bHasProjectScriptPlugin)
		{
			if (bHasProjectScriptPlugin && ProjectFile != null)
			{
				return TargetReceipt.GetDefaultPath(ProjectFile.Directory, Tag.Module.ExternalTool.HeaderTool, BuildHostPlatform.Current.Platform, Configuration, "");
			}
			else
			{
				return TargetReceipt.GetDefaultPath(BuildTool.EngineDirectory, Tag.Module.ExternalTool.HeaderTool, BuildHostPlatform.Current.Platform, Configuration, "");
			}
		}

		private static void SetupUObjectModuleHeader(HeaderToolModuleInfo ModuleInfo, FileItem HeaderFile, SourceFileMetadataCache MetadataCache)
		{
			// Check to see if we know anything about this file.  If we have up-to-date cached information about whether it has
			// UObjects or not, we can skip doing a test here.
			if (MetadataCache.ContainsReflectionMarkup(HeaderFile))
			{
				lock(ModuleInfo)
				{
					bool bFoundHeaderLocation = false;
					foreach (DirectoryReference ModuleDirectory in ModuleInfo.ModuleDirectories)
					{
						if (HeaderFile.FileDirectory.IsUnderDirectory(DirectoryReference.Combine(ModuleDirectory, Tag.Directory.Classes)))
						{
							ModuleInfo.PublicUObjectClassesHeaders.Add(HeaderFile);
							bFoundHeaderLocation = true;
						}
						else if (HeaderFile.FileDirectory.IsUnderDirectory(DirectoryReference.Combine(ModuleDirectory, "Public")))
						{
							ModuleInfo.PublicUObjectHeaders.Add(HeaderFile);
							bFoundHeaderLocation = true;
						}
					}
					if (!bFoundHeaderLocation)
					{
						ModuleInfo.PrivateUObjectHeaders.Add(HeaderFile);
					}
				}
			}
		}

		private static UHTModuleType GetEngineModuleTypeFromDescriptor(ModuleDescriptor Module)
		{
			UHTModuleType? Type = UHTModuleTypeExtensions.EngineModuleTypeFromHostType(Module.Type);
			if (Type == null)
			{
				throw new BuildException("Unhandled engine module type {0} for {1}", Module.Type.ToString(), Module.ModuleName);
			}
			return Type.GetValueOrDefault();
		}

		private static UHTModuleType GetGameModuleTypeFromDescriptor(ModuleDescriptor Module)
		{
			UHTModuleType? Type = UHTModuleTypeExtensions.GameModuleTypeFromHostType(Module.Type);
			if (Type == null)
			{
				throw new BuildException("Unhandled game module type {0}", Module.Type.ToString());
			}
			return Type.GetValueOrDefault();
		}

		// Returns a copy of Nodes sorted by dependency.
		// Independent or circularly-dependent nodes should remain in their same relative order within the original Nodes sequence.
		private static void StableTopologicalSort(List<BuildModuleCPP> NodeListToSort)
		{
			int NodeCount = NodeListToSort.Count;

			Dictionary<BuildModule, HashSet<BuildModule>> Cache = new Dictionary<BuildModule, HashSet<BuildModule>>();

			for (int Index1 = 0; Index1 != NodeCount; ++Index1)
			{
				BuildModuleCPP Node1 = NodeListToSort[Index1];

				for (int Index2 = 0; Index2 != Index1; ++Index2)
				{
					BuildModuleCPP Node2 = NodeListToSort[Index2];

					if (IsDependency(Node2, Node1, Cache) &&
						!IsDependency(Node1, Node2, Cache))
					{
						// Rotate element at Index1 into position at Index2
						for (int Index3 = Index1; Index3 != Index2;)
						{
							--Index3;
							NodeListToSort[Index3 + 1] = NodeListToSort[Index3];
						}
						NodeListToSort[Index2] = Node1;

						// Break out of this loop, because this iteration must have covered all existing cases
						// involving the node formerly at position Index1
						break;
					}
				}
			}
		}

		// Tests whether one module has a dependency on another
		private static bool IsDependency(BuildModuleCPP TestingModule, BuildModuleCPP TesteeModule, Dictionary<BuildModule, HashSet<BuildModule>> Cache)
		{
			if (!Cache.TryGetValue(TestingModule, out HashSet<BuildModule> Dependencies))
			{
				Dependencies = new HashSet<BuildModule>();
				TestingModule.RecursivelyGetAllDependencyModules(new List<BuildModule>(), Dependencies, true, true, false);
				Cache.Add(TestingModule, Dependencies);
			}
			return Dependencies.Contains(TesteeModule);
		}

		// Gets the module type for a given rules object
		private static UHTModuleType GetModuleType(ModuleRules RulesObject, UProjectDescriptor DescriptorForProjectBeingBuilt)
		{
			ModuleRulesContext Context = RulesObject.Context;
			if (Context.bClassifyAsGameModuleForUHT)
			{
				if (RulesObject.Type == ModuleRules.ModuleType.External)
				{
					return UHTModuleType.GameThirdParty;
				}
				if (Context.DefaultUHTModuleType.HasValue)
				{
					return Context.DefaultUHTModuleType.Value;
				}
				if (RulesObject.Plugin != null)
				{
					ModuleDescriptor Module = RulesObject.Plugin.Descriptor.Modules.FirstOrDefault(x => x.ModuleName == RulesObject.Name);
					if (Module != null)
					{
						return GetGameModuleTypeFromDescriptor(Module);
					}
				}
				if (DescriptorForProjectBeingBuilt != null &&
				   DescriptorForProjectBeingBuilt.Modules != null)
				{
					ModuleDescriptor Module = DescriptorForProjectBeingBuilt.Modules.FirstOrDefault(x => x.ModuleName == RulesObject.Name);
					if (Module != null)
					{
						return UHTModuleTypeExtensions.GameModuleTypeFromHostType(Module.Type) ?? UHTModuleType.GameRuntime;
					}
				}
				return UHTModuleType.GameRuntime;
			}
			else
			{
				if (RulesObject.Type == ModuleRules.ModuleType.External)
				{
					return UHTModuleType.EngineThirdParty;
				}
				if (Context.DefaultUHTModuleType.HasValue)
				{
					return Context.DefaultUHTModuleType.Value;
				}
				if (RulesObject.Plugin != null)
				{
					ModuleDescriptor Module = RulesObject.Plugin.Descriptor.Modules.FirstOrDefault(x => x.ModuleName == RulesObject.Name);
					if (Module != null)
					{
						return GetEngineModuleTypeFromDescriptor(Module);
					}
				}
				throw new BuildException("Unable to determine UHT module type for {0}", RulesObject.File);
			}
		}

		// Find all the headers under the given base directory, excluding any other platform folders.
		private static void FindHeaders(DirectoryItem BaseDirectoryToSearch, ReadOnlyHashSet<string> ExcludeFolders, List<FileItem> RecevingHeaders)
		{
			foreach (DirectoryItem SubDirectory in BaseDirectoryToSearch.EnumerateSubDirectories())
			{
				if (!ExcludeFolders.Contains(SubDirectory.Name))
				{
					FindHeaders(SubDirectory, ExcludeFolders, RecevingHeaders);
				}
			}
			foreach (FileItem File in BaseDirectoryToSearch.EnumerateAllCachedFiles())
			{
				if (File.HasExtension(Tag.Ext.Header))
				{
					RecevingHeaders.Add(File);
				}
			}
		}

		// Gets HeaderTool.exe path.
		// Does not care if HeaderTool was build as a monolithic exe or not.
		private static FileReference GetHeaderToolPath(FileReference ReceiptFile)
		{
			TargetReceipt Receipt = TargetReceipt.Read(ReceiptFile);
			return Receipt.Launch;
		}

		// Gets the latest write time of any of the HeaderTool binaries (including DLLs and Plugins) or
		// DateTime.MaxValue if HeaderTool does not exist
		
		// returns Latest timestamp of UHT binaries or DateTime.MaxValue
		// if HeaderTool is out of date and needs to be rebuilt.
		private static bool GetHeaderToolTimestampUtc(FileReference ReceiptPath, out DateTime Timestamp)
		{
            // Try to read the receipt for UHT.
            FileItem ReceiptFile = FileItem.GetItemByFileReference(ReceiptPath);
            if (!ReceiptFile.Exists)
            {
                Timestamp = DateTime.MaxValue;
                return false;
            }

            // Don't check timestamps for individual binaries if we're using the installed version of UHT. It will always be up to date.
            if (!BuildTool.IsFileInstalled(ReceiptFile.FileDirectory))
            {
                if (!TargetReceipt.TryRead(ReceiptPath, out TargetReceipt Receipt))
                {
                    Timestamp = DateTime.MaxValue;
                    return false;
                }

                // Make sure all the build products exist, and that the receipt is newer
                foreach (BuildProduct BuildProduct in Receipt.BuildProducts)
                {
                    FileItem BuildProductItem = FileItem.GetItemByFileReference(BuildProduct.Path);

                    if (!BuildProductItem.Exists ||
                        ReceiptFile.LastWriteTimeUtc < BuildProductItem.LastWriteTimeUtc)
                    {
                        Timestamp = DateTime.MaxValue;
                        return false;
                    }
                }
            }

            // Return the timestamp for all the binaries
            Timestamp = ReceiptFile.LastWriteTimeUtc;
            return true;
        }

        // Gets the timestamp of CoreUObject.gen.cpp file.
        // <returns>Last write time of CoreUObject.gen.cpp or DateTime.MaxValue if it doesn't exist.</returns>
        private static DateTime GetCoreGeneratedTimestampUtc(string ModuleName, string ModuleGeneratedCodeDirectory)
		{
			// In Installed Builds, we don't check the timestamps on engine headers.  Default to a very old date.
			if (BuildTool.IsEngineInstalled())
			{
				return DateTime.MinValue;
			}

			// Otherwise look for CoreUObject.init.gen.cpp
			FileInfo CoreGeneratedFileInfo = new FileInfo(Path.Combine(ModuleGeneratedCodeDirectory, ModuleName + Tag.Ext.InitGenCPP));
			if (CoreGeneratedFileInfo.Exists)
			{
				return CoreGeneratedFileInfo.LastWriteTimeUtc;
			}

			// Doesn't exist, so use a 'newer that everything' date to force rebuild headers.
			return DateTime.MaxValue;
		}

		// Checks the class header files and determines if generated UObject code files are out of date in comparison.
		private static bool AreGeneratedCodeFilesOutOfDate
		(
			BuildConfiguration BuildConfiguration,
			List<HeaderToolModuleInfo> UObjectModules, // Modules that we generate headers for
			DateTime HeaderToolTimestampUtc,
			bool bIsGatheringBuild,
			bool bIsAssemblingBuild
		)
		{
			// Get CoreUObject.init.gen.cpp timestamp.  If the source files are older than the CoreUObject generated code, we'll
			// need to regenerate code for the module
			DateTime? CoreGeneratedTimestampUtc = null;
			{
				// Find the CoreUObject module
				foreach (HeaderToolModuleInfo Module in UObjectModules)
				{
					if (Module.ModuleName.Equals(Tag.Module.Engine.CoreUObject, StringComparison.InvariantCultureIgnoreCase))
					{
						CoreGeneratedTimestampUtc = GetCoreGeneratedTimestampUtc(Module.ModuleName, Path.GetDirectoryName(Module.GeneratedCPPFilenameBase));
						break;
					}
				}
				if (CoreGeneratedTimestampUtc == null)
				{
					throw new BuildException("Could not find CoreUObject in list of all UObjectModules");
				}
			}

			foreach (HeaderToolModuleInfo Module in UObjectModules)
			{
				// If we're using a precompiled engine, skip checking timestamps for modules that are under the engine directory
				if (Module.bIsReadOnly)
				{
					continue;
				}

				// Make sure we have an existing folder for generated code.  If not, then we definitely need to generate code!
				string GeneratedCodeDirectory = Path.GetDirectoryName(Module.GeneratedCPPFilenameBase);
				FileSystemInfo TestDirectory = (FileSystemInfo)new DirectoryInfo(GeneratedCodeDirectory);
				if (!TestDirectory.Exists)
				{
					// Generated code directory is missing entirely!
					Log.TraceLog("HeaderTool needs to run because no generated code directory was found for module {0}", Module.ModuleName);
					return true;
				}

				// Grab our special "Timestamp" file that we saved after the last set of headers were generated.  This file
				// actually contains the list of source files which contained UObjects, so that we can compare to see if any
				// UObject source files were deleted (or no longer contain UObjects), which means we need to run UHT even
				// if no other source files were outdated
				string TimestampFile = Path.Combine(GeneratedCodeDirectory, @"Timestamp");
				FileSystemInfo SavedTimestampFileInfo = (FileSystemInfo)new FileInfo(TimestampFile);
				if (!SavedTimestampFileInfo.Exists)
				{
					// Timestamp file was missing (possibly deleted/cleaned), so headers are out of date
					Log.TraceLog("HeaderTool needs to run because UHT Timestamp file did not exist for module {0}", Module.ModuleName);
					return true;
				}

				// Make sure the last UHT run completed after HeaderTool.exe was compiled last, and after the CoreUObject headers were touched last.
				DateTime SavedTimestampUtc = SavedTimestampFileInfo.LastWriteTimeUtc;
				if (SavedTimestampUtc < HeaderToolTimestampUtc)
				{
					// Generated code is older than HeaderTool.exe.  Out of date!
					Log.TraceLog("HeaderTool needs to run because HeaderTool timestamp ({0}) is later than timestamp for module {1} ({2})", HeaderToolTimestampUtc.ToLocalTime(), Module.ModuleName, SavedTimestampUtc.ToLocalTime());
					return true;
				}
				if (SavedTimestampUtc < CoreGeneratedTimestampUtc)
				{
					// Generated code is older than CoreUObject headers.  Out of date!
					Log.TraceLog("HeaderTool needs to run because CoreUObject timestamp ({0}) is newer than timestamp for module {1} ({2})", CoreGeneratedTimestampUtc.Value.ToLocalTime(), Module.ModuleName, SavedTimestampUtc.ToLocalTime());
					return true;
				}

				// Has the .build.cs file changed since we last generated headers successfully?
				FileInfo ModuleRulesFile = new FileInfo(Module.ModuleRulesFile.FullName);
				if (!ModuleRulesFile.Exists || SavedTimestampUtc < ModuleRulesFile.LastWriteTimeUtc)
				{
					Log.TraceLog("HeaderTool needs to run because SavedTimestamp is older than the rules file ({0}) for module {1}", Module.ModuleRulesFile, Module.ModuleName);
					return true;
				}

				// Iterate over our UObjects headers and figure out if any of them have changed
				List<FileItem> AllUObjectHeaders = new List<FileItem>();
				AllUObjectHeaders.AddRange(Module.PublicUObjectClassesHeaders);
				AllUObjectHeaders.AddRange(Module.PublicUObjectHeaders);
				AllUObjectHeaders.AddRange(Module.PrivateUObjectHeaders);

				// Load up the old timestamp file and check to see if anything has changed
				{
					string[] UObjectFilesFromPreviousRun = File.ReadAllLines(TimestampFile);
					if (AllUObjectHeaders.Count != UObjectFilesFromPreviousRun.Length)
					{
						Log.TraceLog("HeaderTool needs to run because there are a different number of UObject source files in module {0}", Module.ModuleName);
						return true;
					}

					// Iterate over our UObjects headers and figure out if any of them have changed
					HashSet<string> ObjectHeadersSet = new HashSet<string>(AllUObjectHeaders.Select(x => x.AbsolutePath), FileReference.Comparer);
					foreach (string FileName in UObjectFilesFromPreviousRun)
					{
						if(!ObjectHeadersSet.Contains(FileName))
						{
							Log.TraceLog("HeaderTool needs to run because the set of UObject source files in module {0} has changed ({1})", Module.ModuleName, FileName);
							return true;
						}
					}
				}

				foreach (FileItem HeaderFile in AllUObjectHeaders)
				{
					DateTime HeaderFileTimestampUtc = HeaderFile.LastWriteTimeUtc;

					// Has the source header changed since we last generated headers successfully?
					if (SavedTimestampUtc < HeaderFileTimestampUtc)
					{
						Log.TraceLog("HeaderTool needs to run because SavedTimestamp is older than HeaderFileTimestamp ({0}) for module {1}", HeaderFile.AbsolutePath, Module.ModuleName);
						return true;
					}

					// When we're running in assembler mode, outdatedness cannot be inferred by checking the directory timestamp
					// of the source headers.  We don't care if source files were added or removed in this mode, because we're only
					// able to process the known UObject headers that are in the Makefile.  If UObject header files are added/removed,
					// we expect the user to re-run GenerateProjectFiles which will force UBTMakefile outdatedness.
					// @todo ubtmake: Possibly, we should never be doing this check these days.
					//
					// We don't need to do this check if using hot reload makefiles, since makefile out-of-date checks already handle it.
					if (!BuildConfiguration.bUseBuildToolMakefiles && 
						(bIsGatheringBuild || !bIsAssemblingBuild))
					{
						// Also check the timestamp on the directory the source file is in.  If the directory timestamp has
						// changed, new source files may have been added or deleted.  We don't know whether the new/deleted
						// files were actually UObject headers, but because we don't know all of the files we processed
						// in the previous run, we need to assume our generated code is out of date if the directory timestamp
						// is newer.
						DateTime HeaderDirectoryTimestampUtc = new DirectoryInfo(Path.GetDirectoryName(HeaderFile.AbsolutePath)).LastWriteTimeUtc;
						if (SavedTimestampUtc < HeaderDirectoryTimestampUtc)
						{
							Log.TraceLog("HeaderTool needs to run because the directory containing an existing header ({0}) has changed, and headers may have been added to or deleted from module {1}", HeaderFile.AbsolutePath, Module.ModuleName);
							return true;
						}
					}
				}
			}

			return false;
		}

		// Determines if any external dependencies for generated code is out of date
		private static bool AreExternalDependenciesOutOfDate(FileReference ExternalDependenciesFile)
		{
			if (!FileReference.Exists(ExternalDependenciesFile))
			{
				return true;
			}

			DateTime LastWriteTime = File.GetLastWriteTimeUtc(ExternalDependenciesFile.FullName);

			string[] Lines = File.ReadAllLines(ExternalDependenciesFile.FullName);
			foreach (string Line in Lines)
			{
				string ExternalDependencyFile = Line.Trim();
				if (0 < ExternalDependencyFile.Length)
				{
					if (!File.Exists(ExternalDependencyFile) || 
						LastWriteTime < File.GetLastWriteTimeUtc(ExternalDependencyFile))
					{
						return true;
					}
				}
			}

			return false;
		}

		// Updates the intermediate include directory timestamps of all the passed in UObject modules
		private static void UpdateDirectoryTimestamps(List<HeaderToolModuleInfo> UObjectModules)
		{
			foreach (HeaderToolModuleInfo Module in UObjectModules)
			{
				if(!Module.bIsReadOnly)
				{
					string GeneratedCodeDirectory = Path.GetDirectoryName(Module.GeneratedCPPFilenameBase);
					DirectoryInfo GeneratedCodeDirectoryInfo = new DirectoryInfo(GeneratedCodeDirectory);

					try
					{
						if (GeneratedCodeDirectoryInfo.Exists)
						{
							// Touch the include directory since we have technically 'generated' the headers
							// However, the headers might not be touched at all since that would cause the compiler to recompile everything
							// We can't alter the directory timestamp directly, because this may throw exceptions when the directory is
							// open in visual studio or windows explorer, so instead we create a blank file that will change the timestamp for us
							FileReference TimestampFile = FileReference.Combine(new DirectoryReference(GeneratedCodeDirectoryInfo.FullName), "Timestamp");

							// Save all of the UObject files to a timestamp file.  We'll load these on the next run to see if any new
							// files with UObject classes were deleted, so that we'll know to run UHT even if the timestamps of all
							// of the other source files were unchanged
							{
								List<string> AllUObjectFiles = new List<string>();
								AllUObjectFiles.AddRange(Module.PublicUObjectClassesHeaders.ConvertAll(Item => Item.AbsolutePath));
								AllUObjectFiles.AddRange(Module.PublicUObjectHeaders.ConvertAll(Item => Item.AbsolutePath));
								AllUObjectFiles.AddRange(Module.PrivateUObjectHeaders.ConvertAll(Item => Item.AbsolutePath));
								FileReference.WriteAllLines(TimestampFile, AllUObjectFiles);
							}

							// Because new .cpp and .h files may have been generated by UHT, invalidate the DirectoryLookupCache
							DirectoryLookupCache.InvalidateCachedDirectory(new DirectoryReference(GeneratedCodeDirectoryInfo.FullName));
						}
					}
					catch (Exception Exception)
					{
						throw new BuildException(Exception, "Couldn't touch header directories: " + Exception.Message);
					}
				}
			}
		}

		// Run an external native executable (and capture the output), given the executable path and the commandline.
		private static int RunExternalNativeExecutable(FileReference ExePath, string Commandline)
		{
			Log.TraceVerbose("RunExternalExecutable {0} {1}", ExePath.FullName, Commandline);
			using (Process GameProcess = new Process())
			{
				GameProcess.StartInfo.FileName               = ExePath.FullName;
				GameProcess.StartInfo.Arguments              = Commandline;
				GameProcess.StartInfo.UseShellExecute        = false;
				GameProcess.StartInfo.RedirectStandardOutput = true;
				GameProcess.OutputDataReceived               += PrintProcessOutputAsync;
				GameProcess.Start();
				GameProcess.BeginOutputReadLine();
				GameProcess.WaitForExit();

				return GameProcess.ExitCode;
			}
		}

		// Simple function to pipe output asynchronously
		private static void PrintProcessOutputAsync(object Sender, DataReceivedEventArgs Event)
		{
			if (!String.IsNullOrEmpty(Event.Data))
			{
				Log.TraceInformation(Event.Data);
			}
		}

		private static void ResetCachedHeaderInfo(List<HeaderToolModuleInfo> UObjectModules)
		{
			foreach(HeaderToolModuleInfo ModuleInfo in UObjectModules)
			{
				ModuleInfo.GeneratedCodeDirectory.ResetCachedInfo();
			}
		}
	}
}
