using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using BuildToolUtilities;
using System.Reflection;

namespace BuildTool
{
	internal abstract class BuildPlatform
	{
		private static readonly Dictionary<BuildTargetPlatform, BuildPlatform> BuildPlatformDictionary = new Dictionary<BuildTargetPlatform, BuildPlatform>();

		// a mapping of a group to the platforms in the group (ie, Microsoft contains Win32 and Win64)
		private static readonly Dictionary<BuildPlatformGroup, List<BuildTargetPlatform>> PlatformGroupDictionary = new Dictionary<BuildPlatformGroup, List<BuildTargetPlatform>>();

		public readonly BuildTargetPlatform Platform; // The corresponding target platform enum
		private static string[] CachedPlatformFolderNames; // All the platform folder names
		private ReadOnlyHashSet<string> CachedIncludedFolderNames; // Cached copy of the list of folders to include for this platform
		private ReadOnlyHashSet<string> CachedExcludedFolderNames; // Cached copy of the list of folders to exclude for this platform

		public BuildPlatform(BuildTargetPlatform InPlatform) => Platform = InPlatform;

		// Finds all the UEBuildPlatformFactory types in this assembly and uses them to register all the available platforms
		public static void RegisterPlatforms(bool bIncludeNonInstalledPlatforms, bool bHostPlatformOnly)
		{
			// Find and register all tool chains and build platforms that are present
			Type[] AllTypes = Assembly.GetExecutingAssembly().GetTypes();

			// register all build platforms first, since they implement SDK-switching logic that can set environment variables
			foreach (Type CheckType in AllTypes)
			{
				if (CheckType.IsClass && 
				   !CheckType.IsAbstract)
				{
					if (CheckType.IsSubclassOf(typeof(BuildPlatformFactory)))
					{
						Log.TraceVerbose("    Registering build platform: {0}", CheckType.ToString());
						BuildPlatformFactory TempInst = (BuildPlatformFactory)Activator.CreateInstance(CheckType);

						if (bHostPlatformOnly && TempInst.TargetPlatform != BuildHostPlatform.Current.Platform)
						{
							continue;
						}

						// We need all platforms to be registered when we run -validateplatform command to check SDK status of each
						if (bIncludeNonInstalledPlatforms || InstalledPlatformInfo.IsValidPlatform(TempInst.TargetPlatform))
						{
							TempInst.RegisterBuildPlatforms();
						}
					}
				}
			}
		}

		public static string[] GetAllPlatformFolderNames()
		{
			if (CachedPlatformFolderNames == null)
			{
				List<string> PlatformFolderNames = new List<string>();

				PlatformFolderNames.AddRange(BuildTargetPlatform.GetValidPlatformNames()); // Find all the platform folders to exclude from the list of precompiled modules
				PlatformFolderNames.AddRange(BuildPlatformGroup.GetValidGroupNames()); // Also exclude all the platform groups that this platform is not a part of

				CachedPlatformFolderNames = PlatformFolderNames.ToArray();
			}
			return CachedPlatformFolderNames;
		}

		// Finds a list of folder names to include when building for this platform
		public ReadOnlyHashSet<string> GetIncludedFolderNames()
		{
			if (CachedIncludedFolderNames == null)
			{
				HashSet<string> CachedFolderNames = new HashSet<string>(DirectoryReference.Comparer)
				{
					Platform.ToString()
				};

				foreach (BuildPlatformGroup Group in BuildPlatform.GetPlatformGroups(Platform))
				{
					CachedFolderNames.Add(Group.ToString());
				}

				CachedIncludedFolderNames = new ReadOnlyHashSet<string>(CachedFolderNames, DirectoryReference.Comparer);
			}

			return CachedIncludedFolderNames;
		}

		// Finds a list of folder names to exclude when building for this platform
		public ReadOnlyHashSet<string> GetExcludedFolderNames()
		{
			if (CachedExcludedFolderNames == null)
			{
				CachedExcludedFolderNames = new ReadOnlyHashSet<string>(GetAllPlatformFolderNames().Except(GetIncludedFolderNames()), DirectoryReference.Comparer);
			}

			return CachedExcludedFolderNames;
		}

