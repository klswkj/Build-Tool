using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
#pragma warning disable IDE1006 // Naming Styles
	// The type of target
	[Serializable]
	public enum TargetType
	{
		         // Game = Default
		Game,    // Cooked monolithic game executable (GameName.exe).  Also used for a game-agnostic engine executable (DefaultGame.exe or RocketGame.exe)
		Editor,  // Uncooked modular editor executable and DLLs (Editor.exe, Editor*.dll, GameName*.dll)
		Client,  // Cooked monolithic game client executable (GameNameClient.exe, but no server code)
		Server,  // Cooked monolithic game server executable (GameNameServer.exe, but no client code)
		Program, // standalone program, e.g. ShaderCompileWorker.exe. can be modular or monolithic depending on the program)
	}

	// Specifies how to link all the modules in this target
	[Serializable]
	public enum TargetLinkType
	{
		Default,    // Use the default link type based on the current target type
		Monolithic, // Link all modules into a single binary
		Modular,    // Link modules into individual dynamic libraries
	}

	// Specifies whether to share engine binaries and intermediates with other projects, or to create project-specific versions. 
	// By default, editor builds always use the shared build environment (and engine binaries are written to Engine/Binaries/Platform), 
	// but monolithic builds and programs do not (except in installed builds). 
	// Using the shared build environment prevents target-specific modifications to the build environment.
	[Serializable]
	public enum TargetBuildEnvironment
	{
		// Engine binaries and intermediates are output to the engine folder. 
		// Target-specific modifications to the engine build environment will be ignored.
		Shared,

		// Engine binaries and intermediates are specific to this target
		Unique,
	}

	// Determines which version of the engine to take default build settings from. 
	// This allows for backwards compatibility as new options are enabled by default.
	public enum BuildSettingsVersion
	{
		V1, // Legacy default build settings for 4.23 and earlier.
		V2, // New defaults for 4.24: ModuleRules.PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs, ModuleRules.bLegacyPublicIncludePaths = false.

		// When adding new entries here,
		// be sure to update GameProjectUtils::GetDefaultBuildSettingsVersion() to ensure that new projects are created correctly.
		// Always use the defaults for the current engine version.
		// Note that this may cause compatibility issues when upgrading.
		Latest
	}

	// Attribute used to mark fields which much match between targets in the shared build environment
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
	class RequiresUniqueBuildEnvironmentAttribute : Attribute
	{
	}

	// Attribute used to mark configurable sub-objects
	[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
	class ConfigSubObjectAttribute : Attribute
	{
	}

	// TargetRules is a data structure that contains the rules for defining a target 
	// (application/executable)
	public abstract class TargetRules
	{
		// Static class wrapping constants aliasing the global TargetType enum.
		// Alias for TargetType :: Game, Editor, Client, Server, Program
		public static class TargetType
		{
			public const global::BuildTool.TargetType Game    = global::BuildTool.TargetType.Game;
			public const global::BuildTool.TargetType Editor  = global::BuildTool.TargetType.Editor;
			public const global::BuildTool.TargetType Client  = global::BuildTool.TargetType.Client;
			public const global::BuildTool.TargetType Server  = global::BuildTool.TargetType.Server;
			public const global::BuildTool.TargetType Program = global::BuildTool.TargetType.Program;
		}

		// Constructor.
		// For *.Target.cs, RulesAssembly::CreateTargetRulesInstance
		public TargetRules(TargetInfo Target)
		{
			this.Name             = Target.Name;
			this.Platform         = Target.Platform;
			this.Configuration    = Target.Configuration;
			this.Architecture     = Target.Architecture;
			this.ProjectFile      = Target.ProjectFile;
			this.Version          = Target.Version;
			this.WindowsPlatform  = new WindowsTargetRules(this);
			this.HoloLensPlatform = new HoloLensTargetRules(Target);

			// Read settings from config files
			foreach (object ConfigurableObject in GetConfigurableObjects())
			{
				ConfigCache.ReadSettings(DirectoryReference.FromFile(ProjectFile), Platform, ConfigurableObject, ConfigValueTracker);
				XMLConfig.ApplyTo(ConfigurableObject);
				if (Target.ExtraArguments != null)
				{
					Target.ExtraArguments.ApplyTo(ConfigurableObject);
				}
			}

			// If we've got a changelist set, set that we're making a formal build
			bFormalBuild = (Version.Changelist != 0 && Version.IsPromotedBuild);

			// Allow the build platform to set defaults for this target
			BuildPlatform.GetBuildPlatform(Platform).ResetTarget(this);

			// Set the default build version
			if (String.IsNullOrEmpty(BuildVersion))
			{
				if (String.IsNullOrEmpty(Target.Version.BuildVersionString))
				{
					BuildVersion = String.Format("{0}-CL-{1}", Target.Version.BranchName, Target.Version.Changelist);
				}
				else
				{
					BuildVersion = Target.Version.BuildVersionString;
				}
			}

			// Setup macros for signing and encryption keys
			EncryptionAndSigning.CryptoSettings CryptoSettings = EncryptionAndSigning.ParseCryptoSettings(DirectoryReference.FromFile(ProjectFile), Platform);
			if (CryptoSettings.IsAnyEncryptionEnabled())
			{
				ProjectDefinitions.Add(String.Format(Tag.CppContents.Def.ImplementEncryptionKey + "=" + Tag.CppContents.Def.RegisterEncryptionKey + "=({0})", FormatHexBytes(CryptoSettings.EncryptionKey.Key)));
			}
			else
			{
				ProjectDefinitions.Add(Tag.CppContents.Def.ImplementEncryptionKey + "=");
			}

			if (CryptoSettings.IsPakSigningEnabled())
			{
				// ProjectDefinitions.Add(String.Format("IMPLEMENT_SIGNING_KEY_REGISTRATION()=UE_REGISTER_SIGNING_KEY(UE_LIST_ARGUMENT({0}), UE_LIST_ARGUMENT({1}))", FormatHexBytes(CryptoSettings.SigningKey.PublicKey.Exponent), FormatHexBytes(CryptoSettings.SigningKey.PublicKey.Modulus)));
				ProjectDefinitions.Add
				(
					String.Format
					(
						Tag.CppContents.Def.ImplementSigningKey + "=" +
						Tag.CppContents.Def.RegisterSigningKey +
						"(" + Tag.CppContents.Def.ListArgument + "({0}), " + Tag.CppContents.Def.ListArgument + "({1}))",
						FormatHexBytes(CryptoSettings.SigningKey.PublicKey.Exponent),
						FormatHexBytes(CryptoSettings.SigningKey.PublicKey.Modulus)
					)
				);
			}
			else
			{
				ProjectDefinitions.Add(Tag.CppContents.Def.ImplementSigningKey + "=");
			}
		}

		public readonly string Name;
		internal FileReference File;
		public readonly BuildTargetPlatform Platform;
		public readonly TargetConfiguration Configuration;
		public readonly string Architecture;
		public readonly FileReference ProjectFile;
		public readonly ReadOnlyBuildVersion Version;

		// The type of target.
		public global::BuildTool.TargetType Type = global::BuildTool.TargetType.Game;

		// Specifies the engine version to maintain backwards-compatible default build settings with (eg. DefaultSettingsVersion.Release_4_23, DefaultSettingsVersion.Release_4_24). Specify DefaultSettingsVersion.Latest to always
		// use defaults for the current engine version, at the risk of introducing build errors while upgrading.
		public BuildSettingsVersion DefaultBuildSettings
		{
			get { return DefaultBuildSettingsPrivate ?? BuildSettingsVersion.V1; }
			set { DefaultBuildSettingsPrivate = value; }
		}
		private BuildSettingsVersion? DefaultBuildSettingsPrivate; // Cannot be initialized inline; potentially overridden before the constructor is called.

		// Tracks a list of config values read while constructing this target
		internal ConfigValueTracker ConfigValueTracker = new ConfigValueTracker();

		public bool bUsesSteam;
		public bool bUsesCEF3;
		public bool bUsesSlate = true; // (as opposed to the low level windowing/messaging, which is always available).

		// Forces linking against the static CRT. 
		// This is not fully supported across the engine due to the need for allocator implementations to be shared (for example), 
		// and TPS libraries to be consistent with each other, but can be used for utility programs.
		[RequiresUniqueBuildEnvironment]
		public bool bUseStaticCRT = false;

		// Enables the debug C++ runtime (CRT) for debug builds. 
		// By default we always use the release runtime, 
		// since the debug version isn't particularly useful when debugging C++ Engine projects, 
		// and linking against the debug CRT libraries forces
		// our third party library dependencies to also be compiled using the debug CRT (and often perform more slowly). 
		// Often it can be inconvenient to require a separate copy of the debug versions of third party static libraries simply
		// so that you can debug your program's code.
		[RequiresUniqueBuildEnvironment]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bDebugBuildsActuallyUseDebugCRT = false;

		// Whether the output from this target can be publicly distributed, even if it has dependencies on modules that are in folders
		// with special restrictions (eg. CarefullyRedist, NotForLicensees, NoRedist).
		public bool bLegalToDistributeBinary = false;

		// Specifies the configuration whose binaries do not require a "-Platform-Configuration" suffix.
		[RequiresUniqueBuildEnvironment]
		public TargetConfiguration UndecoratedConfiguration = TargetConfiguration.Development;

		// Build all the modules that are valid for this target type. 
		// Used for CIS and making installed engine builds.
		[CommandLine("-AllModules")]
		public bool bBuildAllModules = false;

		// Additional plugins that are built for this target type but not enabled.
		[CommandLine("-BuildPlugin=", ListSeparator = '+')]
		public List<string> BuildPlugins = new List<string>();

		// A list of additional plugins which need to be included in this target. 
		// This allows referencing non-optional plugin modules which cannot be disabled, and 
		// allows building against specific modules in program targets which do not fit the categories in ModuleHostType.
		public List<string> AdditionalPlugins = new List<string>();

		// Additional plugins that should be included for this target.
		[CommandLine("-EnablePlugin=", ListSeparator = '+')]
		public List<string> EnablePlugins = new List<string>();

		// List of plugins to be disabled for this target.
		// Note that the project file may still reference them,
		// so they should be marked as optional to avoid failing to find them at runtime.
		[CommandLine("-DisablePlugin=", ListSeparator = '+')]
		public List<string> DisablePlugins = new List<string>();

		// Path to the set of pak signing keys to embed in the executable.
		public string PakSigningKeysFile = "";

		// Allows a Program Target to specify it's own solution folder path.
		public string SolutionDirectory = String.Empty;

		// Whether the target should be included in the default solution build configuration
		public bool? bBuildInSolutionByDefault = null;

		// Whether this target should be compiled as a DLL.  
		// Requires LinkType to be set to TargetLinkType.Monolithic.
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-CompileAsDll")]
		public bool bShouldCompileAsDLL = false;

		// Subfolder to place executables in, relative to the default location.
		[RequiresUniqueBuildEnvironment]
		public string ExeBinariesSubFolder = String.Empty;

		// Allow target module to override UHT code generation version.
		public EGeneratedCodeVersion GeneratedCodeVersion = EGeneratedCodeVersion.None;

		// Whether to enable the mesh editor.
		[RequiresUniqueBuildEnvironment]
		public bool bEnableMeshEditor = false;

		// Whether to compile the Chaos physics plugin.
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-NoCompileChaos", Value = "false")]
		[CommandLine("-CompileChaos", Value = "true")]
		public bool bCompileChaos = false;

		// Whether to use the Chaos physics interface. 
		// This overrides the physx flags to disable APEX and NvCloth
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-NoUseChaos", Value = "false")]
		[CommandLine("-UseChaos", Value = "true")]
		public bool bUseChaos = false;

		// Whether to compile in checked chaos features for debugging
		[RequiresUniqueBuildEnvironment]
		public bool bUseChaosChecked = false;

		// Whether to compile in chaos memory tracking features
		[RequiresUniqueBuildEnvironment]
		public bool bUseChaosMemoryTracking = false;

		// The physx scene query structure is still created, but we do not use it.
		[RequiresUniqueBuildEnvironment]
		public bool bCustomSceneQueryStructure = false;

		// Whether to include PhysX support.
		[RequiresUniqueBuildEnvironment]
		public bool bCompilePhysX = true;

		// Whether to include PhysX APEX support.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileApex")]
		public bool bCompileAPEX = true;

		// Whether to include NvCloth.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileNvCloth = true;

		// Whether to include ICU unicode/i18n support in Core.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileICU")]
		public bool bCompileICU = true;

		// Whether to compile CEF3 support.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileCEF3")]
		public bool bCompileCEF3 = true;

		// Whether to compile using ISPC.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileISPC = false;

		// Whether to compile the editor or not. 
		// Only desktop platforms (Windows or Mac) will use this, other platforms force this to false.
		public bool bBuildEditor
		{
			get { return (Type == TargetType.Editor); }
			set { Log.TraceWarning("Setting {0}.bBuildEditor is deprecated. Set {0}.Type instead.", GetType().Name); }
		}

		// Whether to compile code related to building assets. 
		// Consoles generally cannot build assets. Desktop platforms generally can.
		[RequiresUniqueBuildEnvironment]
		public bool bBuildRequiresCookedData
		{
			get { return bBuildRequiresCookedDataOverride ?? (Type == TargetType.Game || Type == TargetType.Client || Type == TargetType.Server); }
			set { bBuildRequiresCookedDataOverride = value; }
		}
		bool? bBuildRequiresCookedDataOverride;

		// Whether to compile WITH_EDITORONLY_DATA disabled. 
		// Only Windows will use this, other platforms force this to false.
		[RequiresUniqueBuildEnvironment]
		public bool bBuildWithEditorOnlyData
		{
			get { return bBuildWithEditorOnlyDataOverride ?? (Type == TargetType.Editor || Type == TargetType.Program); }
			set { bBuildWithEditorOnlyDataOverride = value; }
		}
		private bool? bBuildWithEditorOnlyDataOverride;

		// Manually specified value for bBuildDeveloperTools.
		bool? bBuildDeveloperToolsOverride;

		// Whether to compile the developer tools.
		// Programs폴더에 있는 *.Target.cs아닌 이상 모두 여기서 결정남.
		[RequiresUniqueBuildEnvironment]
		public bool bBuildDeveloperTools
		{
			set { bBuildDeveloperToolsOverride = value; }
			get 
			{ 
				return bBuildDeveloperToolsOverride ?? 
					(
					    bCompileAgainstEngine && 
					    (
						    Type == TargetType.Editor  || 
							Type == TargetType.Program || 
							(
							    Configuration != TargetConfiguration.Test &&  
								Configuration != TargetConfiguration.Shipping
							)
						)
					); 
			}
		}

		// Whether to force compiling the target platform modules, 
		// even if they wouldn't normally be built.
		public bool bForceBuildTargetPlatforms = false;

		// Whether to force compiling shader format modules, 
		// even if they wouldn't normally be built.
		public bool bForceBuildShaderFormats = false;

		// Whether we should compile SQLite using the custom Host platform (true),
		// or using the native platform (false).
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileCustomSQLitePlatform")]
		public bool bCompileCustomSQLitePlatform = true;

		// Whether to utilize cache freed OS allocs with MallocBinned
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bUseCacheFreedOSAllocs")]
		public bool bUseCacheFreedOSAllocs = true;

		// Enabled for all builds that include the engine project. 
		// Disabled only when building standalone apps that only link with Core. // Determine bCompileAgainstCoreUObject
		[RequiresUniqueBuildEnvironment]
		public bool bCompileAgainstEngine = true;

		// Enabled for all builds that include the CoreUObject project.  
		// Disabled only when building standalone apps that only link with Core.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileAgainstCoreUObject = true;

		// Enabled for builds that need to initialize the ApplicationCore module. Command line utilities do not normally need this.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileAgainstApplicationCore = true;

		// Whether to compile Recast navmesh generation.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileRecast")]
		public bool bCompileRecast = true;

		// Whether to compile SpeedTree support.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileSpeedTree")]
		bool? bOverrideCompileSpeedTree;

		// Whether we should compile in support for Simplygon or not.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileSpeedTree
		{
			set { bOverrideCompileSpeedTree = value; }
			get { return bOverrideCompileSpeedTree ?? Type == TargetType.Editor; }
		}

		// Enable exceptions for all modules.
		[RequiresUniqueBuildEnvironment]
		public bool bForceEnableExceptions = false;

		// Enable inlining for all modules.
		[RequiresUniqueBuildEnvironment]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseInlining = true;

		// Enable exceptions for all modules.
		[RequiresUniqueBuildEnvironment]
		public bool bForceEnableObjCExceptions = false;

		// Enable RTTI for all modules.
		[CommandLine("-rtti")]
		[RequiresUniqueBuildEnvironment]
		public bool bForceEnableRTTI = false;

		// Compile server-only code.
		[RequiresUniqueBuildEnvironment]
		public bool bWithServerCode
		{
			get { return bWithServerCodeOverride ?? (Type != TargetType.Client); }
			set { bWithServerCodeOverride = value; }
		}

		private bool? bWithServerCodeOverride;

		// When enabled, Push Model Networking will be used on the server.
		// This can help reduce CPU overhead of networking, at the cost of more memory.
		[RequiresUniqueBuildEnvironment]
		public bool bWithPushModel = false;

		// Whether to include stats support even without the engine.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileWithStatsWithoutEngine = false;

		// Whether to include plugin support.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileWithPluginSupport")]
		public bool bCompileWithPluginSupport = false;

		// Whether to allow plugins which support all target platforms.
		[RequiresUniqueBuildEnvironment]
		public bool bIncludePluginsForTargetPlatforms
		{
			get { return bIncludePluginsForTargetPlatformsOverride ?? (Type == TargetType.Editor); }
			set { bIncludePluginsForTargetPlatformsOverride = value; }
		}

		private bool? bIncludePluginsForTargetPlatformsOverride;

		// Whether to allow accessibility code in both Slate and the OS layer.
		[RequiresUniqueBuildEnvironment]
		public bool bCompileWithAccessibilitySupport = true;

		// Whether to include PerfCounters support.
		[RequiresUniqueBuildEnvironment]
        public bool bWithPerfCounters
		{
			get { return bWithPerfCountersOverride ?? (Type == TargetType.Editor || Type == TargetType.Server); }
			set { bWithPerfCountersOverride = value; }
		}

		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bWithPerfCounters")]
		bool? bWithPerfCountersOverride;

		// Whether to enable support for live coding
		[RequiresUniqueBuildEnvironment]
		public bool bWithLiveCoding
		{
			get { return bWithLiveCodingPrivate ?? 
					(Platform == BuildTargetPlatform.Win64 && Configuration != TargetConfiguration.Shipping && 
					Type != TargetType.Program); }
			set { bWithLiveCodingPrivate = value; }
		}

		bool? bWithLiveCodingPrivate;

		// Whether to enable support for live coding
		[RequiresUniqueBuildEnvironment]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseDebugLiveCodingConsole = false;
		
		// Whether to enable support for DirectX Math
		[RequiresUniqueBuildEnvironment]
		public bool bWithDirectXMath = false;

		// Whether to turn on logging for test/shipping builds.
		[RequiresUniqueBuildEnvironment]
		public bool bUseLoggingInShipping = false;

		// Whether to turn on logging to memory for test/shipping builds.
		[RequiresUniqueBuildEnvironment]
		public bool bLoggingToMemoryEnabled;

		// Whether to check that the process was launched through an external launcher.
		public bool bUseLauncherChecks = false;

		// Whether to turn on checks (asserts) for test/shipping builds.
		[RequiresUniqueBuildEnvironment]
		public bool bUseChecksInShipping = false;

		// True if we need FreeType support.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileFreeType")]
		public bool bCompileFreeType = true;

		// True if we want to favor optimizing size over speed.
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/BuildSettings.BuildSettings", "bCompileForSize")]
		public bool bCompileForSize = false;

		// Whether to compile development automation tests.
		public bool bForceCompileDevelopmentAutomationTests = false;

		// Whether to compile performance automation tests.
		public bool bForceCompilePerformanceAutomationTests = false;

		// If true, event driven loader will be used in cooked builds.
		[RequiresUniqueBuildEnvironment]
		public bool bEventDrivenLoader;

		// Whether the XGE controller worker and modules should be included in the engine build.
		// These are required for distributed shader compilation using the XGE interception interface.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseXGEController = true;

		// Whether to use backwards compatible defaults for this module.
		// By default, engine modules always use the latest default settings, while project modules do not (to support an easier migration path).
		[Obsolete("Set DefaultBuildSettings to the appropriate engine version (eg. BuildSettingsVersion.Release_4_23) or BuildSettingsVersion.Latest instead")]
		public bool bUseBackwardsCompatibleDefaults
		{
			get { return DefaultBuildSettings != BuildSettingsVersion.Latest; }
			set { DefaultBuildSettings = (value ? BuildSettingsVersion.V1 : BuildSettingsVersion.Latest); }
		}

		// Enables "include what you use" by default for modules in this target.
		// Changes the default PCH mode for any module in this project to PCHUsageMode.UseExplicitOrSharedPCHs.
		[CommandLine("-IWYU")]
		public bool bIWYU = false;

		// Enforce "include what you use" rules;
		// warns if monolithic headers (Engine.h, EditorEd.h, etc...) are used,
		// and checks that source files include their matching header first.
		public bool bEnforceIWYU = true;

		// Whether the final executable should export symbols.
		public bool bHasExports
		{
			get { return bHasExportsOverride ?? (LinkType == TargetLinkType.Modular); }
			set { bHasExportsOverride = value; }
		}
		private bool? bHasExportsOverride;

		// Make static libraries for all engine modules as intermediates for this target.
		[CommandLine("-Precompile")]
		public bool bPrecompile = false;

		// Whether we should compile with support for OS X 10.9 Mavericks.
		// Used for some tools that we need to be compatible with this version of OS X.
		public bool bEnableOSX109Support = false;

		// True if this is a console application that's being built.
		public bool bIsBuildingConsoleApplication = false;

		// If true, creates an additional console application. Hack for Windows, where it's not possible to conditionally inherit a parent's console Window depending on how
		// the application is invoked; you have to link the same executable with a different subsystem setting.
		public bool bBuildAdditionalConsoleApp
		{
			get { return bBuildAdditionalConsoleAppOverride ?? (Type == TargetType.Editor); }
			set { bBuildAdditionalConsoleAppOverride = value; }
		}
		private bool? bBuildAdditionalConsoleAppOverride;

		// True if debug symbols that are cached for some platforms should not be created.
		public bool bDisableSymbolCache = true;

		// Whether to unify C++ code into larger files for faster compilation.
		[CommandLine("-DisableUnity", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseUnityBuild = true;

		// Whether to force C++ source files to be combined into larger files for faster compilation.
		[CommandLine("-ForceUnity")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bForceUnityBuild = false;

		// Use a heuristic to determine which files are currently being iterated on and exclude them from unity blobs, result in faster
		// incremental compile times. The current implementation uses the read-only flag to distinguish the working set, assuming that files will
		// be made writable by the source control system if they are being modified. This is true for Perforce, but not for Git.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseAdaptiveUnityBuild = true;

		// Disable optimization for files that are in the adaptive non-unity working set.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAdaptiveUnityDisablesOptimizations = false;

		// Disables force-included PCHs for files that are in the adaptive non-unity working set.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAdaptiveUnityDisablesPCH = false;

		// Backing storage for bAdaptiveUnityDisablesProjectPCH.
		[XMLConfigFile(Category = "BuildConfiguration")]
		bool? bAdaptiveUnityDisablesProjectPCHForProjectPrivate;

		// Whether to disable force-included PCHs for project source files in the adaptive non-unity working set.
		// Defaults to bAdaptiveUnityDisablesPCH;
		public bool bAdaptiveUnityDisablesPCHForProject
		{
			get { return bAdaptiveUnityDisablesProjectPCHForProjectPrivate ?? bAdaptiveUnityDisablesPCH; }
			set { bAdaptiveUnityDisablesProjectPCHForProjectPrivate = value; }
		}

		// Creates a dedicated PCH for each source file in the working set,
		// allowing faster iteration on cpp-only changes.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAdaptiveUnityCreatesDedicatedPCH = false;

		// Creates a dedicated PCH for each source file in the working set,
		// allowing faster iteration on cpp-only changes.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAdaptiveUnityEnablesEditAndContinue = false;

		// The number of source files in a game module before unity build will be activated for that module.  This
		// allows small game modules to have faster iterative compile times for single files, at the expense of slower full
		// rebuild times.  This setting can be overridden by the bFasterWithoutUnity option in a module's Build.cs file.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public int MinGameModuleSourceFilesForUnityBuild = 32;

		// Forces shadow variable warnings to be treated as errors on platforms that support it.
		[CommandLine("-ShadowVariableErrors", Value = nameof(WarningLevel.Error))]
		public WarningLevel ShadowVariableWarningLevel = WarningLevel.Warning;

		// Indicates what warning/error level to treat unsafe type casts as on platforms that support it
		// (e.g., double->float or int64->int32)
		[XMLConfigFile(Category = "BuildConfiguration")]
		public WarningLevel UnsafeTypeCastWarningLevel = WarningLevel.Off;

		// Forces the use of undefined identifiers in conditional expressions to be treated as errors.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUndefinedIdentifierErrors = true;

		// New Monolithic Graphics drivers have optional "fast calls" replacing various D3d functions
		[CommandLine("-FastMonoCalls", Value = "true")]
		[CommandLine("-NoFastMonoCalls", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseFastMonoCalls = true;
		// New Xbox driver supports a "fast semantics" context type.
		// This switches it on for the immediate and deferred contexts
		// Try disabling this if you see rendering issues and/or crashes inthe Xbox RHI.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseFastSemanticsRenderContexts = true;

		// An approximate number of bytes of C++ code to target for inclusion in a single unified C++ file.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public int NumIncludedBytesPerUnityCPP = 384 * 1024;

		// Whether to stress test the C++ unity build robustness by including all C++ files files in a project from a single unified file.
		[CommandLine("-StressTestUnity")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bStressTestUnity = false;

		// Whether to force debug info to be generated.
		[CommandLine("-ForceDebugInfo")]
		public bool bForceDebugInfo = false;

		// Whether to globally disable debug info generation;
		// see DebugInfoHeuristics.cs for per-config and per-platform options.
		[CommandLine("-NoDebugInfo")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bDisableDebugInfo = false;

		// Whether to disable debug info generation for generated files.
		// This improves link times for modules that have a lot of generated glue code.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bDisableDebugInfoForGeneratedCode = false;

		// Whether to disable debug info on PC in development builds
		// (for faster developer iteration, as link times are extremely fast with debug info disabled).
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bOmitPCDebugInfoInDevelopment = false;

		// Whether PDB files should be used for Visual C++ builds.
		[CommandLine("-NoPDB", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUsePDBFiles = false;
		
		// Whether PCH files should be used.
		[CommandLine("-NoPCH", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUsePCHFiles = true;

		// Whether to just preprocess source files for this target, and skip compilation
		[CommandLine("-Preprocess")]
		public bool bPreprocessOnly = false;

		// The minimum number of files that must use a pre-compiled header before it will be created and used.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public int MinFilesUsingPrecompiledHeader = 6;

		// When enabled, a precompiled header is always generated for game modules,
		// even if there are only a few source files in the module.
		// This greatly improves compile times for iterative changes on a few files in the project,
		// at the expense of slower full rebuild times for small game projects.
		// This can be overridden by setting MinFilesUsingPrecompiledHeaderOverride in a module's Build.cs file.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bForcePrecompiledHeaderForGameModules = true;

		// Whether to use incremental linking or not.
		// Incremental linking can yield faster iteration times when making small changes.
		// Currently disabled by default because it tends to behave a bit buggy on some computers (PDB-related compile errors).
		[CommandLine("-IncrementalLinking")]
		[CommandLine("-NoIncrementalLinking", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseIncrementalLinking = false;

		// Whether to allow the use of link time code generation (LTCG).
		[CommandLine("-LTCG")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAllowLTCG = false;

		// Whether to enable Profile Guided Optimization (PGO) instrumentation in this build.
		[CommandLine("-PGOProfile", Value = "true")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bPGOProfile = false;

		// Whether to optimize this build with Profile Guided Optimization (PGO).
		[CommandLine("-PGOOptimize", Value = "true")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bPGOOptimize = false;

		// Whether to allow the use of ASLR (address space layout randomization) if supported.
		// Only applies to shipping builds.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAllowASLRInShipping = true;

		// Whether to support edit and continue.  Only works on Microsoft compilers.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bSupportEditAndContinue = false;

		// Whether to omit frame pointers or not. Disabling is useful for e.g. memory profiling on the PC.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bOmitFramePointers = true;

		// If true, then enable memory profiling in the build (defines USE_MALLOC_PROFILER=1 and forces bOmitFramePointers=false).
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseMallocProfiler = false;

		// Enables "Shared PCHs", a feature which significantly speeds up compile times by attempting to
		// share certain PCH files between modules that UBT detects is including those PCH's header files.
		[CommandLine("-NoSharedPCH", Value = "false")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseSharedPCHs = true;

		// True if Development and Release builds should use the release configuration of PhysX/APEX.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseShippingPhysXLibraries = false;

		// True if Development and Release builds should use the checked configuration of PhysX/APEX. if bUseShippingPhysXLibraries is true this is ignored.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bUseCheckedPhysXLibraries = false;

		// Tells the UBT to check if module currently being built is violating EULA.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bCheckLicenseViolations = true;

		// Tells the UBT to break build if module currently being built is violating EULA.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bBreakBuildOnLicenseViolation = true;
		
		// Whether to use the :FASTLINK option when building with /DEBUG to create local PDBs on Windows.
		// Fast, but currently seems to have problems finding symbols in the debugger.
		[CommandLine("-FastPDB")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool? bUseFastPDBLinking;

		// Outputs a map file as part of the build.
		[CommandLine("-MapFile")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bCreateMapFile = false;

		// True if runtime symbols files should be generated as a post build step for some platforms.
		// These files are used by the engine to resolve symbol names of callstack backtraces in logs.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAllowRuntimeSymbolFiles = true;

		// Bundle version for Mac apps.
		[CommandLine("-BundleVersion")]
		public string BundleVersion = null;

		// Whether to deploy the executable after compilation on platforms that require deployment.
		[CommandLine("-Deploy")]
		[CommandLine("-SkipDeploy", Value = "false")]
		public bool bDeployAfterCompile = false;

		// When enabled, allows XGE to compile pre-compiled header files on remote machines.
		// Otherwise, PCHs are always generated locally.
		public bool bAllowRemotelyCompiledPCHs = false;

		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bCheckSystemHeadersForModification;

		// Whether to disable linking for this target.
		[CommandLine("-NoLink")]
		public bool bDisableLinking = false;

		// Indicates that this is a formal build, intended for distribution. This flag is automatically set to true when Build.version has a changelist set.
		// The only behavior currently bound to this flag is to compile the default resource file separately for each binary so that the OriginalFilename field is set correctly.
		// By default, we only compile the resource once to reduce build times.
		[CommandLine("-Formal")]
		public bool bFormalBuild = false;

		// Whether to clean Builds directory on a remote Mac before building.
		[CommandLine("-FlushMac")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bFlushBuildDirOnRemoteMac = false;

		// Whether to write detailed timing info from the compiler and linker.
		[CommandLine("-Timing")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bPrintToolChainTimingInfo = false;

		// Whether to parse timing data into a tracing file compatible with chrome://tracing.
		[CommandLine("-Tracing")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bParseTimingInfoForTracing = false;

		// Whether to expose all symbols as public by default on POSIX platforms
		[CommandLine("-PublicSymbolsByDefault")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bPublicSymbolsByDefault = false;

		// Allows overriding the toolchain to be created for this target.
		// This must match the name of a class declared in the BuildTool assembly.
		[CommandLine("-ToolChain")]
		public string ToolChainName = null;

		// Whether to allow engine configuration to determine if we can load unverified certificates.
		public bool bDisableUnverifiedCertificates = false;

		// Whether to load generated ini files in cooked build, (GameUserSettings.ini loaded either way)
		public bool bAllowGeneratedIniWhenCooked = true;

		// Whether to load non-ufs ini files in cooked build, (GameUserSettings.ini loaded either way)
		public bool bAllowNonUFSIniWhenCooked = true;

		// Add all the public folders as include paths for the compile environment.
		public bool bLegacyPublicIncludePaths
		{
			get { return bLegacyPublicIncludePathsPrivate ?? (DefaultBuildSettings < BuildSettingsVersion.V2); }
			set { bLegacyPublicIncludePathsPrivate = value; }
		}
		private bool? bLegacyPublicIncludePathsPrivate;

		// Which C++ stanard to use for compiling this target
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-CppStd")]
		[XMLConfigFile(Category = "BuildConfiguration")]
		public CppStandardVersion CppStandard = CppStandardVersion.Default;

		// Do not allow manifest changes when building this target.
		// Used to cause earlier errors when building multiple targets with a shared build environment.
		[CommandLine("-NoManifestChanges")]
		internal bool bNoManifestChanges = false;

		// The build version string
		[CommandLine("-BuildVersion")]
		public string BuildVersion;

		// Specifies how to link modules in this target (monolithic or modular).
		// This is currently protected for backwards compatibility. Call the GetLinkType() accessor
		// until support for the deprecated ShouldCompileMonolithic() override has been removed.
		public TargetLinkType LinkType
		{
			get
			{
				return (LinkTypePrivate != TargetLinkType.Default)? 
					LinkTypePrivate : 
					((Type == global::BuildTool.TargetType.Editor)? TargetLinkType.Modular : TargetLinkType.Monolithic);
			}
			set
			{
				LinkTypePrivate = value;
			}
		}

		// Backing storage for the LinkType property.
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-Monolithic", Value ="Monolithic")]
		[CommandLine("-Modular", Value ="Modular")]
		TargetLinkType LinkTypePrivate = TargetLinkType.Default;

		// Macros to define globally across the whole target.
		[RequiresUniqueBuildEnvironment]
		[CommandLine("-Define:")]
		public List<string> GlobalDefinitions = new List<string>();

		// Macros to define across all macros in the project.
		public List<string> ProjectDefinitions = new List<string>();

		// Specifies the name of the launch module. For modular builds, this is the module that is compiled into the target's executable.
		public string LaunchModuleName
		{
			get
			{
				return (LaunchModuleNamePrivate == null && Type != global::BuildTool.TargetType.Program)? Tag.Module.Engine.Launch : LaunchModuleNamePrivate;
			}
			set
			{
				LaunchModuleNamePrivate = value;
			}
		}

		private string LaunchModuleNamePrivate; // Backing storage for the LaunchModuleName property.

		// Specifies the path to write a header containing public definitions for this target.
		// Useful when building a DLL to be consumed by external build processes.
		public string ExportPublicHeader;

		public List<string> ExtraModuleNames = new List<string>(); // List of additional modules to be compiled into the target.

		[CommandLine("-Manifest")]
		public List<FileReference> OutputManifestFileNames = new List<FileReference>();

		[CommandLine("-DependencyList")]
		public List<FileReference> PrecompileDependencyFiles = new List<FileReference>(); // Path to a list of dependencies for this target, when precompiling

		[CommandLine("-SharedBuildEnvironment", Value = "Shared")]
		[CommandLine("-UniqueBuildEnvironment", Value = "Unique")]
		private TargetBuildEnvironment? BuildEnvironmentOverride; // Backing storage for the BuildEnvironment property

		// Specifies the build environment for this target.
		// See TargetBuildEnvironment for more information on the available options.
		public TargetBuildEnvironment BuildEnvironment
		{
			get
			{
				if(BuildEnvironmentOverride.HasValue)
				{
					return BuildEnvironmentOverride.Value;
				}
				if (Type == TargetType.Program && ProjectFile != null && File.IsUnderDirectory(ProjectFile.Directory))
				{
					return TargetBuildEnvironment.Unique;
				}
				else if (BuildTool.IsEngineInstalled() || LinkType != TargetLinkType.Monolithic)
				{
					return TargetBuildEnvironment.Shared;
				}
				else
				{
					return TargetBuildEnvironment.Unique;
				}
			}
			set
			{
				BuildEnvironmentOverride = value;
			}
		}

		// Whether to ignore violations to the shared build environment (eg. editor targets modifying definitions)
		[CommandLine("-OverrideBuildEnvironment")]
		public bool bOverrideBuildEnvironment = false;

		// Specifies a list of steps which should be executed before this target is built, in the context of the host platform's shell.
		// The following variables will be expanded before execution:
		// $(EngineDir), $(ProjectDir), $(TargetName), $(TargetPlatform), $(TargetConfiguration), $(TargetType), $(ProjectFile).
		public List<string> PreBuildSteps = new List<string>();

		// Specifies a list of steps which should be executed after this target is built, in the context of the host platform's shell.
		// The following variables will be expanded before execution:
		// $(EngineDir), $(ProjectDir), $(TargetName), $(TargetPlatform), $(TargetConfiguration), $(TargetType), $(ProjectFile).
		public List<string> PostBuildSteps = new List<string>();

		// Specifies additional build products produced as part of this target.
		public List<string> AdditionalBuildProducts = new List<string>();

		[RequiresUniqueBuildEnvironment]
		[CommandLine("-CompilerArguments=")]
		public string AdditionalCompilerArguments;

		[RequiresUniqueBuildEnvironment]
		[CommandLine("-LinkerArguments=")]
		public string AdditionalLinkerArguments;

		[XMLConfigFile(Category = "ModuleConfiguration", Name = "DisableUnityBuild")]
		public string[] DisableUnityBuildForModules = null;

		[XMLConfigFile(Category = "ModuleConfiguration", Name = "EnableOptimizeCode")]
		public string[] EnableOptimizeCodeForModules = null;

		[XMLConfigFile(Category = "ModuleConfiguration", Name = "DisableOptimizeCode")]
		public string[] DisableOptimizeCodeForModules = null;

		// When generating project files,
		// specifies the name of the project file to use when there are multiple targets of the same type.
		public string GeneratedProjectName;

		[ConfigSubObject]
		public AndroidTargetRules AndroidPlatform = new AndroidTargetRules();

		[ConfigSubObject]
		public IOSTargetRules IOSPlatform = new IOSTargetRules();

		[ConfigSubObject]
		public LuminTargetRules LuminPlatform = new LuminTargetRules();

		// Linux-specific target settings.
		[ConfigSubObject]
		public LinuxTargetRules LinuxPlatform = new LinuxTargetRules();

		[ConfigSubObject]
		public MacTargetRules MacPlatform = new MacTargetRules();

		[ConfigSubObject]
		public PS4TargetRules PS4Platform = new PS4TargetRules();

		[ConfigSubObject]
		public SwitchTargetRules SwitchPlatform = new SwitchTargetRules();

		[ConfigSubObject]
		public WindowsTargetRules WindowsPlatform; // Requires 'this' parameter; initialized in constructor

		[ConfigSubObject]
		public XboxOneTargetRules XboxOnePlatform = new XboxOneTargetRules();

		[ConfigSubObject]
		public HoloLensTargetRules HoloLensPlatform;

		private static string FormatHexBytes(byte[] DataToConvertToSTring)
		{
			return String.Join(",", DataToConvertToSTring.Select(x => String.Format("0x{0:X2}", x)));
		}

		#region GETTERSETTER

		public bool bGenerateProjectFiles => ProjectFileGenerator.bGenerateProjectFiles;
		public bool bIsEngineInstalled => BuildTool.IsEngineInstalled();

		// Override any settings required for the selected target type
		internal void SetGlobalDefinitionsForTargetType()
		{
			if (Type == global::BuildTool.TargetType.Game)
			{
				GlobalDefinitions.Add(Tag.CppContents.Def.TargetGame + Tag.Boolean.One);
			}
			else if (Type == global::BuildTool.TargetType.Client)
			{
				GlobalDefinitions.Add(Tag.CppContents.Def.TargetGame + Tag.Boolean.One);
				GlobalDefinitions.Add(Tag.CppContents.Def.TargetClient + Tag.Boolean.One);
			}
			else if (Type == global::BuildTool.TargetType.Editor)
            {
				GlobalDefinitions.Add(Tag.CppContents.Def.Editor + Tag.Boolean.One);
            }
			else if (Type == global::BuildTool.TargetType.Server)
			{
				GlobalDefinitions.Add(Tag.CppContents.Def.TargetServer + Tag.Boolean.One);
				GlobalDefinitions.Add(Tag.CppContents.Def.UseNullRHI + Tag.Boolean.One);
			}
			else
            {
				throw new BuildException("Invalid TargetType.");
            }
		}

		// Checks whether nativization is enabled for this target, and determine the path for the nativized plugin
		// <returns>The nativized plugin file, or null if nativization is not enabled</returns>
		internal FileReference GetNativizedPlugin()
		{
			if (ProjectFile != null && (Type == TargetType.Game || Type == TargetType.Client || Type == TargetType.Server))
			{
				// Read the config files for this project
				ConfigHierarchy Config = ConfigCache.ReadHierarchy(ConfigHierarchyType.Game, ProjectFile.Directory, BuildHostPlatform.Current.Platform);
				if (Config != null)
				{
					// Determine whether or not the user has enabled nativization of Blueprint assets at cook time (default is 'Disabled')
					if (Config.TryGetValue(Tag.ConfigSection.ProjectPackagingSettings, Tag.ConfigKey.BlueprintNativizationMethod, out string NativizationMethod) 
						&& NativizationMethod != Tag.ConfigValue.Disabled)
					{
						string PlatformName;
						if (Platform == BuildTargetPlatform.Win32 || 
							Platform == BuildTargetPlatform.Win64)
						{
							PlatformName = Tag.PlatformGroup.Windows;
						}
						else
						{
							PlatformName = Platform.ToString();
						}

						// Temp fix to force platforms that only support "Game" configurations at cook time to the correct path.
						string ProjectTargetType;

						if (Platform == BuildTargetPlatform.Win32 || 
							Platform == BuildTargetPlatform.Win64 || 
							Platform == BuildTargetPlatform.Linux || 
							Platform == BuildTargetPlatform.Mac)
						{
							ProjectTargetType = Type.ToString();
						}
						else
						{
							ProjectTargetType = TargetType.Game.ToString();
						}

						FileReference PluginFile = FileReference.Combine(ProjectFile.Directory, Tag.Directory.Generated, Tag.Directory.Plugins, Tag.Directory.NativizedAssets, PlatformName, ProjectTargetType, Tag.Directory.NativizedAssets + Tag.Ext.Plugin);
						if (FileReference.Exists(PluginFile))
						{
							return PluginFile;
						}
						else
						{
							Log.TraceWarning("{0} is configured for nativization, but is missing the generated code plugin at \"{1}\". Make sure to cook {2} data before attempting to build the {3} target. If data was cooked with nativization enabled, this can also mean there were no Blueprint assets that required conversion, in which case this warning can be safely ignored.", Name, PluginFile.FullName, Type.ToString(), Platform.ToString());
						}
					}
				}
			}
			return null;
		}

		// Gets a list of platforms that this target supports
		internal BuildTargetPlatform[] GetSupportedPlatforms()
		{
			// Otherwise take the SupportedPlatformsAttribute from the first type in the inheritance chain that supports it
			for (Type CurrentType = GetType(); CurrentType != null; CurrentType = CurrentType.BaseType)
			{
				object[] Attributes = CurrentType.GetCustomAttributes(typeof(SupportedPlatformsAttribute), false);
				if (Attributes.Length > 0)
				{
					return Attributes.OfType<SupportedPlatformsAttribute>().SelectMany(x => x.Platforms).Distinct().ToArray();
				}
			}

			// Otherwise, get the default for the target type
			if (Type == TargetType.Program)
			{
				return Utils.GetPlatformsInClass(BuildPlatformClass.Desktop);
			}
			else if (Type == TargetType.Editor)
			{
				return Utils.GetPlatformsInClass(BuildPlatformClass.Editor);
			}
			else
			{
				return Utils.GetPlatformsInClass(BuildPlatformClass.All);
			}
		}

		// Gets a list of configurations that this target supports
		internal TargetConfiguration[] GetSupportedConfigurations()
		{
			// Otherwise take the SupportedConfigurationsAttribute from the first type in the inheritance chain that supports it
			for (Type CurrentType = GetType(); CurrentType != null; CurrentType = CurrentType.BaseType)
			{
				object[] Attributes = CurrentType.GetCustomAttributes(typeof(SupportedConfigurationsAttribute), false);
				if (0 < Attributes.Length)
				{
					return Attributes.OfType<SupportedConfigurationsAttribute>().SelectMany(x => x.Configurations).Distinct().ToArray();
				}
			}

			// Otherwise, get the default for the target type
			if (Type == TargetType.Editor)
			{
				return new[] { TargetConfiguration.Debug, TargetConfiguration.DebugGame, TargetConfiguration.Development };
			}
			else
			{
				return ((TargetConfiguration[])Enum.GetValues(typeof(TargetConfiguration))).Where(x => x != TargetConfiguration.Unknown).ToArray();
			}
		}

		// Finds all the subobjects which can be configured by command line options and config files
		internal IEnumerable<object> GetConfigurableObjects()
		{
			yield return this;

			foreach (FieldInfo Field in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				if (Field.GetCustomAttribute<ConfigSubObjectAttribute>() != null)
				{
					yield return Field.GetValue(this);
				}
			}
		}

		public BuildTargetPlatform HostPlatform => BuildHostPlatform.Current.Platform;

		internal void GetBuildSettingsInfo(List<string> DiagnosticMessages)
		{
			if(DefaultBuildSettings < BuildSettingsVersion.V2)
			{
				List<Tuple<string, string>> ModifiedSettings = new List<Tuple<string, string>>();

				if(DefaultBuildSettings < BuildSettingsVersion.V2)
				{
					ModifiedSettings.Add(Tuple.Create(String.Format("{0} = false", nameof(bLegacyPublicIncludePaths)), "Omits subfolders from public include paths to reduce compiler command line length. (Previously: true)."));
					ModifiedSettings.Add(Tuple.Create(String.Format("{0}" + nameof(WarningLevel) + nameof(WarningLevel.Error), nameof(ShadowVariableWarningLevel)), "Treats shadowed variable warnings as errors. (Previously: WarningLevel.Warning)."));
					ModifiedSettings.Add(Tuple.Create(String.Format("{0}" + nameof(ModuleRules.PCHUsage) + nameof(ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs), nameof(ModuleRules.PCHUsage)), "Set in build.cs files to enables IWYU-style PCH model. (Previously: PCHUsageMode.UseSharedPCHs)."));
				}

				if (0 < ModifiedSettings.Count)
				{
					string FormatString = String.Format("[Upgrade]     {{0,-{0}}}   => {{1}}", ModifiedSettings.Max(x => x.Item1.Length));
					foreach (Tuple<string, string> ModifiedSetting in ModifiedSettings)
					{
						DiagnosticMessages.Add(String.Format(FormatString, ModifiedSetting.Item1, ModifiedSetting.Item2));
					}
				}
				DiagnosticMessages.Add(String.Format("[Upgrade] Suppress this message by setting 'DefaultBuildSettings = BuildSettingsVersion.{1};' in {2}, and explicitly overriding settings that differ from the new defaults.", Version, (BuildSettingsVersion)(BuildSettingsVersion.Latest - 1), File.GetFileName()));
				DiagnosticMessages.Add("[Upgrade]");
			}
		}

#endregion GETTERSETTER
	}

	// Read-only wrapper around an existing TargetRules instance.
	// This exposes target settings to modules without letting them to modify the global environment.
	public partial class ReadOnlyTargetRules
	{
		readonly TargetRules Inner; // The writeable TargetRules instance

		public ReadOnlyTargetRules(TargetRules Inner)
		{
			this.Inner = Inner;
			AndroidPlatform = new ReadOnlyAndroidTargetRules(Inner.AndroidPlatform);
			IOSPlatform = new ReadOnlyIOSTargetRules(Inner.IOSPlatform);
			LuminPlatform = new ReadOnlyLuminTargetRules(Inner.LuminPlatform);
			LinuxPlatform = new ReadOnlyLinuxTargetRules(Inner.LinuxPlatform);
			MacPlatform = new ReadOnlyMacTargetRules(Inner.MacPlatform);
			PS4Platform = new ReadOnlyPS4TargetRules(Inner.PS4Platform);
			SwitchPlatform = new ReadOnlySwitchTargetRules(Inner.SwitchPlatform);
			WindowsPlatform = new ReadOnlyWindowsTargetRules(Inner.WindowsPlatform);
			XboxOnePlatform = new ReadOnlyXboxOneTargetRules(Inner.XboxOnePlatform);
			HoloLensPlatform = new ReadOnlyHoloLensTargetRules(Inner.HoloLensPlatform);
		}

		// Provide access to the RelativeEnginePath property for code referencing ModuleRules.BuildConfiguration.
		public string RelativeEnginePath => BuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory());

		public bool IsInPlatformGroup(BuildPlatformGroup PlatformGroupToCheck) => BuildPlatform.IsPlatformInGroup(Platform, PlatformGroupToCheck);

		internal void GetBuildSettingsInfo(List<string> DiagnosticsToBeAppend) => Inner.GetBuildSettingsInfo(DiagnosticsToBeAppend);

		// Accessors for fields on the inner TargetRules instance
		#region Read-only accessor properties
#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif

		public string Name => Inner.Name;
		internal FileReference File => Inner.File;
		public BuildTargetPlatform Platform => Inner.Platform;
		public TargetConfiguration Configuration => Inner.Configuration;
		public string Architecture => Inner.Architecture;
		public FileReference ProjectFile => Inner.ProjectFile;
		public ReadOnlyBuildVersion Version => Inner.Version;
		public TargetType Type => Inner.Type;
		public BuildSettingsVersion DefaultBuildSettings => Inner.DefaultBuildSettings;
		internal ConfigValueTracker ConfigValueTracker => Inner.ConfigValueTracker;
		public bool bUsesSteam => Inner.bUsesSteam;
		public bool bUsesCEF3 => Inner.bUsesCEF3;
		public bool bUsesSlate => Inner.bUsesSlate;
		public bool bUseStaticCRT => Inner.bUseStaticCRT;
		public bool bDebugBuildsActuallyUseDebugCRT => Inner.bDebugBuildsActuallyUseDebugCRT;
		public bool bLegalToDistributeBinary => Inner.bLegalToDistributeBinary;
		public TargetConfiguration UndecoratedConfiguration => Inner.UndecoratedConfiguration;
		public bool bBuildAllModules => Inner.bBuildAllModules;
		public IEnumerable<string> AdditionalPlugins => Inner.AdditionalPlugins;
		public IEnumerable<string> EnablePlugins => Inner.EnablePlugins;
		public IEnumerable<string> DisablePlugins => Inner.DisablePlugins;
		public IEnumerable<string> BuildPlugins => Inner.BuildPlugins;
		public string PakSigningKeysFile => Inner.PakSigningKeysFile;
		public string SolutionDirectory => Inner.SolutionDirectory;
		public bool? bBuildInSolutionByDefault => Inner.bBuildInSolutionByDefault;
		public string ExeBinariesSubFolder => Inner.ExeBinariesSubFolder;
		public EGeneratedCodeVersion GeneratedCodeVersion => Inner.GeneratedCodeVersion;
		public bool bEnableMeshEditor => Inner.bEnableMeshEditor;
		public bool bCompileChaos => Inner.bCompileChaos;
		public bool bUseChaos => Inner.bUseChaos;
		public bool bUseChaosMemoryTracking => Inner.bUseChaosMemoryTracking;
		public bool bUseChaosChecked => Inner.bUseChaosChecked;
		public bool bCustomSceneQueryStructure => Inner.bCustomSceneQueryStructure;
		public bool bCompilePhysX => Inner.bCompilePhysX;
		public bool bCompileAPEX => Inner.bCompileAPEX;
		public bool bCompileNvCloth => Inner.bCompileNvCloth;
		public bool bCompileICU => Inner.bCompileICU;
		public bool bCompileCEF3 => Inner.bCompileCEF3;
		public bool bCompileISPC => Inner.bCompileISPC;
		public bool bBuildEditor => Inner.bBuildEditor;
		public bool bBuildRequiresCookedData => Inner.bBuildRequiresCookedData;
		public bool bBuildWithEditorOnlyData => Inner.bBuildWithEditorOnlyData;
		public bool bBuildDeveloperTools => Inner.bBuildDeveloperTools;
		public bool bForceBuildTargetPlatforms => Inner.bForceBuildTargetPlatforms;
		public bool bForceBuildShaderFormats => Inner.bForceBuildShaderFormats;
		public bool bCompileCustomSQLitePlatform => Inner.bCompileCustomSQLitePlatform;
		public bool bUseCacheFreedOSAllocs => Inner.bUseCacheFreedOSAllocs;
		public bool bCompileAgainstEngine => Inner.bCompileAgainstEngine;
		public bool bCompileAgainstCoreUObject => Inner.bCompileAgainstCoreUObject;
		public bool bCompileAgainstApplicationCore => Inner.bCompileAgainstApplicationCore;
		public bool bCompileRecast => Inner.bCompileRecast;
		public bool bCompileSpeedTree => Inner.bCompileSpeedTree;
		public bool bForceEnableExceptions => Inner.bForceEnableExceptions;
		public bool bForceEnableObjCExceptions => Inner.bForceEnableObjCExceptions;
		public bool bForceEnableRTTI => Inner.bForceEnableRTTI;
		public bool bUseInlining => Inner.bUseInlining;
		public bool bWithServerCode => Inner.bWithServerCode;
		public bool bWithPushModel => Inner.bWithPushModel;
		public bool bCompileWithStatsWithoutEngine => Inner.bCompileWithStatsWithoutEngine;
		public bool bCompileWithPluginSupport => Inner.bCompileWithPluginSupport;
		public bool bIncludePluginsForTargetPlatforms => Inner.bIncludePluginsForTargetPlatforms;
		public bool bCompileWithAccessibilitySupport => Inner.bCompileWithAccessibilitySupport;
		public bool bWithPerfCounters => Inner.bWithPerfCounters;
		public bool bWithLiveCoding => Inner.bWithLiveCoding;
		public bool bUseDebugLiveCodingConsole => Inner.bUseDebugLiveCodingConsole;
		public bool bWithDirectXMath => Inner.bWithDirectXMath;
		public bool bUseLoggingInShipping => Inner.bUseLoggingInShipping;
		public bool bLoggingToMemoryEnabled => Inner.bLoggingToMemoryEnabled;
		public bool bUseLauncherChecks => Inner.bUseLauncherChecks;
		public bool bUseChecksInShipping => Inner.bUseChecksInShipping;
		public bool bCompileFreeType => Inner.bCompileFreeType;
		public bool bCompileForSize => Inner.bCompileForSize;
		public bool bForceCompileDevelopmentAutomationTests => Inner.bForceCompileDevelopmentAutomationTests;
		public bool bForceCompilePerformanceAutomationTests => Inner.bForceCompilePerformanceAutomationTests;
		public bool bUseXGEController => Inner.bUseXGEController;
		public bool bEventDrivenLoader => Inner.bEventDrivenLoader;
		public bool bIWYU => Inner.bIWYU;
		public bool bEnforceIWYU => Inner.bEnforceIWYU;
		public bool bHasExports => Inner.bHasExports;
		public bool bPrecompile => Inner.bPrecompile;
		public bool bEnableOSX109Support => Inner.bEnableOSX109Support;
		public bool bIsBuildingConsoleApplication => Inner.bIsBuildingConsoleApplication;
		public bool bBuildAdditionalConsoleApp => Inner.bBuildAdditionalConsoleApp;
		public bool bDisableSymbolCache => Inner.bDisableSymbolCache;
		public bool bUseUnityBuild => Inner.bUseUnityBuild;
		public bool bForceUnityBuild => Inner.bForceUnityBuild;
		public bool bAdaptiveUnityDisablesOptimizations => Inner.bAdaptiveUnityDisablesOptimizations;
		public bool bAdaptiveUnityDisablesPCH => Inner.bAdaptiveUnityDisablesPCH;
		public bool bAdaptiveUnityDisablesPCHForProject => Inner.bAdaptiveUnityDisablesPCHForProject;
		public bool bAdaptiveUnityCreatesDedicatedPCH => Inner.bAdaptiveUnityCreatesDedicatedPCH;
		public bool bAdaptiveUnityEnablesEditAndContinue => Inner.bAdaptiveUnityEnablesEditAndContinue;
		public int MinGameModuleSourceFilesForUnityBuild => Inner.MinGameModuleSourceFilesForUnityBuild;
		public bool bUndefinedIdentifierErrors => Inner.bUndefinedIdentifierErrors;
		public bool bUseFastMonoCalls => Inner.bUseFastMonoCalls;
		public bool bUseFastSemanticsRenderContexts => Inner.bUseFastSemanticsRenderContexts;
		public int NumIncludedBytesPerUnityCPP => Inner.NumIncludedBytesPerUnityCPP;
		public bool bStressTestUnity => Inner.bStressTestUnity;
		public bool bDisableDebugInfo => Inner.bDisableDebugInfo;
		public bool bDisableDebugInfoForGeneratedCode => Inner.bDisableDebugInfoForGeneratedCode;
		public bool bOmitPCDebugInfoInDevelopment => Inner.bOmitPCDebugInfoInDevelopment;
		public bool bUsePDBFiles => Inner.bUsePDBFiles;
		public bool bUsePCHFiles => Inner.bUsePCHFiles;
		public bool bPreprocessOnly => Inner.bPreprocessOnly;
		public int MinFilesUsingPrecompiledHeader => Inner.MinFilesUsingPrecompiledHeader;
		public bool bForcePrecompiledHeaderForGameModules => Inner.bForcePrecompiledHeaderForGameModules;
		public bool bUseIncrementalLinking => Inner.bUseIncrementalLinking;
		public bool bAllowLTCG => Inner.bAllowLTCG;
		public bool bPGOProfile => Inner.bPGOProfile;
		public bool bPGOOptimize => Inner.bPGOOptimize;
		public bool bAllowASLRInShipping => Inner.bAllowASLRInShipping;
		public bool bSupportEditAndContinue => Inner.bSupportEditAndContinue;
		public bool bOmitFramePointers => Inner.bOmitFramePointers;
		public bool bUseMallocProfiler => Inner.bUseMallocProfiler;
		public bool bUseSharedPCHs => Inner.bUseSharedPCHs;
		public bool bUseShippingPhysXLibraries => Inner.bUseShippingPhysXLibraries;
		public bool bUseCheckedPhysXLibraries => Inner.bUseCheckedPhysXLibraries;
		public bool bCheckLicenseViolations => Inner.bCheckLicenseViolations;
		public bool bBreakBuildOnLicenseViolation => Inner.bBreakBuildOnLicenseViolation;
		public bool? bUseFastPDBLinking => Inner.bUseFastPDBLinking;
		public bool bCreateMapFile => Inner.bCreateMapFile; // *.map and *.objpaths
		public bool bAllowRuntimeSymbolFiles => Inner.bAllowRuntimeSymbolFiles;
		public bool bDeployAfterCompile => Inner.bDeployAfterCompile;
		public bool bAllowRemotelyCompiledPCHs => Inner.bAllowRemotelyCompiledPCHs;
		public bool bCheckSystemHeadersForModification => Inner.bCheckSystemHeadersForModification;
		public bool bDisableLinking => Inner.bDisableLinking;
		public bool bFormalBuild => Inner.bFormalBuild;
		public bool bUseAdaptiveUnityBuild => Inner.bUseAdaptiveUnityBuild;
		public bool bFlushBuildDirOnRemoteMac => Inner.bFlushBuildDirOnRemoteMac;
		public bool bPrintToolChainTimingInfo => Inner.bPrintToolChainTimingInfo;
		public bool bParseTimingInfoForTracing => Inner.bParseTimingInfoForTracing;
		public bool bPublicSymbolsByDefault => Inner.bPublicSymbolsByDefault;
		public bool bLegacyPublicIncludePaths => Inner.bLegacyPublicIncludePaths;
		public string BundleVersion => Inner.BundleVersion;
		public string ToolChainName => Inner.ToolChainName;
		public CppStandardVersion CppStandard => Inner.CppStandard;
		internal bool bNoManifestChanges => Inner.bNoManifestChanges;
		public string BuildVersion => Inner.BuildVersion;
		public WarningLevel ShadowVariableWarningLevel => Inner.ShadowVariableWarningLevel;
		public WarningLevel UnsafeTypeCastWarningLevel => Inner.UnsafeTypeCastWarningLevel;
		public TargetLinkType LinkType => Inner.LinkType;

		public IReadOnlyList<string> GlobalDefinitions => Inner.GlobalDefinitions.AsReadOnly();

		public IReadOnlyList<string> ProjectDefinitions => Inner.ProjectDefinitions.AsReadOnly();

		public string LaunchModuleName => Inner.LaunchModuleName;

		public string ExportPublicHeader => Inner.ExportPublicHeader;

		public IReadOnlyList<string> ExtraModuleNames => Inner.ExtraModuleNames.AsReadOnly();

		public IReadOnlyList<FileReference> ManifestFileNames => Inner.OutputManifestFileNames.AsReadOnly();

		public IReadOnlyList<FileReference> DependencyListFileNames => Inner.PrecompileDependencyFiles.AsReadOnly();

		public TargetBuildEnvironment BuildEnvironment => Inner.BuildEnvironment;

		public bool bOverrideBuildEnvironment => Inner.bOverrideBuildEnvironment;

		public IReadOnlyList<string> PreBuildSteps => Inner.PreBuildSteps;

		public IReadOnlyList<string> PostBuildSteps => Inner.PostBuildSteps;

		public IReadOnlyList<string> AdditionalBuildProducts => Inner.AdditionalBuildProducts;

		public string AdditionalCompilerArguments => Inner.AdditionalCompilerArguments;

		public string AdditionalLinkerArguments => Inner.AdditionalLinkerArguments;

		public string GeneratedProjectName => Inner.GeneratedProjectName;

		public ReadOnlyAndroidTargetRules AndroidPlatform { get; private set; }
		public ReadOnlyLuminTargetRules LuminPlatform { get; private set; }
		public ReadOnlyLinuxTargetRules LinuxPlatform { get; private set; }
		public ReadOnlyIOSTargetRules IOSPlatform { get; private set; }
		public ReadOnlyMacTargetRules MacPlatform { get; private set; }
		public ReadOnlyPS4TargetRules PS4Platform { get; private set; }
		public ReadOnlySwitchTargetRules SwitchPlatform { get; private set; }
		public ReadOnlyWindowsTargetRules WindowsPlatform { get; private set; }
		public ReadOnlyHoloLensTargetRules HoloLensPlatform { get; private set; }
		public ReadOnlyXboxOneTargetRules XboxOnePlatform { get; private set; }

		public bool bShouldCompileAsDLL { get { return Inner.bShouldCompileAsDLL; } 	}
		public bool bGenerateProjectFiles { get { return Inner.bGenerateProjectFiles; } }
		public bool bIsEngineInstalled { get { return Inner.bIsEngineInstalled; } }
		public IReadOnlyList<string> DisableUnityBuildForModules { get { return Inner.DisableUnityBuildForModules; } }
		public IReadOnlyList<string> EnableOptimizeCodeForModules { get { return Inner.EnableOptimizeCodeForModules; } }
		public IReadOnlyList<string> DisableOptimizeCodeForModules => Inner.DisableOptimizeCodeForModules;

#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning restore C1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif
		#endregion Read-only accessor properties
	} // End ReadOnlyTargetRules
#pragma warning restore IDE1006 // Naming Styles
}
