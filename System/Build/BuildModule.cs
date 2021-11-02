using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// A unit of code compilation and linking.
	internal abstract class BuildModule
	{
		public readonly ModuleRules ModuleRule;
		public string ModuleRuleFileName => ModuleRule.Name; // The name that uniquely identifies the module.
		public DirectoryReference ModuleDirectory => ModuleRule.Directory; // Path to the module directory

		public FileReference RulesFile => ModuleRule.File; // The name of the .Build.cs file this module was created from, if any

		public static DirectoryReference GeneratedDirectory { get; set; }  // The directory for this module's object files
		public DirectoryReference[] ModuleDirectories; // Paths to all potential module source directories (with platform extension directories added in)

		// The binary the module will be linked into for the current target.  
		// Only set after UEBuildBinary.BindModules is called.
		public BuildBinary Binary = null;
		
		protected readonly string ModuleApiDefine; // The name of the _API define for this module
		public readonly HashSet<string> PublicDefinitions; // Set of all the public definitions
		public readonly HashSet<DirectoryReference> PublicIncludePaths; // Set of all public include paths

		// Nested public include paths which used to be added automatically,
		// but are now only added for modules with bNestedPublicIncludePaths set.
		public readonly HashSet<DirectoryReference> LegacyPublicIncludePaths = new HashSet<DirectoryReference>();

		public readonly HashSet<DirectoryReference> PublicSystemIncludePaths; // Set of all system include paths
		public readonly HashSet<DirectoryReference> PrivateIncludePaths; // Set of all private include paths

		public List<BuildModule> PublicDependencyModules;  // Names of modules that this module's public interface depends on.
		public List<BuildModule> PrivateDependencyModules; // Names of modules that this module's private implementation depends on.

		public List<BuildModule> PublicIncludePathModules;  // Names of modules with header files that this module's public interface needs access to.
		public List<BuildModule> PrivateIncludePathModules; // Names of modules with header files that this module's private implementation needs access to.

		public readonly HashSet<DirectoryReference> PublicSystemLibraryPaths; // Set of all public system library paths
		public readonly HashSet<string> PublicAdditionalLibraries; // Set of all additional libraries
		public readonly HashSet<string> PublicSystemLibraries; // Set of all system libraries
		public readonly HashSet<string> PublicFrameworks; // Set of additional frameworks
		public readonly HashSet<string> PublicWeakFrameworks;

		public HashSet<string> PublicDelayLoadDLLs; // Names of DLLs that this module should delay load

		public List<BuildModule> DynamicallyLoadedModules; // Extra modules this module may require at run time

		protected readonly HashSet<BuildFramework> PublicAdditionalFrameworks;
		protected readonly HashSet<BuildBundleResource> PublicAdditionalBundleResources;

		private readonly HashSet<DirectoryReference> WhitelistRestrictedFolders; // Set of all whitelisted restricted folder references
		public readonly Dictionary<string, string> AliasRestrictedFolders; // Set of aliased restricted folder references

		public BuildModule(ModuleRules InModuleRules, DirectoryReference InGeneratedDirectory)
		{
			this.ModuleRule = InModuleRules;

			// PlugIn -> ($EngineDir)\($PlugInDir)\($EnterpriseDir)\($ModuleName)\Intermediate\ !$*
			if (GeneratedDirectory == null)
			{
                if (InGeneratedDirectory != null)
                {
                    GeneratedDirectory = InGeneratedDirectory;
                }
                else
                {
					GeneratedDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated);
				}
            }

			ModuleApiDefine = ModuleRuleFileName.ToUpperInvariant() + Tag.CppContents.Def.API;

			PublicIncludePaths        = CreateDirectoryHashSet(InModuleRules.PublicIncludePaths);
			PublicSystemIncludePaths  = CreateDirectoryHashSet(InModuleRules.PublicSystemIncludePaths);
			PublicSystemLibraryPaths  = CreateDirectoryHashSet(InModuleRules.PublicSystemLibraryPaths);
			PublicDefinitions         = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicDefinitions);
			PublicAdditionalLibraries = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicAdditionalLibraries);
			PublicSystemLibraries     = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicSystemLibraries);
			PublicFrameworks          = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicFrameworks);
			PublicWeakFrameworks      = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicWeakFrameworks);

			foreach (string LibraryName in PublicAdditionalLibraries)
			{
				// if the library path is fully qualified we just add it, this is the preferred method of adding a library
				if (File.Exists(LibraryName))
				{
					continue;
				}

				// the library path does not seem to be resolvable as is, lets warn about it as dependency checking will not work for it
				Log.TraceWarning("Library '{0}' was not resolvable to a file when used in Module '{1}', " +
					"assuming it is a filename and will search library paths for it. This is slow and dependency checking will not work for it. " +
					"Please update reference to be fully qualified alternatively use PublicSystemLibraryPaths " +
					"if you do intended to use this slow path to suppress this warning. ", LibraryName, ModuleRuleFileName);
			}

			PublicAdditionalFrameworks = new HashSet<BuildFramework>();

			if(InModuleRules.PublicAdditionalFrameworks != null)
			{
				foreach(ModuleRules.Framework FrameworkRules in InModuleRules.PublicAdditionalFrameworks)
				{
					BuildFramework Framework;
					if(String.IsNullOrEmpty(FrameworkRules.ZipPath))
					{
						Framework = new BuildFramework(FrameworkRules.FrameworkName, FrameworkRules.CopyBundledAssets);
					}
					else
					{
						Framework = new BuildFramework
                        (
                            FrameworkRules.FrameworkName,
                            FileReference.Combine(ModuleDirectory, FrameworkRules.ZipPath),
                            DirectoryReference.Combine
							(
								BuildTool.EngineDirectory, 
								Tag.Directory.Generated, 
								Tag.Directory.UnzippedFrameworks, 
								FrameworkRules.FrameworkName, 
								Path.GetFileNameWithoutExtension(FrameworkRules.ZipPath)
							),
                            FrameworkRules.CopyBundledAssets
                        );
                    }

					PublicAdditionalFrameworks.Add(Framework);
				}
			}

			PublicAdditionalBundleResources = InModuleRules.AdditionalBundleResources == null ? 
				new HashSet<BuildBundleResource>() : 
				new HashSet<BuildBundleResource>(InModuleRules.AdditionalBundleResources.Select(x => new BuildBundleResource(x)));

			PublicDelayLoadDLLs = HashSetFromOptionalEnumerableStringParameter(InModuleRules.PublicDelayLoadDLLs);

			if(InModuleRules.bUsePrecompiled)
			{
				PrivateIncludePaths = new HashSet<DirectoryReference>();
			}
			else
			{
				PrivateIncludePaths = CreateDirectoryHashSet(InModuleRules.PrivateIncludePaths);
			}

			WhitelistRestrictedFolders = new HashSet<DirectoryReference>(InModuleRules.WhitelistRestrictedFolders.Select(x => DirectoryReference.Combine(ModuleDirectory, x)));
			AliasRestrictedFolders     = new Dictionary<string, string>(InModuleRules.AliasRestrictedFolders);

			// merge the main directory and any others set in the Rules
			List<DirectoryReference> MergedDirectories  = new List<DirectoryReference> { ModuleDirectory };
			DirectoryReference[] ExtraModuleDirectories = InModuleRules.GetModuleDirectoriesForAllSubClasses();

			if (ExtraModuleDirectories != null)
			{
				MergedDirectories.AddRange(ExtraModuleDirectories);
			}

			// cache the results (it will always at least have the ModuleDirectory)
			ModuleDirectories = MergedDirectories.ToArray();
		}

		// Determines if a file is part of the given module
		public virtual bool ContainsFile(FileReference FileLocation) => FileLocation.IsUnderDirectory(ModuleDirectory);

		// Returns a list of this module's dependencies.
		// <returns>An enumerable containing the dependencies of the module.</returns>
		public HashSet<BuildModule> GetAllModules(bool bWithIncludePathModules, bool bWithDynamicallyLoadedModules)
		{
			HashSet<BuildModule> Modules = new HashSet<BuildModule>();
			Modules.UnionWith(PublicDependencyModules);
			Modules.UnionWith(PrivateDependencyModules);

			if(bWithIncludePathModules)
			{
				Modules.UnionWith(PublicIncludePathModules);
				Modules.UnionWith(PrivateIncludePathModules);
			}

			if(bWithDynamicallyLoadedModules)
			{
				Modules.UnionWith(DynamicallyLoadedModules);
			}
			return Modules;
        }

		// Returns a list of this module's frameworks.
		public List<string> GetPublicFrameworks() => new List<string>(PublicFrameworks);

		// Converts an optional string list parameter to a well-defined hash set.
		protected HashSet<DirectoryReference> CreateDirectoryHashSet(IEnumerable<string> InEnumerableStrings)
		{
			HashSet<DirectoryReference> Directories = new HashSet<DirectoryReference>();
			if(InEnumerableStrings != null)
			{
				foreach(string InputString in InEnumerableStrings)
				{
					DirectoryReference Dir = new DirectoryReference(ExpandPathVariables(InputString, null, null));
					if(DirectoryLookupCache.DirectoryExists(Dir))
					{
						Directories.Add(Dir);
					}
					else
					{
						Log.WriteLineOnce(LogEventType.Warning, LogFormatOptions.NoSeverityPrefix, "{0}: warning: Referenced directory '{1}' does not exist.", RulesFile, Dir);
					}
				}
			}
			return Directories;
		}

		// Converts an optional string list parameter to a well-defined hash set.
		protected HashSet<string> HashSetFromOptionalEnumerableStringParameter(IEnumerable<string> InEnumerableStrings)
		{
			return InEnumerableStrings == null ? new HashSet<string>() : new HashSet<string>(InEnumerableStrings.Select(x => ExpandPathVariables(x, null, null)));
		}

		// Determines whether this module has a circular dependency on the given module
		public bool HasCircularDependencyOn(string ModuleName) => ModuleRule.CircularlyReferencedDependentModules.Contains(ModuleName);

		// Enumerates additional build products which may be produced by this module. 
		// Some platforms (eg. Mac, Linux) can link directly against .so/.dylibs, 
		// but they are also copied to the output folder by the toolchain.
		public void GatherAdditionalResources(List<string> LibrariesRequiredBythisModule, List<BuildBundleResource> BundleResources)
		{
			LibrariesRequiredBythisModule.AddRange(PublicAdditionalLibraries);
			LibrariesRequiredBythisModule.AddRange(PublicSystemLibraries);
			BundleResources.AddRange(PublicAdditionalBundleResources);
		}

		// Determines the distribution level of a module based on its directory and includes.
		public Dictionary<RestrictedFolder, DirectoryReference> FindRestrictedFolderReferences(List<DirectoryReference> RootDirectories)
		{
			Dictionary<RestrictedFolder, DirectoryReference> References = new Dictionary<RestrictedFolder, DirectoryReference>();
			if (!ModuleRule.bLegalToDistributeObjectCode)
			{
				// Find all the directories that this module references
				HashSet<DirectoryReference> ReferencedDirs = new HashSet<DirectoryReference>();
				GetReferencedDirectories(ReferencedDirs);

				// Remove all the whitelisted folders
				ReferencedDirs.ExceptWith(WhitelistRestrictedFolders);
				ReferencedDirs.ExceptWith(PublicDependencyModules .SelectMany(x => x.WhitelistRestrictedFolders));
				ReferencedDirs.ExceptWith(PrivateDependencyModules.SelectMany(x => x.WhitelistRestrictedFolders));

				// Add flags for each of them
				foreach(DirectoryReference ReferencedDir in ReferencedDirs)
				{
					// Find the base directory containing this reference
					DirectoryReference BaseDir = RootDirectories.FirstOrDefault(x => ReferencedDir.IsUnderDirectory(x));

					// @todo platplug does this need to check platform extension engine directories? what are ReferencedDir's here?
					if (BaseDir == null)
					{
						continue;
					}

					// Add references to each of the restricted folders
					List<RestrictedFolder> Folders = RestrictedFolders.FindRestrictedFolders(BaseDir, ReferencedDir);
					foreach(RestrictedFolder Folder in Folders)
					{
						if(!References.ContainsKey(Folder))
						{
							References.Add(Folder, ReferencedDir);
						}
					}
				}
			}

			return References;
		}

		// Finds all the directories that this folder references when building
		protected virtual void GetReferencedDirectories(HashSet<DirectoryReference> OutDirectories)
		{
			OutDirectories.Add(ModuleDirectory);

			foreach(DirectoryReference PublicIncludePath in PublicIncludePaths)
			{
				OutDirectories.Add(PublicIncludePath);
			}
			foreach(DirectoryReference PrivateIncludePath in PrivateIncludePaths)
			{
				OutDirectories.Add(PrivateIncludePath);
			}
			foreach(DirectoryReference PublicSystemIncludePath in PublicSystemIncludePaths)
			{
				OutDirectories.Add(PublicSystemIncludePath);
			}
			foreach (DirectoryReference PublicSystemLibraryPath in PublicSystemLibraryPaths)
			{
				OutDirectories.Add(PublicSystemLibraryPath);
			}
		}

        // Find all the modules which affect the private compile environment.
		protected void FindModulesInPrivateCompileEnvironment(Dictionary<BuildModule, bool> ModuleToIncludePathsOnlyFlag)
		{
			// Add in all the modules that are only in the private compile environment
			foreach (BuildModule PrivateDependencyModule in PrivateDependencyModules)
			{
				PrivateDependencyModule.RecursivelyFindModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
			}
			foreach (BuildModule PrivateIncludePathModule in PrivateIncludePathModules)
			{
				PrivateIncludePathModule.RecursivelyFindIncludePathModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
			}

			// Add the modules in the public compile environment
			RecursivelyFindModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
		}

		// Find all the modules which affect the public compile environment.
		protected void RecursivelyFindModulesInPublicCompileEnvironment(Dictionary<BuildModule, bool> ModuleToIncludePathsOnlyFlag)
		{
			if (ModuleToIncludePathsOnlyFlag.TryGetValue(this, out bool bModuleIncludePathsOnly) && !bModuleIncludePathsOnly)
			{
				return;
			}

			ModuleToIncludePathsOnlyFlag[this] = false;

			foreach (BuildModule DependencyModule in PublicDependencyModules)
			{
				DependencyModule.RecursivelyFindModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
			}

			// Now add an include paths from modules with header files that we need access to, but won't necessarily be importing
			foreach (BuildModule IncludePathModule in PublicIncludePathModules)
			{
				IncludePathModule.RecursivelyFindIncludePathModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
			}
		}

		// Find all the modules which affect the public compile environment.
		protected void RecursivelyFindIncludePathModulesInPublicCompileEnvironment(Dictionary<BuildModule, bool> ModuleToIncludePathsOnlyFlag)
		{
			if (!ModuleToIncludePathsOnlyFlag.ContainsKey(this))
			{
				// Add this module to the list
				ModuleToIncludePathsOnlyFlag.Add(this, true);

				// Include any of its public include path modules in the compile environment too
				foreach (BuildModule IncludePathModule in PublicIncludePathModules)
				{
					IncludePathModule.RecursivelyFindIncludePathModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);
				}
			}
		}

		private void AddIncludePaths(HashSet<DirectoryReference> OutIncludePaths, HashSet<DirectoryReference> IncludePathsToAdd)
		{
			// Need to check whether directories exist to avoid bloating compiler command line with generated code directories
			foreach(DirectoryReference IncludePathToAdd in IncludePathsToAdd)
			{
				OutIncludePaths.Add(IncludePathToAdd);
			}
		}

		// 여기에서 #define CORE_API DLLEXPORT 매크로정의하는게 확실한듯. 
		// Sets up the environment for compiling any module that includes the public interface of this module.
		public virtual void AddModuleToCompileEnvironment
		(
			BuildBinary                 SourceBinary,
			HashSet<DirectoryReference> OutIncludePaths,
			HashSet<DirectoryReference> OutSystemIncludePaths,
			List<string>                OutDefinitions,
			List<BuildFramework>        OutAdditionalFrameworks,
			List<FileItem>              OutAdditionalPrerequisites,
			bool                        bLegacyPublicIncludePaths
		)
		{
			// Add the module's parent directory to the include path,
			// so we can root #includes from generated source files to it
			OutIncludePaths.Add(ModuleDirectory.ParentDirectory);

			// Add this module's public include paths and definitions.
			AddIncludePaths(OutIncludePaths, PublicIncludePaths);

			if(bLegacyPublicIncludePaths)
			{
				AddIncludePaths(OutIncludePaths, LegacyPublicIncludePaths);
			}

			OutSystemIncludePaths.UnionWith(PublicSystemIncludePaths);
			OutDefinitions.AddRange(PublicDefinitions);

			// Add the additional frameworks so that the compiler can know about their #include paths
			OutAdditionalFrameworks.AddRange(PublicAdditionalFrameworks);

			// Add the import or export declaration for the module
			// ModuleRules.SymbolVisibility.VisibileForDll
			if (ModuleRule.Type == ModuleRules.ModuleType.CPlusPlus)
			{
				if(ModuleRule.Target.LinkType == TargetLinkType.Monolithic)
				{
					if (ModuleRule.Target.bShouldCompileAsDLL && 
					   (ModuleRule.Target.bHasExports || ModuleRule.ModuleSymbolVisibility == ModuleRules.SymbolVisibility.VisibileForDll))
					{
						OutDefinitions.Add(ModuleApiDefine + Tag.CppContents.Def.DllExport);
					}
					else
					{
						OutDefinitions.Add(ModuleApiDefine + Tag.CppContents.Def.AssignEmpty);
					}
				}
				else if(Binary == null || SourceBinary != Binary)
				{
					OutDefinitions.Add(ModuleApiDefine + Tag.CppContents.Def.DllImport);
				}
				else if(!Binary.bAllowExports)
				{
					OutDefinitions.Add(ModuleApiDefine + Tag.CppContents.Def.AssignEmpty);
				}
#error
				else // if (Binary       != null   &&
					 //     SourceBinary == Binary &&
					 //     Binary.bAllowExports)
				{
					OutDefinitions.Add(ModuleApiDefine + Tag.CppContents.Def.DllExport);
				}
			}

			// Add any generated type library headers
			if (0 < ModuleRule.TypeLibraries.Count)
			{
				OutIncludePaths.Add(GeneratedDirectory);

				foreach (ModuleRules.TypeLibrary TypeLibrary in ModuleRule.TypeLibraries)
				{
					OutAdditionalPrerequisites.Add(FileItem.GetItemByFileReference(FileReference.Combine(GeneratedDirectory, TypeLibrary.Header)));
				}
			}
		}

		// Sets up the environment for compiling this module.
		protected virtual void SetupPrivateCompileEnvironment
		(
			HashSet<DirectoryReference> IncludePaths,
			HashSet<DirectoryReference> SystemIncludePaths,
			List<string>                Definitions,
			List<BuildFramework>        AdditionalFrameworks,
			List<FileItem>              AdditionalPrerequisites,
			bool                        bWithLegacyPublicIncludePaths
		)
		{
			if (!ModuleRule.bTreatAsEngineModule)
			{
				Definitions.Add(Tag.CppContents.Def.DeprecatedForGame + "=" + Tag.CppContents.Def.DeprecatedValue);
			}

			// Add this module's private include paths and definitions.
			IncludePaths.UnionWith(PrivateIncludePaths);

			// Find all the modules that are part of the public compile environment for this module.
			Dictionary<BuildModule, bool> ModuleToIncludePathsOnlyFlag = new Dictionary<BuildModule, bool>();
			FindModulesInPrivateCompileEnvironment(ModuleToIncludePathsOnlyFlag);

			// Now set up the compile environment for the modules in the original order that we encountered them
			foreach (BuildModule Module in ModuleToIncludePathsOnlyFlag.Keys)
			{
				// Module.Binary.bAllowExport가 참일 때
				// ModuleApiDefine#_API 매크로가 export가 됨.
				Module.AddModuleToCompileEnvironment(Binary, IncludePaths, SystemIncludePaths, Definitions, AdditionalFrameworks, AdditionalPrerequisites, bWithLegacyPublicIncludePaths);
			}
		}

		// Expand path variables within the context of this module
		public string ExpandPathVariables(string PathToExpand, DirectoryReference BinaryOutputDir = null, DirectoryReference TargetOutputDir = null)
		{
			if(PathToExpand.StartsWith("$(", StringComparison.Ordinal))
			{
				int StartIdx = 2;
				for(int EndIdx = StartIdx; EndIdx < PathToExpand.Length; ++EndIdx)
				{
					if(PathToExpand[EndIdx] == ')')
					{
						if(MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(BuildTool.EngineDirectory)))
						{
							PathToExpand = BuildTool.EngineDirectory + PathToExpand.Substring(EndIdx + 1);
						}
						else if(MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(ReadOnlyTargetRules.ProjectFile) + nameof(FileReference.Directory)))
						{
							if(ModuleRule.Target.ProjectFile == null)
							{
								PathToExpand = BuildTool.EngineDirectory + PathToExpand.Substring(EndIdx + 1);
							}
							else
							{
								PathToExpand = ModuleRule.Target.ProjectFile.Directory + PathToExpand.Substring(EndIdx + 1);
							}
						}
						else if(MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(ModuleRules.ModuleDirectory)))
						{
							PathToExpand = ModuleRule.ModuleDirectory + PathToExpand.Substring(EndIdx + 1);
						}
						else if(MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(ModuleRules.PluginDirectory)))
						{
							PathToExpand = ModuleRule.PluginDirectory + PathToExpand.Substring(EndIdx + 1);
						}
						else if(BinaryOutputDir != null && MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(BinaryOutputDir)))
						{
							PathToExpand = BinaryOutputDir.FullName + PathToExpand.Substring(EndIdx + 1);
						}
						else if(TargetOutputDir != null && MatchVariableName(PathToExpand, StartIdx, EndIdx, nameof(TargetOutputDir)))
						{
							PathToExpand = TargetOutputDir.FullName + PathToExpand.Substring(EndIdx + 1);
						}
						else
						{
							string EnvVarName = PathToExpand.Substring(StartIdx, EndIdx - StartIdx);
							string EnvVarValue = Environment.GetEnvironmentVariable(EnvVarName);

							if(String.IsNullOrEmpty(EnvVarValue))
							{
								throw new BuildException("Environment variable '{0}' is not defined (referenced by {1})", EnvVarName, ModuleRule.File);
							}

							PathToExpand = EnvVarValue + PathToExpand.Substring(EndIdx + 1);
						}

						break;
					}
				}
			}
			return PathToExpand;
		}

		// Match a variable name within a path
		private bool MatchVariableName(string PathVariable, int StartIdx, int EndIdx, string VariableNameToCompare)
		{
			return VariableNameToCompare.Length == EndIdx - StartIdx && 
				String.Compare(PathVariable, StartIdx, VariableNameToCompare, 0, EndIdx - StartIdx) == 0;
		}

		// Expand path variables within the context of this module
		private IEnumerable<string> ExpandPathVariables(IEnumerable<string> PathsToExpandVariableWithIn, DirectoryReference BinaryDir = null, DirectoryReference ExeDir = null)
		{
			foreach(string Path in PathsToExpandVariableWithIn)
			{
				yield return ExpandPathVariables(Path, BinaryDir, ExeDir);
			}
		}

		// Sets up the environment for linking any module
		// that includes the public interface of this module.
		protected virtual void RecusivelySetupPublicLinkEnvironment
		(
			BuildBinary               SourceBinary,
			List<DirectoryReference>  LibraryPaths,
			List<string>              AdditionalLibraries,
			List<string>              RuntimeLibraryPaths,
			List<string>              Frameworks,
			List<string>              WeakFrameworks,
			List<BuildFramework>      AdditionalFrameworks,
			List<BuildBundleResource> AdditionalBundleResources,
			List<string>              DelayLoadDLLs,
			List<BuildBinary>         BinaryDependencies,
			HashSet<BuildModule>      VisitedModules,
			DirectoryReference        ExeDir
		)
		{
			// There may be circular dependencies in compile dependencies, so we need to avoid reentrance.
			if (VisitedModules.Add(this))
			{
				// Add this module's binary to the binary dependencies.
				if (Binary != null && 
					Binary != SourceBinary && 
					!BinaryDependencies.Contains(Binary))
				{
					BinaryDependencies.Add(Binary);
				}

				// If this module belongs to a static library that we are not currently building, recursively add the link environment settings for all of its dependencies too.
				// Keep doing this until we reach a module that is not part of a static library (or external module, since they have no associated binary).
				// Static libraries do not contain the symbols for their dependencies, so we need to recursively gather them to be linked into other binary types.
				bool bIsBuildingAStaticLibrary     = (SourceBinary != null && SourceBinary.Type == BuildBinaryType.StaticLibrary);
				bool bIsModuleBinaryAStaticLibrary = (Binary       != null && Binary.Type       == BuildBinaryType.StaticLibrary);

				if (!bIsBuildingAStaticLibrary && bIsModuleBinaryAStaticLibrary)
				{
					// Gather all dependencies and recursively call SetupPublicLinkEnvironmnet
					List<BuildModule> AllDependencyModules = new List<BuildModule>();
					AllDependencyModules.AddRange(PrivateDependencyModules);
					AllDependencyModules.AddRange(PublicDependencyModules);

					foreach (BuildModule DependencyModule in AllDependencyModules)
					{
						bool bIsExternalModule = (DependencyModule as BuildModuleExternal != null);
						bool bIsInStaticLibrary = (DependencyModule.Binary != null && DependencyModule.Binary.Type == BuildBinaryType.StaticLibrary);
						if (bIsExternalModule || bIsInStaticLibrary)
						{
							DependencyModule.RecusivelySetupPublicLinkEnvironment
							(
								SourceBinary, 
								LibraryPaths, 
								AdditionalLibraries, 
								RuntimeLibraryPaths, 
								Frameworks, 
								WeakFrameworks,
								AdditionalFrameworks, 
								AdditionalBundleResources, 
								DelayLoadDLLs, 
								BinaryDependencies, 
								VisitedModules, 
								ExeDir
							);
						}
					}
				}

				// Add this module's public include library paths and additional libraries.
				LibraryPaths             .AddRange(PublicSystemLibraryPaths);
				AdditionalLibraries      .AddRange(PublicAdditionalLibraries);
				AdditionalLibraries      .AddRange(PublicSystemLibraries);
				RuntimeLibraryPaths      .AddRange(ExpandPathVariables(ModuleRule.PublicRuntimeLibraryPaths, SourceBinary.OutputDir, ExeDir));
				Frameworks               .AddRange(PublicFrameworks);
				WeakFrameworks           .AddRange(PublicWeakFrameworks);
				AdditionalBundleResources.AddRange(PublicAdditionalBundleResources);
				AdditionalFrameworks     .AddRange(PublicAdditionalFrameworks);
				DelayLoadDLLs            .AddRange(PublicDelayLoadDLLs);
			}
		}

		// Sets up the environment for linking this module.
		public virtual void SetupPrivateLinkEnvironment
		(
			BuildBinary          SourceBinary,
			LinkEnvironment      LinkEnvironment,
			List<BuildBinary>    BinaryDependencies,
			HashSet<BuildModule> VisitedModules,
			DirectoryReference   ExeDir
		)
		{
			// Add the private rpaths
			LinkEnvironment.RuntimeLibraryPaths.AddRange(ExpandPathVariables(ModuleRule.PrivateRuntimeLibraryPaths, SourceBinary.OutputDir, ExeDir));

			// Allow the module's public dependencies to add library paths and additional libraries to the link environment.
			RecusivelySetupPublicLinkEnvironment
			(
				SourceBinary, 
				LinkEnvironment.LibraryPaths, 
				LinkEnvironment.AdditionalLibraries, 
				LinkEnvironment.RuntimeLibraryPaths, 
				LinkEnvironment.Frameworks, 
				LinkEnvironment.WeakFrameworks,
				LinkEnvironment.AdditionalFrameworks, 
				LinkEnvironment.AdditionalBundleResources, 
				LinkEnvironment.DelayLoadDLLs, 
				BinaryDependencies,
				VisitedModules, 
				ExeDir
			);

			// Also allow the module's public and private dependencies to modify the link environment.
			List<BuildModule> AllDependencyModules = new List<BuildModule>();
			AllDependencyModules.AddRange(PrivateDependencyModules);
			AllDependencyModules.AddRange(PublicDependencyModules);

			foreach (BuildModule DependencyModule in AllDependencyModules)
			{
				DependencyModule.RecusivelySetupPublicLinkEnvironment
				(
					SourceBinary, 
					LinkEnvironment.LibraryPaths, 
					LinkEnvironment.AdditionalLibraries, 
					LinkEnvironment.RuntimeLibraryPaths, 
					LinkEnvironment.Frameworks, 
					LinkEnvironment.WeakFrameworks,
					LinkEnvironment.AdditionalFrameworks, 
					LinkEnvironment.AdditionalBundleResources, 
					LinkEnvironment.DelayLoadDLLs, 
					BinaryDependencies, 
					VisitedModules, 
					ExeDir
				);
			}

			// Add all the additional properties
			LinkEnvironment.AdditionalProperties.AddRange(ModuleRule.AdditionalPropertiesForReceipt.Inner);

			// this is a link-time property that needs to be accumulated (if any modules contributing to this module is ignoring, all are ignoring)
			LinkEnvironment.bIgnoreUnresolvedSymbols |= ModuleRule.bIgnoreUnresolvedSymbols;
		}

		// Compiles the module, and returns a list of files output by the compiler.
		public virtual List<FileItem> Compile
		(
			ReadOnlyTargetRules   Target, 
			ToolChain             ToolChain, 
			CppCompileEnvironment CompileEnvironment, 
			FileReference         SingleFileToCompile, 
			ISourceFileWorkingSet WorkingSet,
			IActionGraphBuilder   Graph
		)
		{
			// Generate type libraries for Windows
			foreach (ModuleRules.TypeLibrary TypeLibrary in ModuleRule.TypeLibraries)
			{
				FileReference OutputFile = FileReference.Combine(GeneratedDirectory, TypeLibrary.Header);
				ToolChain.GenerateTypeLibraryHeader(CompileEnvironment, TypeLibrary, OutputFile, Graph);
			}

			return new List<FileItem>();
			// then, Goto UEBuildModule.cs : line 279 (compile)
		}

		// Object interface.
		public override string ToString()
		{
			return ModuleRuleFileName;
		}

		
		// Finds the modules referenced by this module which have not yet been bound to a binary
		
		// <returns>List of unbound modules</returns>
		public List<BuildModule> GetUnboundReferences()
		{
			List<BuildModule> Modules = new List<BuildModule>();
			Modules.AddRange(PrivateDependencyModules.Where(x => x.Binary == null));
			Modules.AddRange(PublicDependencyModules .Where(x => x.Binary == null));
			return Modules;
		}
		
		// Gets all of the modules referenced by this module
		public virtual void RecursivelyGetAllDependencyModules
		(
            List<BuildModule>    ReferencedModules,
            HashSet<BuildModule> IgnoreReferencedModules,
            bool                 bIncludeDynamicallyLoaded,
            bool                 bIgnoreCircularDependencies,
            bool                 OutbThisModuleDirectDependencies
		)
		{
			// Goto CppBuildModule::RecursivelyGetAllDependencyModules
		}

		public delegate BuildModule CreateModuleDelegate(string Name, string ReferenceChain);

		// Creates all the modules required for this target
		public void RecursivelyCreateModules(CreateModuleDelegate DelegateToCreateModule, string ReferenceChainMessage)
		{
			// Get the reference chain for anything referenced by this module
			// NextReferenceChain = "Target -> Launch.Build.cs"
			string NextReferenceChain = String.Format("{0} -> {1}", ReferenceChainMessage, (RulesFile == null)? ModuleRuleFileName : RulesFile.GetFileName());

			// Recursively create all the public include path modules. These modules may not be added to the target (and we don't process their referenced 
			// dependencies), but they need to be created to set up their include paths.
			RecursivelyCreateIncludePathModulesByName(ModuleRule.PublicIncludePathModuleNames, ref PublicIncludePathModules, DelegateToCreateModule, NextReferenceChain);

			// Create all the referenced modules. This path can be recursive, so we check against PrivateIncludePathModules to ensure we don't recurse through the 
			// same module twice (it produces better errors if something fails).
			if(PrivateIncludePathModules == null)
			{
				// Create the private include path modules
				RecursivelyCreateIncludePathModulesByName(ModuleRule.PrivateIncludePathModuleNames, ref PrivateIncludePathModules, DelegateToCreateModule, NextReferenceChain);

				// Create all the dependency modules
				RecursivelyCreateModulesByName(ModuleRule.PublicDependencyModuleNames,  ref PublicDependencyModules,  DelegateToCreateModule, NextReferenceChain);
				RecursivelyCreateModulesByName(ModuleRule.PrivateDependencyModuleNames, ref PrivateDependencyModules, DelegateToCreateModule, NextReferenceChain);
				RecursivelyCreateModulesByName(ModuleRule.DynamicallyLoadedModuleNames, ref DynamicallyLoadedModules, DelegateToCreateModule, NextReferenceChain);
			}
		}

		private static void RecursivelyCreateModulesByName(List<string> ModuleNames, ref List<BuildModule> Modules, CreateModuleDelegate CreateModule, string ReferenceChain)
		{
			// Check whether the module list is already set.
			// We set this immediately (via the ref) to avoid infinite recursion.
			if (Modules == null)
			{
				Modules = new List<BuildModule>();
				foreach (string ModuleName in ModuleNames)
				{
					BuildModule Module = CreateModule(ModuleName, ReferenceChain);
					if (!Modules.Contains(Module))
					{
						Module.RecursivelyCreateModules(CreateModule, ReferenceChain);
						Modules.Add(Module);
					}
				}
			}
		}

		private static void RecursivelyCreateIncludePathModulesByName(List<string> ModuleNames, ref List<BuildModule> Modules, CreateModuleDelegate CreateModule, string ReferenceChain)
		{
			// Check whether the module list is already set. We set this immediately (via the ref) to avoid infinite recursion.
			if (Modules == null)
			{
				Modules = new List<BuildModule>();
				foreach (string ModuleName in ModuleNames)
				{
					BuildModule Module = CreateModule(ModuleName, ReferenceChain);
					RecursivelyCreateIncludePathModulesByName(Module.ModuleRule.PublicIncludePathModuleNames, ref Module.PublicIncludePathModules, CreateModule, ReferenceChain);
					Modules.Add(Module);
				}
			}
		}

		
		// Returns valueless API defines (like MODULE_API)
		
		public IEnumerable<string> GetEmptyApiMacros()
		{
			if (ModuleRule.Type == ModuleRules.ModuleType.CPlusPlus)
			{
				return new[] {ModuleApiDefine + "="};
			}

			return new string[0];
		}

		// Write information about this binary to a JSON file
		public virtual void ExportJson(DirectoryReference BinaryOutputDir, DirectoryReference TargetOutputDir, JsonWriter Writer)
		{
			Writer.WriteValue(nameof(ModuleRuleFileName),   ModuleRuleFileName);
			Writer.WriteValue(nameof(ModuleDirectory),      ModuleDirectory.FullName);
			Writer.WriteValue(nameof(RulesFile),            RulesFile.FullName);
			Writer.WriteValue(nameof(ModuleRules.PCHUsage), ModuleRule.PCHUsage.ToString());

			if (ModuleRule.PrivatePCHHeaderFile != null)
			{
				Writer.WriteValue(nameof(ModuleRules.PrivatePCHHeaderFile), FileReference.Combine(ModuleDirectory, ModuleRule.PrivatePCHHeaderFile).FullName);
			}

			if (ModuleRule.SharedPCHHeaderFile != null)
			{
				Writer.WriteValue(nameof(ModuleRules.SharedPCHHeaderFile), FileReference.Combine(ModuleDirectory, ModuleRule.SharedPCHHeaderFile).FullName);
			}

			ExportJsonModuleArray(Writer, nameof(BuildModule.PublicDependencyModules), PublicDependencyModules);
			ExportJsonModuleArray(Writer, nameof(BuildModule.PublicIncludePathModules), PublicIncludePathModules);
			ExportJsonModuleArray(Writer, nameof(BuildModule.PrivateDependencyModules), PrivateDependencyModules);
			ExportJsonModuleArray(Writer, nameof(BuildModule.PrivateIncludePathModules), PrivateIncludePathModules);
			ExportJsonModuleArray(Writer, nameof(BuildModule.DynamicallyLoadedModules), DynamicallyLoadedModules);

			ExportJsonStringArray(Writer, nameof(BuildModule.PublicSystemIncludePaths), PublicSystemIncludePaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicIncludePaths), PublicIncludePaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, nameof(BuildModule.PrivateIncludePaths), PrivateIncludePaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicSystemLibraryPaths), PublicSystemLibraryPaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicAdditionalLibraries), PublicAdditionalLibraries);
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicSystemLibraries), PublicSystemLibraries);
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicFrameworks), PublicFrameworks);
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicWeakFrameworks), PublicWeakFrameworks);
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicDelayLoadDLLs), PublicDelayLoadDLLs);
			ExportJsonStringArray(Writer, nameof(BuildModule.PublicDefinitions), PublicDefinitions);

			Writer.WriteArrayStart(nameof(ModuleRules.CircularlyReferencedDependentModules));
			foreach(string ModuleName in ModuleRule.CircularlyReferencedDependentModules)
			{
				Writer.WriteValue(ModuleName);
			}
			Writer.WriteArrayEnd();

			// Don't add runtime dependencies for modules that aren't being linked in. They may reference BinaryOutputDir, which is invalid.
			if (Binary != null)
			{
				Writer.WriteArrayStart(nameof(ModuleRules.RuntimeDependencies));
				foreach (ModuleRules.RuntimeDependency RuntimeDependency in ModuleRule.RuntimeDependencies.Inner)
				{
					Writer.WriteObjectStart();
					Writer.WriteValue(nameof(ModuleRules.RuntimeDependency.Path), ExpandPathVariables(RuntimeDependency.Path, BinaryOutputDir, TargetOutputDir));
					if (RuntimeDependency.SourcePath != null)
					{
						Writer.WriteValue(nameof(ModuleRules.RuntimeDependency.SourcePath), ExpandPathVariables(RuntimeDependency.SourcePath, BinaryOutputDir, TargetOutputDir));
					}
					Writer.WriteValue(nameof(ModuleRules.RuntimeDependency.StagedType), RuntimeDependency.StagedType.ToString());
					Writer.WriteObjectEnd();
				}
				Writer.WriteArrayEnd();
			}
		}

		// Write an array of module names to a JSON writer
		void ExportJsonModuleArray(JsonWriter Writer, string ArrayName, IEnumerable<BuildModule> Modules = null)
		{
			Writer.WriteArrayStart(ArrayName);
			if (Modules != null)
			{
				foreach (BuildModule Module in Modules)
				{
					Writer.WriteValue(Module.ModuleRuleFileName);
				}
			}
			Writer.WriteArrayEnd();
		}
		
		
		// Write an array of strings to a JSON writer
		void ExportJsonStringArray(JsonWriter Writer, string ArrayName, IEnumerable<string> Strings = null)
		{
			Writer.WriteArrayStart(ArrayName);
			if (Strings != null)
			{
				foreach(string String in Strings)
				{
					Writer.WriteValue(String);
				}
			}
			Writer.WriteArrayEnd();
		}
	};
}