		// Gets all the registered platforms
		public static IEnumerable<BuildTargetPlatform> GetRegisteredPlatforms()
		{
			return BuildPlatformDictionary.Keys;
		}

		// Whether the required external SDKs are installed for this platform. Could be either a manual install or an AutoSDK.
		public abstract SDKStatus HasRequiredSDKsInstalled();

		// Determines if the given name is a build product for a target.
		// <param name="FileName">The name to check</param>
		// <param name="NamePrefixes">Target or application names that may appear at the start of the build product name (eg. "Editor", "ShooterGameEditor")</param>
		// <param name="NameSuffixes">Suffixes which may appear at the end of the build product name</param>
		// <returns>True if the string matches the name of a build product, false otherwise</returns>
		public abstract bool IsBuildProduct(string FileNameToCheck, string[] NamePrefixes, string[] NameSuffixes);

		// Modify the rules for a newly created module, where the target is a different host platform.
		// This is not required - but allows for hiding details of a particular platform.
		public virtual void ModifyModuleRulesForOtherPlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules TargetBeginBuild)
        {

        }

		// Set all the platform-specific defaults for a new target
		public virtual void ResetTarget(TargetRules Target)
		{
		}

		// Validate a target's settings
		public virtual void ValidateTarget(TargetRules Target)
		{
		}

		// Enumerates any additional directories needed to clean this target
		public virtual void FindAdditionalBuildProductsToClean(ReadOnlyTargetRules TargetToClean, List<FileReference> ReceivingFilesToDelete, List<DirectoryReference> ReceivingDirectoriesToDelete)
		{
		}
		public virtual void PostBuildSync(BuildTarget Target)
        {
        }

		// Get a list of extra modules the platform requires.
		// This is to allow undisclosed platforms to add modules they need without exposing information about the platform.
		public virtual void AddExtraModules(ReadOnlyTargetRules TargetBeingBuild, List<string> ExtraModuleNames)
        {
        }

