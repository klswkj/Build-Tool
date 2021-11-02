using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
	// Class which compiles (and caches) rules assemblies for different folders.
	public sealed class RulesCompiler
	{
		// Enum for types of rules files. Should match extensions in RulesFileExtensions.
		public enum RulesFileType
		{
			Module,          // .build.cs files
			Target,          // .target.cs files
			AutomationModule // .automation.csproj files
		}

		// Cached list of rules files in each directory of each type
		class RulesFileCache
		{
			public List<FileReference> ModuleRules       = new List<FileReference>();
			public List<FileReference> TargetRules       = new List<FileReference>();
			public List<FileReference> AutomationModules = new List<FileReference>();
		}

		// Map of root folders to a cached list of all UBT-related source files in that folder or any of its sub-folders.
		// We cache these file names so we can avoid searching for them later on.
		private static readonly Dictionary<DirectoryReference, RulesFileCache> RootFolderToRulesFileCache = new Dictionary<DirectoryReference, RulesFileCache>();

		// The cached rules assembly for engine modules and targets.
		private static RulesAssembly ProgramRulesDLL_EngineRulesAssembly;

		// The cached rules assembly for enterprise modules and targets.
		private static RulesAssembly EnterpriseRulesAssembly;

		// Map of assembly names we've already compiled and loaded to their Assembly and list of game folders.
		// This is used to prevent trying to recompile the same assembly when ping-ponging between different types of targets
		private static readonly Dictionary<FileReference, RulesAssembly> LoadedAssemblyMap = new Dictionary<FileReference, RulesAssembly>();

		// <param name="bIncludeTempTargets">Whether to include targets generated by UAT to accomodate content-only projects that need to be compiled to include plugins</param>
		// <param name="bIncludePlatformExtensions"></param>
		// <returns></returns>
		public static List<FileReference> FindAllRulesSourceFiles
		(
			RulesFileType RulesFileType,
			List<DirectoryReference> GameFolders,
			List<FileReference> ForeignPlugins,
			List<DirectoryReference> AdditionalSearchPaths,
			bool bIncludeEngine = true,
			bool bIncludeEnterprise = true,
			bool bIncludeTempTargets = true,
			bool bIncludePlatformExtensions = true
		)
		{
			List<DirectoryReference> Folders = new List<DirectoryReference>();

			// Add all engine source (including third party source)
			if (bIncludeEngine)
			{
				Folders.Add(BuildTool.EngineSourceDirectory);
			}
			if(bIncludeEnterprise)
			{
				Folders.Add(BuildTool.EnterpriseSourceDirectory);
			}
			if (bIncludePlatformExtensions)
			{
				Folders.Add(BuildTool.EnginePlatformExtensionsDirectory);
			}

			// @todo plugin: Disallow modules from including plugin modules as dependency modules? (except when the module is part of that plugin)

			// Get all the root folders for plugins
			List<DirectoryReference> RootFolders = new List<DirectoryReference>();

			if (bIncludeEngine)
			{
				RootFolders.AddRange(BuildTool.GetAllEngineDirectories());
			}
			if(bIncludeEnterprise)
			{
				RootFolders.Add(BuildTool.EnterpriseDirectory);
			}
			if (GameFolders != null)
			{
				if (bIncludePlatformExtensions)
				{
					foreach (DirectoryReference GameFolder in GameFolders)
					{
						RootFolders.AddRange(BuildTool.GetAllProjectDirectories(GameFolder));
					}
				}
				else
				{
					RootFolders.AddRange(GameFolders);
				}
			}

			// Find all the plugin source directories
			foreach (DirectoryReference RootFolder in RootFolders)
			{
				DirectoryReference PluginsFolder = DirectoryReference.Combine(RootFolder, Tag.Directory.Plugins);

				foreach (FileReference PluginFile in Plugins.EnumeratePlugins(PluginsFolder))
				{
					Folders.Add(DirectoryReference.Combine(PluginFile.Directory, Tag.Directory.SourceCode));
				}
			}

			// Add all the extra plugin folders
			if (ForeignPlugins != null)
			{
				foreach (FileReference ForeignPlugin in ForeignPlugins)
				{
					Folders.Add(DirectoryReference.Combine(ForeignPlugin.Directory, Tag.Directory.SourceCode));
				}
			}

			// Add in the game folders to search
			if (GameFolders != null)
			{
				foreach (DirectoryReference GameFolder in GameFolders)
				{
					if (bIncludePlatformExtensions)
					{
						Folders.AddRange(BuildTool.GetAllProjectDirectories(GameFolder, Tag.Directory.SourceCode));
					}
					else
					{
						Folders.Add(DirectoryReference.Combine(GameFolder, Tag.Directory.SourceCode));
					}

					if (bIncludeTempTargets)
					{
						DirectoryReference GameIntermediateSourceFolder = DirectoryReference.Combine(GameFolder, Tag.Directory.Generated, Tag.Directory.SourceCode);
						Folders.Add(GameIntermediateSourceFolder);
					}
				}
			}

			// Process the additional search path, if sent in
			if (AdditionalSearchPaths != null)
			{
				foreach (DirectoryReference AdditionalSearchPath in AdditionalSearchPaths)
				{
					if (AdditionalSearchPath != null)
					{
						if (DirectoryReference.Exists(AdditionalSearchPath))
						{
							Folders.Add(AdditionalSearchPath);
						}
						else
						{
							throw new BuildException("Couldn't find AdditionalSearchPath for rules source files '{0}'", AdditionalSearchPath);
						}
					}
				}
			}

			// Iterate over all the folders to check
			List<FileReference> SourceFiles          = new List<FileReference>();
			HashSet<FileReference> UniqueSourceFiles = new HashSet<FileReference>();

			foreach (DirectoryReference Folder in Folders)
			{
				IReadOnlyList<FileReference> SourceFilesForFolder = FindAllRulesFiles(Folder, RulesFileType);
				foreach (FileReference SourceFile in SourceFilesForFolder)
				{
					if (UniqueSourceFiles.Add(SourceFile))
					{
						SourceFiles.Add(SourceFile);
					}
				}
			}
			return SourceFiles;
		}

		// Invalidate the cache for the givcen directory
		// <param name="DirectoryPath">Directory to invalidate</param>
        public static void InvalidateRulesFileCache(string DirectoryPath)
        {
            DirectoryReference Directory = new DirectoryReference(DirectoryPath);
            RootFolderToRulesFileCache.Remove(Directory);
            DirectoryLookupCache.InvalidateCachedDirectory(Directory);
        }

		// Prefetch multiple directories in parallel
		// <param name="Directories">The directories to cache</param>
		private static void PrefetchRulesFiles(IEnumerable<DirectoryReference> Directories)
		{
			// 여기 문제 using BuildToolUtilities;을 epicgames에서만 만들어서 쓰는거 같은데,
			// 레퍼런스도 못찾겠음, 내부에서는 QueueSegment를 쓰는거 같은데 그거 밖에 모르겠음.
			ThreadPoolWorkQueue Queue = null;

			try
			{
				foreach(DirectoryReference Directory in Directories)
				{
					if(!RootFolderToRulesFileCache.ContainsKey(Directory))
					{
						RulesFileCache Cache = new RulesFileCache();
						RootFolderToRulesFileCache[Directory] = Cache;

						if(Queue == null)
						{
							Queue = new ThreadPoolWorkQueue();
						}

						DirectoryItem DirectoryItem = DirectoryItem.GetItemByDirectoryReference(Directory);
						Queue.Enqueue(() => FindAllRulesFilesRecursively(DirectoryItem, Cache, Queue));
					}
				}
			}
			finally
			{
				if(Queue != null)
				{
					Queue.Dispose();
					Queue = null;
				}
			}
		}

		// Finds all the rules of the given type under a given directory
		private static IReadOnlyList<FileReference> FindAllRulesFiles(DirectoryReference DirectoryToSearch, RulesFileType InRulesFileTypeToReturn)
		{
			// Check to see if we've already cached source files for this folder
			if (!RootFolderToRulesFileCache.TryGetValue(DirectoryToSearch, out RulesFileCache Cache))
			{
				Cache = new RulesFileCache();
				using (ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
				{
					DirectoryItem BaseDirectory = DirectoryItem.GetItemByDirectoryReference(DirectoryToSearch);
					Queue.Enqueue(() => FindAllRulesFilesRecursively(BaseDirectory, Cache, Queue));
				}
				Cache.ModuleRules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				Cache.TargetRules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				Cache.AutomationModules.Sort((A, B) => A.FullName.CompareTo(B.FullName));
				RootFolderToRulesFileCache[DirectoryToSearch] = Cache;
			}

			// Get the list of files of the type we're looking for
			if (InRulesFileTypeToReturn == RulesCompiler.RulesFileType.Module)
			{
				return Cache.ModuleRules;
			}
			else if (InRulesFileTypeToReturn == RulesCompiler.RulesFileType.Target)
			{
				return Cache.TargetRules;
			}
			else if (InRulesFileTypeToReturn == RulesCompiler.RulesFileType.AutomationModule)
			{
				return Cache.AutomationModules;
			}
			else
			{
				throw new BuildException("Unhandled rules type: {0}", InRulesFileTypeToReturn);
			}
		}

		// Search through a directory tree for any rules files
		private static void FindAllRulesFilesRecursively(DirectoryItem RootDirectoryToSearch, RulesFileCache RecevingRulesFileCache, ThreadPoolWorkQueue Queue)
		{
			// Scan all the files in this directory
			bool bSearchSubFolders = true;
			foreach (FileItem File in RootDirectoryToSearch.EnumerateAllCachedFiles())
			{
				if (File.HasExtension(Tag.Ext.BuildCS))
				{
					lock(RecevingRulesFileCache.ModuleRules)
					{
						RecevingRulesFileCache.ModuleRules.Add(File.FileDirectory);
					}
					bSearchSubFolders = false;
				}
				else if (File.HasExtension(Tag.Ext.TargetCS))
				{
					lock(RecevingRulesFileCache.TargetRules)
					{
						RecevingRulesFileCache.TargetRules.Add(File.FileDirectory);
					}
				}
				else if (File.HasExtension(Tag.Ext.AutomationCharpProject))
				{
					lock(RecevingRulesFileCache.AutomationModules)
					{
						RecevingRulesFileCache.AutomationModules.Add(File.FileDirectory);
					}
					bSearchSubFolders = false;
				}
			}

			// If we didn't find anything to stop the search, search all the subdirectories too
			if (bSearchSubFolders)
			{
				foreach (DirectoryItem SubDirectory in RootDirectoryToSearch.EnumerateSubDirectories())
				{
					Queue.Enqueue(() => FindAllRulesFilesRecursively(SubDirectory, RecevingRulesFileCache, Queue));
				}
			}
		}

		// Find all the module rules files under a given directory
		private static void AddModuleRulesWithContext
		(
            DirectoryReference DirectoryToSearch,
            ModuleRulesContext InModuleRulesContext,
            Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext
		)
		{
			// BaseDirectory = {D:\UERelease\Engine\Source\Programs} -> 43개 *.Build.cs
			IReadOnlyList<FileReference> RulesFiles = FindAllRulesFiles(DirectoryToSearch, RulesFileType.Module);
			foreach (FileReference RulesFile in RulesFiles)
			{
				// ModuleContext = {BuildTool.ModuleRulesContext}
				ModuleFileToContext[RulesFile] = InModuleRulesContext;
			}
		}

		// Find all the module rules files under a given directory
		private static void AddEngineModuleRulesWithContext
		(
			DirectoryReference DirectoryToSearch, 
			string SubDirectoryName, 
			ModuleRulesContext BaseModuleContext, 
			UHTModuleType? DefaultUHTModuleType, 
			Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext
		)
		{
			DirectoryReference Directory = DirectoryReference.Combine(DirectoryToSearch, SubDirectoryName);
			if (DirectoryLookupCache.DirectoryExists(Directory))
			{
				ModuleRulesContext ModuleContext = new ModuleRulesContext(BaseModuleContext) { DefaultUHTModuleType = DefaultUHTModuleType };
				AddModuleRulesWithContext(Directory, ModuleContext, ModuleFileToContext);
			}
		}

		// Creates the engine rules assembly
		// <param name="bUsePrecompiled">Whether to use a precompiled engine</param>
		// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		// <returns>New rules assembly</returns>
		public static RulesAssembly CreateEngineRulesAssembly(bool bUsePrecompiled, bool bSkipCompile)
		{
			if (ProgramRulesDLL_EngineRulesAssembly == null)
			{
				List<PluginInfo> IncludedPlugins = new List<PluginInfo>();

				// search for all engine plugins
				// BuildTool.EngineDirectory = {D:\UERelease\Engine}
				// Addrange *.uplugin (305개)
				IncludedPlugins.AddRange(Plugins.ReadEnginePlugins(BuildTool.EngineDirectory));

				RulesScope EngineScope = new RulesScope(Tag.Scope.Engine /*For Scope*/, null);

				ProgramRulesDLL_EngineRulesAssembly 
					= PrivateCreateEngineOrEnterpriseRulesAssembly
					(
						EngineScope, 
						BuildTool.GetAllEngineDirectories().ToList(), 
						ProjectFileGenerator.EngineProjectFileNameBase, 
						IncludedPlugins, 
						BuildTool.IsEngineInstalled() || bUsePrecompiled, // = bReadOnly
						bSkipCompile, 
						null
					);
			}
			return ProgramRulesDLL_EngineRulesAssembly;
		}

		// Creates the enterprise rules assembly
		public static RulesAssembly CreateEnterpriseRulesAssembly(bool bUsePrecompiledEnterpriseAndEngineFolder, bool bSkipCompile)
		{
			if (EnterpriseRulesAssembly == null)
			{
				RulesAssembly EngineAssembly = CreateEngineRulesAssembly(bUsePrecompiledEnterpriseAndEngineFolder, bSkipCompile);
				if (DirectoryReference.Exists(BuildTool.EnterpriseDirectory))
				{
					RulesScope EnterpriseScope = new RulesScope(Tag.Scope.Enterprise, EngineAssembly.Scope);

					//List<DirectoryReference> EnterpriseDirectories = new List<DirectoryReference>() { BuildTool.EnterpriseDirectory };

					IReadOnlyList<PluginInfo> IncludedPlugins = Plugins.ReadEnterprisePlugins(BuildTool.EnterpriseDirectory);
					EnterpriseRulesAssembly = PrivateCreateEngineOrEnterpriseRulesAssembly
					(
						EnterpriseScope, 
						new List<DirectoryReference>() { BuildTool.EnterpriseDirectory }, 
						ProjectFileGenerator.EnterpriseProjectFileNameBase, 
						IncludedPlugins, 
						BuildTool.IsEnterpriseInstalled() || bUsePrecompiledEnterpriseAndEngineFolder, 
						bSkipCompile, 
						EngineAssembly
					);
				}
				else
				{
					// If we're asked for the enterprise rules assembly but the enterprise directory is missing, fallback on the engine rules assembly
					Log.TraceWarning("Trying to build an enterprise target but the enterprise directory is missing. Falling back on engine components only.");
					return EngineAssembly;
				}
			}

			return EnterpriseRulesAssembly;
		}

		// Creates a rules assembly
		// <param name="Scope">Scope for items created from this assembly</param>
		// <param name="RootDirectories">The root directories to create rules for</param>
		// <param name="AssemblyPrefix">A prefix for the assembly file name</param>
		// <param name="Plugins">List of plugins to include in this assembly</param>
		// <param name="bReadOnly">Whether the assembly should be marked as installed</param> // BuildTool.IsEngineInstalled() || bUsePrecompiled
		// <param name="bSkipCompile">Whether to skip compilation for this assembly</param>
		// <param name="Parent">The parent rules assembly</param>
		// Return ProgramRules.dll (with in EngineRules.dll, ...)
		private static RulesAssembly PrivateCreateEngineOrEnterpriseRulesAssembly
		(
			RulesScope Scope,
			List<DirectoryReference> RootDirectories,
			string AssemblyPrefix,
			IReadOnlyList<PluginInfo> PluginsTobeIncluded,
			bool bReadOnly,
			bool bSkipCompile,
			RulesAssembly ParentRulesAssembly
		)
		{
			// Scope hierarchy
			RulesScope PluginsScope = new RulesScope(Scope.Name + $" {Tag.Scope.Plugins}",  Scope);
			RulesScope ProgramsScope = new RulesScope(Scope.Name + $" {Tag.Scope.Program}", PluginsScope);

			// Find the shared modules, excluding the programs directory. These are used to create an assembly with the bContainsEngineModules flag set to true.
			Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext = new Dictionary<FileReference, ModuleRulesContext>();
			ModuleRulesContext DefaultModuleContext = new ModuleRulesContext(Scope, RootDirectories[0]);

			// ModuleFileToContext에 모두 입력
			// Module - *.Build.cs - .dll 모두 입력
			foreach (DirectoryReference RootDirectory in RootDirectories)
            {
                DirectoryReference SourceDirectory = DirectoryReference.Combine(RootDirectory, Tag.Directory.SourceCode);
                // Directory = {D:\UERelease\Engine\Source\Runtime Developer / Editor / ThirdParty 내의 모든 *.Build.cs를 ModuleFileToContext에 저장 }
                AddEngineModuleRulesWithContext(SourceDirectory, Tag.Directory.EngineCode, DefaultModuleContext, UHTModuleType.EngineRuntime, ModuleFileToContext);
                AddEngineModuleRulesWithContext(SourceDirectory, Tag.Directory.EngineAndEditor, DefaultModuleContext, UHTModuleType.EngineDeveloper, ModuleFileToContext);
                AddEngineModuleRulesWithContext(SourceDirectory, Tag.Directory.EditorOnly, DefaultModuleContext, UHTModuleType.EngineEditor, ModuleFileToContext);
                AddEngineModuleRulesWithContext(SourceDirectory, Tag.Directory.ThirdParty, DefaultModuleContext, UHTModuleType.EngineThirdParty, ModuleFileToContext);
            }

            // Add all the plugin modules too
            // (don't need to loop over RootDirectories since the plugins come in already found
            ModuleRulesContext PluginsModuleContext = new ModuleRulesContext(PluginsScope, RootDirectories[0]);

            // Plugins(*.uplugins)에 해당되는 Module(*.build.cs / *.dll) 모두 ModuleFileToContext에 저장
            FindModuleRulesForPlugins(PluginsTobeIncluded, PluginsModuleContext, ModuleFileToContext);

            // Create the assembly
            DirectoryReference AssemblyDir = RootDirectories[0];

			// EngineAssemblyFileName = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineRules.dll}
			FileReference EngineAssemblyFileName = FileReference.Combine(AssemblyDir, Tag.Directory.Generated, Tag.Directory.Build, Tag.Directory.BuildRules, AssemblyPrefix + Tag.Binary.Rules + Tag.Ext.Dll);
			
			// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineRules.dll"
			RulesAssembly EngineRulesdll_EngineAssembly = new RulesAssembly
			(
				Scope, 
				RootDirectories, 
				PluginsTobeIncluded, 
				ModuleFileToContext,
				new List<FileReference>(), 
				EngineAssemblyFileName, // EngineAssemblyFileName = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineRules.dll}
				bContainsEngineModules: true, 
				DefaultBuildSettings: BuildSettingsVersion.Latest, 
				bReadOnly: bReadOnly, 
				bSkipCompile: bSkipCompile, 
				ParentRulesAssembly: ParentRulesAssembly
			);

			List<FileReference> ProgramTargetFiles = new List<FileReference>();
			Dictionary<FileReference, ModuleRulesContext> ProgramModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
			// RootDirectories
			// [0] = {D:\UERelease\Engine}
			// [1] = {D:\UERelease\Engine\Platforms\XXX}
			foreach (DirectoryReference RootDirectory in RootDirectories)
			{
				DirectoryReference SourceDirectory   = DirectoryReference.Combine(RootDirectory, Tag.Directory.SourceCode);
				DirectoryReference ProgramsDirectory = DirectoryReference.Combine(SourceDirectory, Tag.Directory.ExternalTools);

				// Also create a scope for them, and update the UHT module type
				ModuleRulesContext ProgramsModuleContext = new ModuleRulesContext(ProgramsScope, RootDirectory) { DefaultUHTModuleType = UHTModuleType.Program };

                // Find all the rules files
                AddModuleRulesWithContext(ProgramsDirectory, ProgramsModuleContext, ProgramModuleFiles);
                ProgramTargetFiles.AddRange(FindAllRulesFiles(SourceDirectory, RulesFileType.Target));
            }

            // Create a path to the assembly that we'll either load or compile

            // ProgramAssemblyFileName = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineProgramRules.dll}
            FileReference ProgramAssemblyFileName = FileReference.Combine(AssemblyDir, Tag.Directory.Generated, Tag.Directory.Build, Tag.Directory.BuildRules, AssemblyPrefix + Tag.Binary.ProgramRules + Tag.Ext.Dll);
			RulesAssembly ProgramRulesDLL_Assembly = new RulesAssembly
			(
				ProgramsScope,    // Name = "Engine" -> Name = "Engine Plugins" -> Name = "Engine Programs"
				RootDirectories,  // [0] = {D:\UERelease\Engine}, [1] = {D:\UERelease\Engine\Platforms\XXX}
				new List<PluginInfo>().AsReadOnly(), 
				ProgramModuleFiles,      // ProgramModuleFiles = Count = 43 // [0] = {[{D:\UERelease\Engine\Source\Programs\BenchmarkTool\BenchmarkTool.Build.cs}, {BuildTool.ModuleRulesContext}]}, ...
				ProgramTargetFiles,      // ProgramTargetFiles = Count = 47 // [0] = {D:\UERelease\Engine\Source\Programs\BenchmarkTool\BenchmarkTool.Target.cs}, ... 
				ProgramAssemblyFileName, // ProgramAssemblyFileName = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineProgramRules.dll}
				bContainsEngineModules: false,
				DefaultBuildSettings: BuildSettingsVersion.Latest, 
				bReadOnly: bReadOnly, 
				bSkipCompile: bSkipCompile, 
				ParentRulesAssembly: EngineRulesdll_EngineAssembly
			);

			// Return the combined assembly
			return ProgramRulesDLL_Assembly;
		}

		// Creates a rules assembly with the given parameters.
		// <param name="ProjectFileName"> The project file to create rules for. Null for the engine.</param>
		public static RulesAssembly CreateProjectRulesAssembly(FileReference ProjectFileName, bool bUsePrecompiled, bool bSkipCompile)
		{
			// Check if there's an existing assembly for this project
			if (!LoadedAssemblyMap.TryGetValue(ProjectFileName, out RulesAssembly OutProjectRulesAssembly))
			{
				UProjectDescriptor Project = UProjectDescriptor.FromFile(ProjectFileName);

				// Create the parent assembly
				RulesAssembly Parent;
				if (Project.IsEnterpriseProject)
				{
					Parent = CreateEnterpriseRulesAssembly(bUsePrecompiled, bSkipCompile);
				}
				else
				{
					Parent = CreateEngineRulesAssembly(bUsePrecompiled, bSkipCompile);
				}

				DirectoryReference MainProjectDirectory = ProjectFileName.Directory;
				//DirectoryReference MainProjectSourceDirectory = DirectoryReference.Combine(MainProjectDirectory, "Source");

				// Create a scope for things in this assembly
				RulesScope Scope = new RulesScope(Tag.Scope.Project, Parent.Scope);

				// Create a new context for modules created by this assembly
				ModuleRulesContext DefaultModuleContext = new ModuleRulesContext(Scope, MainProjectDirectory)
				{
					bCanBuildDebugGame = true,
					bCanHotReload = true,
					bClassifyAsGameModuleForUHT = true,
					bCanUseForSharedPCH = false
				};

				Dictionary<FileReference, ModuleRulesContext> AllModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
				List<FileReference> AllTargetFiles = new List<FileReference>();
				List<DirectoryReference> AllProjectDirectories = new List<DirectoryReference>(BuildTool.GetAllProjectDirectories(ProjectFileName));

				if (Project.AdditionalRootDirectories != null)
				{
					AllProjectDirectories.AddRange(Project.AdditionalRootDirectories);
				}

				// Find all the rules/plugins under the project source directories
				foreach (DirectoryReference ProjectDirectory in AllProjectDirectories)
				{
					DirectoryReference ProjectSourceDirectory = DirectoryReference.Combine(ProjectDirectory, Tag.Directory.SourceCode);

					AddModuleRulesWithContext(ProjectSourceDirectory, DefaultModuleContext, AllModuleFiles);
					AllTargetFiles.AddRange(FindAllRulesFiles(ProjectSourceDirectory, RulesFileType.Target));
				}

				// Find all the project plugins
				List<PluginInfo> ProjectPluginInfos = new List<PluginInfo>();
				ProjectPluginInfos.AddRange(Plugins.ReadProjectPlugins(MainProjectDirectory));

				// Add the project's additional plugin directories plugins too
				if (Project.AdditionalPluginDirectories != null)
				{
					foreach (DirectoryReference AdditionalPluginDirectory in Project.AdditionalPluginDirectories)
					{
						ProjectPluginInfos.AddRange(Plugins.ReadAdditionalPlugins(AdditionalPluginDirectory));
					}
				}

				// Find all the plugin module rules
				FindModuleRulesForPlugins(ProjectPluginInfos, DefaultModuleContext, AllModuleFiles);

				// Add the games project's intermediate source folder
				DirectoryReference ProjectIntermediateSourceDirectory = DirectoryReference.Combine(MainProjectDirectory, Tag.Directory.Generated, Tag.Directory.SourceCode);

				if (DirectoryReference.Exists(ProjectIntermediateSourceDirectory))
				{
					AddModuleRulesWithContext(ProjectIntermediateSourceDirectory, DefaultModuleContext, AllModuleFiles);
					AllTargetFiles.AddRange(FindAllRulesFiles(ProjectIntermediateSourceDirectory, RulesFileType.Target));
				}

				// Compile the assembly. If there are no module or target files, just use the parent assembly.
				FileReference AssemblyFileName = FileReference.Combine(MainProjectDirectory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Directory.BuildRules, ProjectFileName.GetFileNameWithoutExtension() + Tag.Binary.ModuleRules + Tag.Ext.Dll);

				if (AllModuleFiles.Count == 0 && AllTargetFiles.Count == 0)
				{
					OutProjectRulesAssembly = Parent;
				}
				else
				{
					OutProjectRulesAssembly = new RulesAssembly(Scope, new List<DirectoryReference> { MainProjectDirectory }, ProjectPluginInfos, AllModuleFiles, AllTargetFiles, AssemblyFileName, bContainsEngineModules: false, DefaultBuildSettings: null, bReadOnly: BuildTool.IsProjectInstalled(), bSkipCompile: bSkipCompile, ParentRulesAssembly: Parent);
				}

				LoadedAssemblyMap.Add(ProjectFileName, OutProjectRulesAssembly);
			}

			return OutProjectRulesAssembly;
		}

		// Creates a rules assembly with the given parameters.
        public static RulesAssembly CreatePluginRulesAssembly
		(
			FileReference PluginFileNameToCreate,
			RulesAssembly ParentRulesAssembly,
			bool          bSkipCompile,
			bool          bContainsEngineModules
		)
		{
			// Check if there's an existing assembly for this project
			if (!LoadedAssemblyMap.TryGetValue(PluginFileNameToCreate, out RulesAssembly PluginRulesAssembly))
			{
				// Find all the rules source files
				Dictionary<FileReference, ModuleRulesContext> ModuleFiles = new Dictionary<FileReference, ModuleRulesContext>();
				List<FileReference> TargetFiles = new List<FileReference>();

				// Create a list of plugins for this assembly. If it already exists in the parent assembly, just create an empty assembly.
				List<PluginInfo> ForeignPlugins = new List<PluginInfo>();
				if (ParentRulesAssembly == null || 
					!ParentRulesAssembly.EnumeratePlugins().Any(x => x.File == PluginFileNameToCreate))
				{
					ForeignPlugins.Add(new PluginInfo(PluginFileNameToCreate, PluginType.External));
				}

				// Create a new scope for the plugin. It should not reference anything else.
				RulesScope Scope = new RulesScope(Tag.Scope.Plugin, ParentRulesAssembly.Scope);

                // Find all the modules
                ModuleRulesContext PluginModuleContext = new ModuleRulesContext(Scope, PluginFileNameToCreate.Directory)
				{ bClassifyAsGameModuleForUHT = !bContainsEngineModules };

                FindModuleRulesForPlugins(ForeignPlugins, PluginModuleContext, ModuleFiles);

				// Compile the assembly
				FileReference AssemblyFileName = FileReference.Combine(PluginFileNameToCreate.Directory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Directory.BuildRules, Path.GetFileNameWithoutExtension(PluginFileNameToCreate.FullName) + Tag.Binary.ModuleRules + Tag.Ext.Dll);
				PluginRulesAssembly = new RulesAssembly(Scope, new List<DirectoryReference> { PluginFileNameToCreate.Directory }, ForeignPlugins, ModuleFiles, TargetFiles, AssemblyFileName, bContainsEngineModules, DefaultBuildSettings: null, bReadOnly: false, bSkipCompile: bSkipCompile, ParentRulesAssembly: ParentRulesAssembly);
				LoadedAssemblyMap.Add(PluginFileNameToCreate, PluginRulesAssembly);
			}
			return PluginRulesAssembly;
		}

		public static RulesAssembly CreateTargetRulesAssembly
		(
			FileReference BeingCompiledProjectFile,
			string BeingBuiltTargetName,
			bool bSkipRulesCompile,
			bool bUsePrecompiled,
			FileReference ForeignPlugin
		)
		{
			RulesAssembly RulesAssembly;
			if (BeingCompiledProjectFile != null)
			{
				RulesAssembly = CreateProjectRulesAssembly(BeingCompiledProjectFile, bUsePrecompiled, bSkipRulesCompile);
			}
			else
			{
				RulesAssembly = CreateEngineRulesAssembly(bUsePrecompiled, bSkipRulesCompile);

				if (RulesAssembly.GetTargetFileName(BeingBuiltTargetName) == null && 
					DirectoryReference.Exists(BuildTool.EnterpriseDirectory))
				{
					// Target isn't part of the engine assembly, try the enterprise assembly
					RulesAssembly = CreateEnterpriseRulesAssembly(bUsePrecompiled, bSkipRulesCompile);
				}
			}
			if (ForeignPlugin != null)
			{
				RulesAssembly = CreatePluginRulesAssembly(ForeignPlugin, RulesAssembly, bSkipRulesCompile, true);
			}
			return RulesAssembly;
		}

		// Finds all the module rules for plugins under the given directory.
		private static void FindModuleRulesForPlugins
		(
			IReadOnlyList<PluginInfo>                     SearchTargetPlugins, 
			ModuleRulesContext                            PrototypeContext, 
			Dictionary<FileReference, ModuleRulesContext> OutFileToModuleRulesContext
		)
		{
			PrefetchRulesFiles(SearchTargetPlugins.Select(x => DirectoryReference.Combine(x.RootDirectory, Tag.Directory.SourceCode)));

			foreach (PluginInfo Plugin in SearchTargetPlugins)
			{
				// *.uplugin내에(해당되는) 모듈(*.build.cs / *.dll) 찾기
				List<FileReference> PluginModuleFiles = FindAllRulesFiles(DirectoryReference.Combine(Plugin.RootDirectory, Tag.Directory.SourceCode), RulesFileType.Module).ToList();

				foreach (FileReference ChildFile in Plugin.ChildFiles)
				{
					PluginModuleFiles.AddRange(FindAllRulesFiles(DirectoryReference.Combine(ChildFile.Directory, Tag.Directory.SourceCode), RulesFileType.Module));
				}

				foreach (FileReference PluginModuleFile in PluginModuleFiles)
				{
					ModuleRulesContext PluginContext = new ModuleRulesContext(PrototypeContext)
					{
						DefaultOutputBaseDir = Plugin.RootDirectory,
						ModulePluginInfo     = Plugin
					};
					OutFileToModuleRulesContext[PluginModuleFile] = PluginContext;
				}
			}
		}

		// Gets the filename that declares the given type.
		public static string GetFileNameFromType(Type ExistingType)
		{
			if (ProgramRulesDLL_EngineRulesAssembly != null && 
				ProgramRulesDLL_EngineRulesAssembly.TryGetFileNameFromType(ExistingType, out FileReference OutFileName))
			{
				return OutFileName.FullName;
			}
			else if (EnterpriseRulesAssembly != null && EnterpriseRulesAssembly.TryGetFileNameFromType(ExistingType, out OutFileName))
			{
				return OutFileName.FullName;
			}

			foreach (RulesAssembly RulesAssembly in LoadedAssemblyMap.Values)
			{
				if (RulesAssembly.TryGetFileNameFromType(ExistingType, out OutFileName))
				{
					return OutFileName.FullName;
				}
			}
			return null;
		}
    }
}