		// Modify the rules for a newly created module, in a target that's being built for this platform.
		// This is not required - but allows for hiding details of a particular platform.
		public virtual void ModifyModuleRulesForActivePlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules TargetBeingBuild)
        {
        }

		// Setup the target environment for building
		public virtual void SetUpEnvironment(ReadOnlyTargetRules TargetBeingCompiled, CppCompileEnvironment CompileEnvironment, LinkEnvironment LinkEnvironment)
        {
        }

		// Deploys the given target
		public virtual void Deploy(TargetReceipt Receipt)
		{
		}

		// Whether this platform should create debug information or not
		public abstract bool ShouldCreateDebugInfo(ReadOnlyTargetRules Target);

		// Creates a toolchain instance for this platform.
		// There should be a single toolchain instance per-target, as their may be state data and configuration cached between calls.
		public abstract ToolChain CreateToolChain(ReadOnlyTargetRules Target);


		// Whether this platform requires specific Visual Studio version.
		public virtual VCProjectFileFormat GetRequiredVisualStudioVersion()
		{
			return VCProjectFileFormat.Default;
		}

		// Get the default architecture for a project. This may be overriden on the command line to UBT.
		public virtual string GetDefaultArchitecture(FileReference ProjectFile) => "";

		// Get name for architecture-specific directories (can be shorter than architecture name itself)
		public virtual string GetFolderNameForArchitecture(string Architecture) => Architecture;

		// Searches a directory tree for build products to be cleaned.
		// <param name="NamePrefixes">Target or application names that may appear at the start of the build product name (eg. "Editor", "ShooterGameEditor")</param>
		// <param name="NameSuffixes">Suffixes which may appear at the end of the build product name</param>
		public void FindBuildProductsToClean(DirectoryReference DirToSearch, string[] NamePrefixes, string[] NameSuffixes, List<FileReference> FilesToClean, List<DirectoryReference> DirectoriesToClean)
		{
			foreach (FileReference File in DirectoryReference.EnumerateFiles(DirToSearch))
			{
				string FileName = File.GetFileName();
				if (IsDefaultBuildProduct(FileName, NamePrefixes, NameSuffixes) || IsBuildProduct(FileName, NamePrefixes, NameSuffixes))
				{
					FilesToClean.Add(File);
				}
			}
			foreach (DirectoryReference SubDir in DirectoryReference.EnumerateDirectories(DirToSearch))
			{
				string SubDirName = SubDir.GetDirectoryName();
				if (IsBuildProduct(SubDirName, NamePrefixes, NameSuffixes))
				{
					DirectoriesToClean.Add(SubDir);
				}
				else
				{
					FindBuildProductsToClean(SubDir, NamePrefixes, NameSuffixes, FilesToClean, DirectoriesToClean);
				}
			}
		}

		// Determines if a filename is a default UBT build product
		// <param name="NamePrefixes">Target or application names that may appear at the start of the build product name (eg. "Editor", "ShooterGameEditor")</param>
		public static bool IsDefaultBuildProduct(string FileNameToCheck, string[] NamePrefixes, string[] NameSuffixes)
		{
			return BuildPlatform.IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, Tag.Ext.Target)  ||
				   BuildPlatform.IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, Tag.Ext.Modules) ||
				   BuildPlatform.IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, Tag.Ext.Version);
		}

		public static bool IsBuildProductName(string FileName, string[] NamePrefixes, string[] NameSuffixes, string Extension)
		{
			return IsBuildProductName(FileName, 0, FileName.Length, NamePrefixes, NameSuffixes, Extension);
		}
		public static bool IsBuildProductName(string FileName, int Index, int Count, string[] NamePrefixes, string[] NameSuffixes, string Extension)
		{
			// Check if the extension matches, and forward on to the next IsBuildProductName() overload without it if it does.
			if (Extension.Length < Count && 
				String.Compare(FileName, Index + Count - Extension.Length, Extension, 0, Extension.Length, StringComparison.InvariantCultureIgnoreCase) == 0)
			{
				return IsBuildProductName(FileName, Index, Count - Extension.Length, NamePrefixes, NameSuffixes);
			}

			return false;
		}

		public static bool IsBuildProductName(string FileName, int Index, int Count, string[] NamePrefixes, string[] NameSuffixes)
		{
			foreach (string NamePrefix in NamePrefixes)
			{
				if (NamePrefix.Length <= Count && 
					String.Compare(FileName, Index, NamePrefix, 0, NamePrefix.Length, StringComparison.InvariantCultureIgnoreCase) == 0)
				{
					int MinIdx = Index + NamePrefix.Length;
					foreach (string NameSuffix in NameSuffixes)
					{
						int MaxIdx = Index + Count - NameSuffix.Length;
						if (MinIdx <= MaxIdx && 
							String.Compare(FileName, MaxIdx, NameSuffix, 0, NameSuffix.Length, StringComparison.InvariantCultureIgnoreCase) == 0)
						{
							if (MinIdx < MaxIdx && FileName[MinIdx] == '-')
							{
								MinIdx++;
								while (MinIdx < MaxIdx && 
									FileName[MinIdx] != '-' && 
									FileName[MinIdx] != '.')
								{
									MinIdx++;
								}
							}
							if (MinIdx == MaxIdx)
							{
								return true;
							}
						}
					}
				}
			}

			return false;
		}

		// Get the bundle directory for the shared link environment
		public virtual DirectoryReference GetBundleDirectory(ReadOnlyTargetRules Rules, List<FileReference> ExecutableOutputFiles)
		{
			return null;
		}

		// Determines whether a given platform is available
		public static bool IsPlatformAvailable(BuildTargetPlatform Platform)
		{
			return BuildPlatformDictionary.ContainsKey(Platform) && BuildPlatformDictionary[Platform].HasRequiredSDKsInstalled() == SDKStatus.Valid;
		}

		// Register the given platforms UEBuildPlatform instance
		public static void RegisterBuildPlatform(BuildPlatform InBuildPlatform)
		{
			Log.TraceVerbose("        Registering build platform: {0} - buildable: {1}", InBuildPlatform.Platform, InBuildPlatform.HasRequiredSDKsInstalled() == SDKStatus.Valid);

			if (BuildPlatformDictionary.ContainsKey(InBuildPlatform.Platform) == true)
			{
				Log.TraceWarning("RegisterBuildPlatform Warning: Registering build platform {0} for {1} when it is already set to {2}",
					InBuildPlatform.ToString(), InBuildPlatform.Platform.ToString(), BuildPlatformDictionary[InBuildPlatform.Platform].ToString());
				BuildPlatformDictionary[InBuildPlatform.Platform] = InBuildPlatform;
			}
			else
			{
				BuildPlatformDictionary.Add(InBuildPlatform.Platform, InBuildPlatform);
			}
		}

		// Assign a platform as a member of the given group
		public static void RegisterPlatformWithGroup(BuildTargetPlatform InPlatform, BuildPlatformGroup InGroup)
		{
			// find or add the list of groups for this platform
			if (!PlatformGroupDictionary.TryGetValue(InGroup, out List<BuildTargetPlatform> Platforms))
			{
				Platforms = new List<BuildTargetPlatform>();
				PlatformGroupDictionary.Add(InGroup, Platforms);
			}
			Platforms.Add(InPlatform);
		}
		
		// Retrieve the list of platforms in this group (if any)
		public static List<BuildTargetPlatform> GetPlatformsInGroup(BuildPlatformGroup InGroup)
		{
			if (PlatformGroupDictionary.TryGetValue(InGroup, out List<BuildTargetPlatform> OutPlatformList))
			{
				return OutPlatformList;
			}
			else
			{
				return null;
			}
		}

		// Enumerates all the platform groups for a given platform
		public static IEnumerable<BuildPlatformGroup> GetPlatformGroups(BuildTargetPlatform Platform)
		{
			return PlatformGroupDictionary.Where(x => x.Value.Contains(Platform)).Select(x => x.Key);
		}

		// Retrieve the IUEBuildPlatform instance for the given TargetPlatform
		public static BuildPlatform GetBuildPlatform(BuildTargetPlatform InPlatformBeingBuild, bool bDontThrowEx = false)
		{
			if (BuildPlatformDictionary.ContainsKey(InPlatformBeingBuild) == true)
			{
				return BuildPlatformDictionary[InPlatformBeingBuild];
			}
			if (bDontThrowEx == true)
			{
				return null;
			}
			throw new BuildException("GetBuildPlatform: No BuildPlatform found for {0}", InPlatformBeingBuild.ToString());
		}

		// Allow all registered build platforms to modify the newly created module passed in for the given platform.
		// This is not required - but allows for hiding details of a particular platform.
		public static void PlatformModifyHostModuleRules(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules TargetBeingBuild)
		{
			// PlatformEntry => [9] = {[{Win64}, {BuildTool.WindowsPlatform}]}
			// PlatformEntry => [10] = {[{Win32}, {BuildTool.WindowsPlatform}]}
			foreach (KeyValuePair<BuildTargetPlatform, BuildPlatform> PlatformEntry in BuildPlatformDictionary)
			{
				PlatformEntry.Value.ModifyModuleRulesForOtherPlatform(ModuleName, InModuleRules, TargetBeingBuild);
			}
		}

		// Returns the delimiter used to separate paths in the PATH environment variable for the platform we are executing on.
		public static String GetPathVarDelimiter()
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux        || 
				BuildHostPlatform.Current.Platform == BuildTargetPlatform.LinuxAArch64 ||
				BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				return ":";
			}
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32 || 
				BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || 
				BuildHostPlatform.Current.Platform == BuildTargetPlatform.HoloLens)
			{
				return ";";
			}

			Log.TraceWarning("PATH variable delimiter unknown for platform " + BuildHostPlatform.Current.Platform.ToString() + " using ';'");
			return ";";
		}

		// Define OverridePlatformHeaderName("OVERRIDE_PLATFORM_HEADER_NAME")
		public virtual string GetPlatformName() => Platform.ToString();
		public virtual bool CanUseXGE() => true;
		public virtual bool CanUseParallelExecutor() => CanUseXGE();
		public virtual bool CanUseDistcc() => false;
		public virtual bool CanUseSNDBS() => false;

		// Return whether the given platform requires a monolithic build
		public static bool PlatformRequiresMonolithicBuilds(BuildTargetPlatform InPlatform)
		{
			// Some platforms require monolithic builds...
			BuildPlatform BuildPlatform = GetBuildPlatform(InPlatform, true);
			if (BuildPlatform != null)
			{
				return BuildPlatform.ShouldCompileMonolithicBinary(InPlatform);
			}

			// We assume it does not
			return false;
		}

		// Get the extension to use for the given binary type(.dll, .exe, ...)
		public virtual string GetBinaryExtension(BuildBinaryType InBinaryType)
		{
			throw new BuildException("GetBinaryExtensiton for {0} not handled in {1}", InBinaryType.ToString(), this.ToString());
		}

		// Get the extensions to use for debug info for the given binary type(.pdb, ...)
		public virtual string[] GetDebugInfoExtensions(ReadOnlyTargetRules InTarget, BuildBinaryType InBinaryType)
		{
			throw new BuildException("GetDebugInfoExtensions for {0} not handled in {1}", InBinaryType.ToString(), this.ToString());
		}

		// Whether this platform should build a monolithic binary
		public virtual bool ShouldCompileMonolithicBinary(BuildTargetPlatform InPlatform) => false;

		// Allows the platform to override whether the architecture name should be appended to the name of binaries.
		public virtual bool RequiresArchitectureSuffix() => true;

		// For platforms that need to output multiple files per binary (ie Android "fat" binaries)
		// this will emit multiple paths. By default, it simply makes an array from the input
		public virtual List<FileReference> FinalizeBinaryPaths(FileReference BinaryName, FileReference ProjectFile, ReadOnlyTargetRules Target)
		{
			List<FileReference> TempList = new List<FileReference>() { BinaryName };
			return TempList;
		}

		// Return all valid configurations for this platform
		// Typically, this is always Debug, Development, and Shipping - but Test is a likely future addition for some platforms
		public virtual List<TargetConfiguration> GetConfigurations(BuildTargetPlatform InBuildTargetPlatform, bool bIncludeDebug)
		{
			List<TargetConfiguration> Configurations = new List<TargetConfiguration>()
			{
				TargetConfiguration.Development,
			};

			if (bIncludeDebug)
			{
				Configurations.Insert(0, TargetConfiguration.Debug);
			}

			return Configurations;
		}

		protected static bool DoProjectSettingsMatchDefault
		(
            BuildTargetPlatform Platform,
            DirectoryReference ProjectDirectoryName,
            string Section,
            string[] BoolKeys,
            string[] IntKeys,
            string[] StringKeys
		)
		{
			ConfigHierarchy ProjectIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, ProjectDirectoryName, Platform);
			ConfigHierarchy DefaultIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, (DirectoryReference)null, Platform);

            // look at all bool values
            if (BoolKeys != null)
            {
                foreach (string Key in BoolKeys)
                {
                    DefaultIni.GetBool(Section, Key, out bool Default);
                    ProjectIni.GetBool(Section, Key, out bool Project);

                    if (Default != Project)
                    {
                        Log.TraceInformationOnce("{0} is not set to default. (Base: {1} vs. {2}: {3})", 
							Key, 
							Default, Path.GetFileName(ProjectDirectoryName.FullName), 
							Project);
                        return false;
                    }
                }
            }

            // look at all int values
            if (IntKeys != null)
			{
				foreach (string Key in IntKeys)
				{
					DefaultIni.GetInt32(Section, Key, out int Default);
					ProjectIni.GetInt32(Section, Key, out int Project);

					if (Default != Project)
					{
						Log.TraceInformationOnce("{0} is not set to default. (Base: {1} vs. {2}: {3})", 
							Key, 
							Default, Path.GetFileName(ProjectDirectoryName.FullName), 
							Project);
						return false;
					}
				}
			}

			// look for all string values
			if (StringKeys != null)
			{
				foreach (string Key in StringKeys)
				{
					DefaultIni.GetString(Section, Key, out string Default);
                    ProjectIni.GetString(Section, Key, out string Project);

					if (Default != Project)
					{
						Log.TraceInformationOnce("{0} is not set to default. (Base: {1} vs. {2}: {3})", 
							Key, 
							Default, Path.GetFileName(ProjectDirectoryName.FullName), 
							Project);
						return false;
					}
				}
			}

			// We match all important settings
			return true;
		}

		// Check for the default configuration
		// return true if the project uses the default build config
		public virtual bool HasDefaultBuildConfig(BuildTargetPlatform Platform, DirectoryReference ProjectDirectoryName)
		{
			string[] BoolKeys = new string[] 
			{
				nameof(ReadOnlyTargetRules.bCompileAPEX),              nameof(ReadOnlyTargetRules.bCompileICU),
				nameof(ReadOnlyTargetRules.bCompileRecast),            nameof(ReadOnlyTargetRules.bCompileSpeedTree),
				nameof(ReadOnlyTargetRules.bCompileWithPluginSupport), nameof(ReadOnlyTargetRules.bCompilePhysX),
				nameof(ReadOnlyTargetRules.bCompileFreeType),          nameof(ReadOnlyTargetRules.bCompileForSize),
				nameof(ReadOnlyTargetRules.bCompileCEF3),              nameof(ReadOnlyTargetRules.bCompileCustomSQLitePlatform)
			};

			return DoProjectSettingsMatchDefault(Platform, ProjectDirectoryName, Tag.Directory.Script + Tag.Module.Engine.BuildSettings + Tag.Ext.BuildSettings, BoolKeys, null, null);
		}

		public virtual bool RequiresBuild(BuildTargetPlatform Platform, DirectoryReference ProjectDirectoryName) => false;

		// Setup the configuration environment for building
		public virtual void SetUpConfigurationEnvironment(ReadOnlyTargetRules TargetBeingBuilt, CppCompileEnvironment GlobalCompileEnvironment, LinkEnvironment GlobalLinkEnvironment)
		{
			if (GlobalCompileEnvironment.bUseDebugCRT) // Conventional Definitions
			{
				GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.Debug + Tag.Boolean.One);
			}
			else
			{
				GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.NDebug + Tag.Boolean.One);
			}

			switch (TargetBeingBuilt.Configuration)
			{
				default:
				case TargetConfiguration.Debug:
					GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.BuildDebug + Tag.Boolean.One);
					break;
				case TargetConfiguration.DebugGame:
				// Individual game modules can be switched to be compiled in debug as necessary. By default, everything is compiled in development.
				case TargetConfiguration.Development:
					GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.BuildDevelopment + Tag.Boolean.One);
					break;
				case TargetConfiguration.Shipping:
					GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.BuildShipping + Tag.Boolean.One);
					break;
				case TargetConfiguration.Test:
					GlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.BuildTest + Tag.Boolean.One);
					break;
			}

			// Create debug info based on the heuristics specified by the user.
			GlobalCompileEnvironment.bCreateDebugInfo = 
				!TargetBeingBuilt.bDisableDebugInfo && 
				ShouldCreateDebugInfo(TargetBeingBuilt);

			GlobalLinkEnvironment.bCreateDebugInfo = GlobalCompileEnvironment.bCreateDebugInfo;
		}

		// Allows the platform to return various build metadata that is not tracked by other means.
		// If the returned string changes, the makefile will be invalidated.
		public string GetExternalBuildMetadata(FileReference ProjectFile)
		{
			StringBuilder Result = new StringBuilder();
			GetExternalBuildMetadata(ProjectFile, Result);
			return Result.ToString();
		}

		// Allows the platform to return various build metadata that is not tracked by other means.
		// If the returned string changes, the makefile will be invalidated.
		public virtual void GetExternalBuildMetadata(FileReference ProjectFile, StringBuilder Metadata)
		{
		}

		// Checks if platform is part of a given platform group
		internal static bool IsPlatformInGroup(BuildTargetPlatform PlatformToCheck, BuildPlatformGroup PlatformGroupCheck)
		{
			List<BuildTargetPlatform> Platforms = BuildPlatform.GetPlatformsInGroup(PlatformGroupCheck);

            return Platforms != null && Platforms.Contains(PlatformToCheck);
        }
	}
}
