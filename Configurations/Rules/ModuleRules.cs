using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

// UEBuildModule
//     ├─────────── ModuleRules   ┬─  PluginInfo          Plugin
//     │                          ├─  ModuleRulesContext  Context
//     │                          ├─  ReadOnlyTargetRules Target
//     │                          ├─  bool                bTreatAsEngineModule, bUseRTTI, bUseAVX, bEnforceIWYU, bAddDefaultIncludePaths, bPrecompile, bUsePrecompiled
//     └─────────── UEBuildBinary ┬── bool             bAllowExports, bUsePrecompiled
//                                └── UEBuildModuleCPP PrimaryModule

namespace BuildTool
{
#pragma warning disable IDE1006 // Naming Styles
	// Controls how a particular warning is treated
	public enum WarningLevel
	{
		Off,     // Do not display diagnostics
		Warning, // Output warnings normally
		Error,   // Output warnings as errors
	}

	// ModuleRules is a data structure that 
	// contains the rules for defining a module
	public class ModuleRules
	{
		#region ENUMTYPE
		public enum ModuleType
		{
			CPlusPlus,
			External,  // External (third-party)
		}

		public enum CodeOptimization
		{
			Never,                // Code should never be optimized if possible.
			InNonDebugBuilds,     // Code should only be optimized in non-debug builds (not in Debug).
			InShippingBuildsOnly, // Code should only be optimized in shipping builds (not in Debug, DebugGame, Development)
			Always,               // Code should always be optimized if possible.
			Default,              // Default: 'InNonDebugBuilds' for game modules, 'Always' otherwise.
		}

		// What type of PCH to use for this module.
		public enum PCHUsageMode
		{
			Default,       // Default: Engine modules use shared PCHs, game modules do not
			NoPCHs,        // Never use any PCHs.
			NoSharedPCHs,  // Always generate a unique PCH for this module if appropriate
			UseSharedPCHs, // Shared PCHs are OK!

			// Shared PCHs may be used if an explicit private PCH is not set through PrivatePCHHeaderFile. 
			// In either case, none of the source files manually include a module PCH, and should include a matching header instead.
			UseExplicitOrSharedPCHs,
		}

		// Which type of targets this module should be precompiled for
		public enum PrecompileTargetsType
		{
			None, // Never precompile this module.

			// Inferred from the module's directory.
			// Engine modules under Engine/Source/Runtime will be compiled for games, 
			// those under Engine/Source/Editor will be compiled for the editor, etc...
			Default,
			Game,
			Editor,
			Any,
		}

		// Control visibility of symbols in this module for special cases
		public enum SymbolVisibility
		{
			Default,        // Standard visibility rules
			VisibileForDll, // Make sure symbols in this module are visible in Dll builds
		}

		#endregion ENUMTYPE
		// Information about a file which is required by the target at runtime, 
		// and must be moved around with it.
		[Serializable]
		public class RuntimeDependency
		{
			// The file that should be staged. Should use $(EngineDir) and $(ProjectDir) variables as a root, 
			// so that the target can be relocated to different machines.
			public string Path;
			public string SourcePath; // It will be copied to Path at build time, ready for staging.

			public StagedFileType StagedType; // How to stage this file.
			public RuntimeDependency(string InPath, StagedFileType InStagedType = StagedFileType.RawFile)
			{
				Path       = InPath;
				StagedType = InStagedType;
			}

			public RuntimeDependency(string InPathRuntimeDepenedency, string InSourcePathInWorkingTree, StagedFileType InStagedType = StagedFileType.RawFile)
			{
				Path       = InPathRuntimeDepenedency;
				SourcePath = InSourcePathInWorkingTree;
				StagedType = InStagedType;
			}
		}

		// List of runtime dependencies, with convenience methods for adding new items
		[Serializable]
		public class RuntimeDependencyList
		{
			internal List<RuntimeDependency> Inner = new List<RuntimeDependency>();

			public RuntimeDependencyList()
			{
			}

			public void Add(string InPath, StagedFileType InStagedType = StagedFileType.RawFile)
			{
				// May include wildcards.
				Inner.Add(new RuntimeDependency(InPath, InStagedType));
			}

			public void Add(string InPath, string InSourcePathInWorkingTree, StagedFileType InStagedType = StagedFileType.RawFile)
			{
				// Pahts May include wildcards.
				Inner.Add(new RuntimeDependency(InPath, InSourcePathInWorkingTree, InStagedType));
			}
		}

		// List of runtime dependencies with convenience methods for adding new items
		[Serializable]
		public sealed class ReceiptPropertyList
		{
			internal List<ReceiptProperty> Inner = new List<ReceiptProperty>();

			public ReceiptPropertyList()
			{
			}

			public void Add(string InPropertyName, string InPropertyValue)
			{
				Inner.Add(new ReceiptProperty(InPropertyName, InPropertyValue));
			}
		}

		// Stores information about a framework on IOS or MacOS
		public sealed class Framework
		{
			internal string ZipPath;       // For non-system frameworks, specifies the path to a zip file that contains it.
			internal string FrameworkName;
			internal string CopyBundledAssets = null;

			public Framework(string InFrameworkName, string ContainingFrameworkZipPath = null, string CopyBundledAssets = null)
			{
				this.FrameworkName     = InFrameworkName;
				this.ZipPath           = ContainingFrameworkZipPath;
				this.CopyBundledAssets = CopyBundledAssets;
			}
		}

		public class BundleResource
		{
			public string ResourcePath         = null;
			public string BundleContentsSubdir = null;
			public bool   bShouldLog           = true;

			public BundleResource(string ResourcePath, string BundleContentsSubdir = "Resources", bool bShouldLog = true)
			{
				this.ResourcePath         = ResourcePath;
				this.BundleContentsSubdir = BundleContentsSubdir;
				this.bShouldLog           = bShouldLog;
			}
		}

		// Information about a Windows type library (TLB/OLB file) which requires a generated header.
		public class TypeLibrary
		{
			public string FileName;   // Name of the type library
			public string Attributes; // Additional attributes for the #import directive
			public string Header;     // Name of the output header

			// Constructor
			// <param name="FileName">Name of the type library. Follows the same conventions as the filename parameter in the MSVC #import directive.</param>
			// <param name="Attributes">Additional attributes for the import directive</param>
			// <param name="Header">Name of the output header</param>
			public TypeLibrary(string FileName, string Attributes, string Header)
			{
				this.FileName   = FileName;
				this.Attributes = Attributes;
				this.Header     = Header;
			}
		}

		public string Name { get; internal set; }

		internal FileReference      File;
		internal DirectoryReference Directory;
		internal PluginInfo         Plugin;
		internal ModuleRulesContext Context; // The rules context for this instance

		// Additional directories that contribute to this module (likely in BuildTool.EnginePlatformExtensionsDirectory). 
		// The dictionary tracks module subclasses
		internal Dictionary<Type, DirectoryReference> DirectoriesForModuleSubClasses;

		// Rules for the target that this module belongs to
		public readonly ReadOnlyTargetRules Target;

		public ModuleType Type = ModuleType.CPlusPlus;

		// Subfolder of Binaries/PLATFORM folder to put this module in when building DLLs.
		// This should only be used by modules that are found via searching like the TargetPlatform or ShaderFormat modules.
		// If FindModules is not used to track them down, the modules will not be found.
		public string BinariesSubFolder = "";

		private CodeOptimization? OptimizeCodeOverride;

		// When this module's code should be optimized.
		public CodeOptimization OptimizeCode
		{
			get
			{
				if (OptimizeCodeOverride.HasValue)
				{
					return OptimizeCodeOverride.Value;
				}

				bool? ShouldOptimizeCode = null;
				if (Target.EnableOptimizeCodeForModules?.Contains(Name) ?? false)
				{
					ShouldOptimizeCode = true;
				}

				if (Target.DisableOptimizeCodeForModules?.Contains(Name) ?? false)
				{
					ShouldOptimizeCode = false;
				}

				return !ShouldOptimizeCode.HasValue
					? CodeOptimization.Default
					: ShouldOptimizeCode.Value ? CodeOptimization.Always : CodeOptimization.Never;
			}
			set { OptimizeCodeOverride = value; }
		}

		// Explicit private PCH for this module. Implies that this module will not use a shared PCH.
		// *.Build.cs에서 작성
		public string PrivatePCHHeaderFile;

		// Header file name for a shared PCH provided by this module.  
		// Must be a valid relative path to a public C++ header file.
		// This should only be set for header files that are included by a significant number of other C++ modules.
		// *.Build.cs에서 작성
		public string SharedPCHHeaderFile;
		
		// Specifies an alternate name for intermediate directories and files for intermediates of this module. 
		// Useful when hitting path length limitations.
		public string ShortName = null;

		// Precompiled header usage for this module
		public PCHUsageMode PCHUsage
		{
			get
			{
				if (PCHUsagePrivate.HasValue)
				{
					// Use the override
					return PCHUsagePrivate.Value;
				}
				else if(Target.bIWYU || DefaultBuildSettings >= BuildSettingsVersion.V2)
				{
					// Use shared or explicit PCHs, and enable IWYU
					return PCHUsageMode.UseExplicitOrSharedPCHs;
				}
				else if(Plugin != null)
				{
					// Older plugins use shared PCHs by default, since they aren't typically large enough to warrant their own PCH.
					return PCHUsageMode.UseSharedPCHs;
				}
				else
				{
					// Older game modules do not enable shared PCHs by default, because games usually have a large precompiled header of their own.
					return PCHUsageMode.NoSharedPCHs;
				}
			}
			set { PCHUsagePrivate = value; }
		}
		private PCHUsageMode? PCHUsagePrivate;

		// Whether this module should be treated as an engine module 
		// (eg. using engine definitions, PCHs, compiled with optimizations enabled in DebugGame configurations, etc...).
		// Initialized to a default based on the rules assembly it was created from.
		public bool bTreatAsEngineModule;

		// Which engine version's build settings to use by default.
		public BuildSettingsVersion DefaultBuildSettings
		{
			get { return DefaultBuildSettingsPrivate ?? Target.DefaultBuildSettings; }
			set { DefaultBuildSettingsPrivate = value; }
		}
		private BuildSettingsVersion? DefaultBuildSettingsPrivate;

		public bool bUseRTTI = false; // Use run time type information

		// Direct the compiler to generate AVX instructions wherever SSE or AVX intrinsics are used, on the platforms that support it.
		// Note that by enabling this you are changing the minspec for the PC platform, 
		// and the resultant executable will crash on machines without AVX support.
		public bool bUseAVX = false;

		public bool bEnableBufferSecurityChecks = true; // This should usually be enabled as it prevents severe security risks.
		public bool bEnableExceptions = false; // Enable exception handling
		public bool bEnableObjCExceptions = false; // Enable objective C exception handling

		// How to treat shadow variable warnings
		public WarningLevel ShadowVariableWarningLevel
		{
			get { return ShadowVariableWarningLevelPrivate ?? (DefaultBuildSettings < BuildSettingsVersion.V2 ? Target.ShadowVariableWarningLevel : WarningLevel.Error); }
			set { ShadowVariableWarningLevelPrivate = value; }
		}
		private WarningLevel? ShadowVariableWarningLevelPrivate;

		// How to treat unsafe implicit type cast warnings (e.g., double->float or int64->int32)
		// *.Build.cs에서도 작성
		public WarningLevel UnsafeTypeCastWarningLevel
		{
			get { return UnsafeTypeCastWarningLevelPrivate ?? Target.UnsafeTypeCastWarningLevel; }
			set { UnsafeTypeCastWarningLevelPrivate = value; }
		}
		private WarningLevel? UnsafeTypeCastWarningLevelPrivate;

		// Enable warnings for using undefined identifiers in #if expressions
		public bool bEnableUndefinedIdentifierWarnings = true;

		private bool? bUseUnityOverride;

		// If unity builds are enabled this can be used to override if this specific module will build using Unity.
		// This is set using the per module configurations in BuildConfiguration.
		public bool bUseUnity
		{
			set { bUseUnityOverride = value; }
			get
			{
				bool UseUnity = true;
				if (Target.DisableUnityBuildForModules?.Contains(Name) ?? false)
				{
					UseUnity = false;
				}

				return bUseUnityOverride ?? UseUnity;
			}
		}

		// The number of source files in this module before unity build will be activated for that module. 
		// If set to anything besides -1, will override the default setting which is controlled by MinGameModuleSourceFilesForUnityBuild
		public int MinSourceFilesForUnityBuildOverride = 0;

		// Overrides BuildConfiguration.MinFilesUsingPrecompiledHeader if non-zero.
		public int MinFilesUsingPrecompiledHeaderOverride = 0;

		// Module uses a #import so must be built locally when compiling with SN-DBS
		public bool bBuildLocallyWithSNDBS = false;

		// Redistribution override flag for this module.
		public bool? IsRedistributableOverride = null;

		// Whether the output from this module can be publicly distributed, even if it has code/
		// dependencies on modules that are not (i.e. CarefullyRedist, NotForLicensees, NoRedist).
		// This should be used when you plan to release binaries but not source.
		public bool bLegalToDistributeObjectCode = false;

		// List of folders which are whitelisted to be referenced when compiling this binary, without propagating restricted folder names
		// *.Build.cs에서도 작성
		public List<string> WhitelistRestrictedFolders = new List<string>();

		// Set of aliased restricted folder references
		public Dictionary<string, string> AliasRestrictedFolders = new Dictionary<string, string>();

		// Enforce "include what you use" rules when PCHUsage is set to ExplicitOrSharedPCH; 
		// warns when monolithic headers (Engine.h, EditorEd.h, etc...) 
		// are used, and checks that source files include their matching header first.
		public bool bEnforceIWYU = true;

		// Whether to add all the default include paths to the module 
		// (eg. the Source/Classes folder, subfolders under Source/Public).
		public bool bAddDefaultIncludePaths = true;

		// Whether to ignore dangling (i.e. unresolved external) symbols in modules
		public bool bIgnoreUnresolvedSymbols = false;

		// Whether this module should be precompiled. 
		// Defaults to the bPrecompile flag from the target. Clear this flag to prevent a module being precompiled.
		public bool bPrecompile;

		// Whether this module should use precompiled data.
		// Always true for modules created from installed assemblies.
		public bool bUsePrecompiled;

		// Whether this module can use PLATFORM_XXXX style defines, where XXXX is a confidential platform name. 
		// This is used to ensure engine or other shared code does not reveal confidential information inside an #if PLATFORM_XXXX block. 
		// Licensee game code may want to allow for them, however.
		// Note: this is future looking, and previous confidential platforms (like PS4) are unlikely to be restricted
		public bool bAllowConfidentialPlatformDefines = false;

		// List of modules names (no path needed) with header files that our module's public headers needs access to, 
		// but we don't need to "import" or link against.
		// *.Build.cs에서도 작성
		public List<string> PublicIncludePathModuleNames = new List<string>();

		// List of modules name (no path needed) with header files that our module's private code files needs access to,
		// but we don't need to "import" or link against.
		// *.Build.cs에서도 작성
		public List<string> PrivateIncludePathModuleNames = new List<string>();

		// List of public dependency module names (no path needed) (automatically does the private/public include).
		// These are modules that are required by our public source files.
		// *.Build.cs에서도 작성
		public List<string> PublicDependencyModuleNames = new List<string>();

		// These are modules that our private code depends on
		// but nothing in our public include files depend on.
		// *.Build.cs에서도 작성
		public List<string> PrivateDependencyModuleNames = new List<string>();

		// (This setting is currently not need as we discover all files from the 'Public' folder)
		// List of all paths to include files that are exposed to other modules
		// *.Build.cs에서 작성
		public List<string> PublicIncludePaths = new List<string>();
		// List of all paths to this module's internal include files
		// Not exposed to other modules (at least one include to the 'Private' path, more if we want to avoid relative paths)
		// *.Build.cs에서도 작성
		public List<string> PrivateIncludePaths = new List<string>();

		// List of search paths for libraries at runtime (eg. .so[dynmaicLibrary in Assembly code] files)
		public List<string> PublicRuntimeLibraryPaths = new List<string>();
		public List<string> PrivateRuntimeLibraryPaths = new List<string>();

		// List of delay load DLLs - typically used for External (third party) modules
		// *.Build.cs에서도 작성
		public List<string> PublicDelayLoadDLLs = new List<string>();

		// Private compiler definitions for this module
		// *.Build.cs에서도 작성
		public List<string> PrivateDefinitions = new List<string>();

		// Only for legacy reason, should not be used in new code.
		// List of module dependencies that should be treated as circular references.
		// This modules must have already been added to either the public or private dependent module list.
		// *.Build.cs에서도 작성
		public List<string> CircularlyReferencedDependentModules = new List<string>();

		// List of system/library include paths - typically used for External (third party) modules. 
		// These are public stable header file directories that are not checked when resolving header dependencies.
		public List<string> PublicSystemIncludePaths = new List<string>();

		// List of system library paths (directory of .lib files)
		// - for External (third party) modules please use the PublicAdditionalLibaries instead
		public List<string> PublicSystemLibraryPaths = new List<string>();

		// List of additional libraries (names of the .lib files including extension)
		// - typically used for External (third party) modules
		public List<string> PublicAdditionalLibraries = new List<string>();

		// List of system libraries to use - these are typically referenced via name and then found via the system paths.
		// If you need to reference a .lib file use the PublicAdditionalLibraries instead
		// *.Build.cs에서도 작성
		public List<string> PublicSystemLibraries = new List<string>();

		// List of XCode frameworks (iOS and MacOS)
		// *.Build.cs에서도 작성
		public List<string> PublicFrameworks = new List<string>();

		// List of weak frameworks (for OS version transitions)
		public List<string> PublicWeakFrameworks = new List<string>();

		// List of addition frameworks - typically used for External (third party) modules on Mac and iOS
		public List<Framework> PublicAdditionalFrameworks = new List<Framework>();

		// List of addition resources that should be copied to the app bundle for Mac or iOS
		public List<BundleResource> AdditionalBundleResources = new List<BundleResource>();

		// List of type libraries that we need to generate headers for (Windows only)
		public List<TypeLibrary> TypeLibraries = new List<TypeLibrary>();

		// Public compiler definitions for this module
		// *.Build.cs에서도 작성
		public List<string> PublicDefinitions = new List<string>();

		public void AppendStringToPublicDefinition(string Definition, string Text)
		{
			string WithEquals = Definition + "=";
			for (int Index=0; Index < PublicDefinitions.Count; ++Index)
			{
				if (PublicDefinitions[Index].StartsWith(WithEquals))
				{
					PublicDefinitions[Index] = PublicDefinitions[Index] + Text;
					return;
				}
			}

			// if we get here, we need to make a new entry
			PublicDefinitions.Add(Definition + "=" + Text);
		}

		// Addition modules this module may require at run-time 
		// *.Build.cs에서도 작성
		public List<string> DynamicallyLoadedModuleNames = new List<string>();

		// List of files which this module depends on at runtime. These files will be staged along with the target.
		// *.Build.cs에서도 작성
		public RuntimeDependencyList RuntimeDependencies = new RuntimeDependencyList();

		// List of additional properties to be added to the build receipt
		public ReceiptPropertyList AdditionalPropertiesForReceipt = new ReceiptPropertyList();

		// Which targets this module should be precompiled for
		public PrecompileTargetsType PrecompileForTargets = PrecompileTargetsType.Default;

		// External files which invalidate the makefile if modified. Relative paths are resolved relative to the .build.cs file.
		public List<string> ExternalDependencies = new List<string>();

		// Subclass rules files which invalidate the makefile if modified.
		public List<string> SubclassRules;

		// Whether this module requires the IMPLEMENT_MODULE macro to be implemented. 
		// Most modules require this, since we use the IMPLEMENT_MODULE macro to do other global overloads 
		// (eg. operator new/delete forwarding to GMalloc).
		public bool? bRequiresImplementModule;

		// Whether this module qualifies included headers from other modules relative to the root of their 'Public' folder.
		// This reduces the number of search paths that have to be passed to the compiler,
		// improving performance and reducing the length of the compiler command line.
		public bool bLegacyPublicIncludePaths
		{
			set { bLegacyPublicIncludePathsPrivate = value; }
			get { return bLegacyPublicIncludePathsPrivate ?? ((DefaultBuildSettings < BuildSettingsVersion.V2) && Target.bLegacyPublicIncludePaths); }
		}
		private bool? bLegacyPublicIncludePathsPrivate;

		public CppStandardVersion CppStandard = CppStandardVersion.Default;

		// *.Build.cs에서도 작성
		public SymbolVisibility ModuleSymbolVisibility = ModuleRules.SymbolVisibility.Default;

		// The AutoSDK directory for the active host platform
		public string AutoSdkDirectory
		{
			get
			{
				return BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference AutoSdkDir) ? AutoSdkDir.FullName : null;
			}
		}

        // The current engine directory
        // public static string EngineDirectory => BuildTool.EngineDirectory.FullName;

        // Property for the directory containing this plugin. Useful for adding paths to third party dependencies.
        public string PluginDirectory
		{
			get
			{
				if(Plugin == null)
				{
					throw new BuildException("Module '{0}' does not belong to a plugin; PluginDirectory property is invalid.", Name);
				}
				else
				{
					return Plugin.RootDirectory.FullName;
				}
			}
		}

        // Property for the directory containing this module. Useful for adding paths to third party dependencies.
        public string ModuleDirectory => Directory.FullName;

		// For *.Build.cs
        public ModuleRules(ReadOnlyTargetRules Target) => this.Target = Target;

        // Add the given Engine ThirdParty modules as static private dependencies
        // Statically linked to this module, meaning they utilize exports from the other module
        // Private, meaning the include paths for the included modules will not be exposed when giving this modules include paths
        // NOTE: There is no AddThirdPartyPublicStaticDependencies function.

        // <param name="Target">The target this module belongs to</param>
        // <param name="ModuleNames">The names of the modules to add</param>
        public void AddEngineThirdPartyPrivateStaticDependencies(ReadOnlyTargetRules Target, params string[] ModuleNames)
		{
			if (!bUsePrecompiled || Target.LinkType == TargetLinkType.Monolithic)
			{
				PrivateDependencyModuleNames.AddRange(ModuleNames);
			}
		}

		// Add the given Engine ThirdParty modules as dynamic private dependencies
		// Dynamically linked to this module, meaning they do not utilize exports from the other module
		// Private, meaning the include paths for the included modules will not be exposed when giving this modules include paths
		// NOTE: There is no AddThirdPartyPublicDynamicDependencies function.
		
		// <param name="Target">Rules for the target being built</param>
		// <param name="ModuleNames">The names of the modules to add</param>
		public void AddEngineThirdPartyPrivateDynamicDependencies(ReadOnlyTargetRules Target, params string[] ModuleNames)
		{
			if (!bUsePrecompiled || Target.LinkType == TargetLinkType.Monolithic)
			{
				PrivateIncludePathModuleNames.AddRange(ModuleNames);
				DynamicallyLoadedModuleNames.AddRange(ModuleNames);
			}
		}

		// Setup this module for physics support (based on the settings in UEBuildConfiguration)
		public void EnableMeshEditorSupport(ReadOnlyTargetRules Target)
		{
			if (Target.bEnableMeshEditor == true)
			{
				PublicDefinitions.Add(Tag.CppContents.Def.EnableMeshEditor + Tag.Boolean.One); // "ENABLE_MESH_EDITOR=1"
			}
			else
			{
				PublicDefinitions.Add(Tag.CppContents.Def.EnableMeshEditor + Tag.Boolean.Zero); // "ENABLE_MESH_EDITOR=0"
			}
		}

		// Setup this module for physics support (based on the settings in UEBuildConfiguration)
		public void SetupModulePhysicsSupport(ReadOnlyTargetRules Target)
		{
			bool bUseNonPhysXInterface = Target.bUseChaos == true;

			PublicIncludePathModuleNames.Add(Tag.Module.Engine.PhysicsCore);
            PublicIncludePathModuleNames.AddRange
			(
                new string[] 
				{
                    Tag.Module.Engine.Chaos,
					Tag.Module.Engine.FieldSystemCore
                }
            );

			PublicDependencyModuleNames.Add(Tag.Module.Engine.PhysicsCore);
			PublicDependencyModuleNames.AddRange
			(
				new string[] 
				{
					Tag.Module.Engine.Chaos,
					Tag.Module.Engine.FieldSystemCore
                }
            );

            if (Target.bCompileChaos == true || bUseNonPhysXInterface)
            {
                PublicDefinitions.Add(Tag.CppContents.Def.IncludeChaos + Tag.Boolean.One); // "INCLUDE_CHAOS=1"
			}
            else
            {
                PublicDefinitions.Add(Tag.CppContents.Def.IncludeChaos + Tag.Boolean.Zero);
            }

            // definitions used outside of PhysX/APEX need to be set here, not in PhysX.Build.cs or APEX.Build.cs, 
            // since we need to make sure we always set it, even to 0 (because these are Private dependencies,
			// the defines inside their Build.cs files won't leak out)
            if (Target.bCompilePhysX == true)
			{
				PrivateDependencyModuleNames.Add(Tag.Module.ThirdParty.PhysX);
				PublicDefinitions.Add(Tag.CppContents.Def.WihtPhysX + Tag.Boolean.One);
			}
			else
			{
				PublicDefinitions.Add(Tag.CppContents.Def.WihtPhysX + Tag.Boolean.Zero);
			}

			if(!bUseNonPhysXInterface)
			{
				// Disable non-physx interfaces
				PublicDefinitions.Add(Tag.CppContents.Def.WithChaos + Tag.Boolean.Zero);
				PublicDefinitions.Add(Tag.CppContents.Def.WithChaosClothing + Tag.Boolean.Zero);

				// WITH_CHAOS_NEEDS_TO_BE_FIXED
				//
				// Anything wrapped in this define needs to be fixed
				// in one of the build targets. This define was added
				// to help identify complier failures between the
				// the three build targets( UseChaos, PhysX, WithChaos )
				// This defaults to off , and will be enabled for bUseChaos. 
				// This define should be removed when all the references 
				// have been fixed across the different builds.
				PublicDefinitions.Add(Tag.CppContents.Def.WithChaosNeedsToBeFixed + Tag.Boolean.Zero);

				if (Target.bCompilePhysX)
				{
					PublicDefinitions.Add(Tag.CppContents.Def.PhysicsInterfacePhysX + Tag.Boolean.One);
				}
				else
				{
					PublicDefinitions.Add(Tag.CppContents.Def.PhysicsInterfacePhysX + Tag.Boolean.Zero);
				}

				if (Target.bCompileAPEX == true)
				{
					if (!Target.bCompilePhysX)
					{
						throw new BuildException("APEX is enabled, without PhysX. This is not supported!");
					}

                    PrivateDependencyModuleNames.Add(Tag.Module.ThirdParty.Apex);
                    PublicDefinitions.Add(Tag.CppContents.Def.WithApex + Tag.Boolean.One);

                    // @MIXEDREALITY_CHANGE : BEGIN - Do not use Apex Cloth for HoloLens.  TODO: can we enable this in the future?
                    if (Target.Platform == BuildTargetPlatform.HoloLens)
                    {
                        PublicDefinitions.Add(Tag.CppContents.Def.WithApexClothing + Tag.Boolean.Zero); // "WITH_APEX_CLOTHING=0"
                        PublicDefinitions.Add(Tag.CppContents.Def.WithClothCollisionDetection + Tag.Boolean.Zero); // "WITH_CLOTH_COLLISION_DETECTION=0"
                    }
                    else
                    {
                        PublicDefinitions.Add(Tag.CppContents.Def.WithApexClothing + Tag.Boolean.One); // "WITH_APEX_CLOTHING=1"
                        PublicDefinitions.Add(Tag.CppContents.Def.WithClothCollisionDetection + Tag.Boolean.One); // "WITH_CLOTH_COLLISION_DETECTION=1"
                    }
                    // @MIXEDREALITY_CHANGE : END

                    // "WITH_PHYSX_COOKING=1"
                    PublicDefinitions.Add(Tag.CppContents.Def.WithPhysXCooking + Tag.Boolean.One);  // APEX currently relies on cooking even at runtime

                }
                else
				{
					PublicDefinitions.Add(Tag.CppContents.Def.WithApex + Tag.Boolean.Zero); // "WITH_APEX=0"
					PublicDefinitions.Add(Tag.CppContents.Def.WithApexClothing + Tag.Boolean.Zero); // "WITH_APEX_CLOTHING=0"
					PublicDefinitions.Add(Tag.CppContents.Def.WithClothCollisionDetection + Tag.Boolean.Zero); // "WITH_CLOTH_COLLISION_DETECTION=0"
					PublicDefinitions.Add(string.Format(Tag.CppContents.Def.WithPhysXCooking + (Target.bBuildEditor && Target.bCompilePhysX? Tag.Boolean.One : Tag.Boolean.Zero)));  // without APEX, we only need cooking in editor builds
				}

				if (Target.bCompileNvCloth == true)
				{
					if (!Target.bCompilePhysX)
					{
						throw new BuildException("NvCloth is enabled, without PhysX. This is not supported!");
					}

					PrivateDependencyModuleNames.Add(Tag.Module.ThirdParty.NvCloth);
					PublicDefinitions.Add(Tag.CppContents.Def.WithNVCloth + Tag.Boolean.One); // "WITH_NVCLOTH=1"

				}
				else
				{
					PublicDefinitions.Add(Tag.CppContents.Def.WithNVCloth + Tag.Boolean.Zero); // "WITH_NVCLOTH=0"
				}
			}
			else
			{
				// Disable apex/cloth/physx interface
				PublicDefinitions.Add(Tag.CppContents.Def.PhysicsInterfacePhysX + Tag.Boolean.Zero); // "PHYSICS_INTERFACE_PHYSX=0"
				PublicDefinitions.Add(Tag.CppContents.Def.WithApex + Tag.Boolean.Zero); // "WITH_APEX=0"
				PublicDefinitions.Add(Tag.CppContents.Def.WithApexClothing + Tag.Boolean.Zero); // "WITH_APEX_CLOTHING=0"
				PublicDefinitions.Add(string.Format(Tag.CppContents.Def.WithPhysXCooking + (Target.bBuildEditor && Target.bCompilePhysX ? Tag.Boolean.One : Tag.Boolean.Zero)));  // without APEX, we only need cooking in editor builds
				PublicDefinitions.Add(Tag.CppContents.Def.WithNVCloth + Tag.Boolean.Zero); // "WITH_NVCLOTH=0" 

				if (Target.bUseChaos)
				{
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaos + Tag.Boolean.One); // "WITH_CHAOS=1"
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaosNeedsToBeFixed + Tag.Boolean.One); // "WITH_CHAOS_NEEDS_TO_BE_FIXED=1"
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaosClothing + Tag.Boolean.One); // "WITH_CHAOS_CLOTHING=1"
					PublicDefinitions.Add(Tag.CppContents.Def.WithClothCollisionDetection + Tag.Boolean.One); // "WITH_CLOTH_COLLISION_DETECTION=1"

					PublicIncludePathModuleNames.AddRange
					(
						new string[] 
						{
						    Tag.Module.Engine.Chaos,
						}
					);

					PublicDependencyModuleNames.AddRange
					(
						new string[] 
						{
							Tag.Module.Engine.Chaos,
						}
					);
				}
				else
				{
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaos + Tag.Boolean.Zero); // "WITH_CHAOS=0"
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaosNeedsToBeFixed + Tag.Boolean.Zero); // "WITH_CHAOS_NEEDS_TO_BE_FIXED=0"
					PublicDefinitions.Add(Tag.CppContents.Def.WithChaosClothing + Tag.Boolean.Zero); // "WITH_CHAOS_CLOTHING=0"
					PublicDefinitions.Add(Tag.CppContents.Def.WithClothCollisionDetection + Tag.Boolean.Zero); // "WITH_CLOTH_COLLISION_DETECTION=0"
				}
			}

			// WITH_CUSTOM_SQ_STRUCTURE
			PublicDefinitions.Add(Tag.CppContents.Def.WithCustomSQStructure + (Target.bCustomSceneQueryStructure ? Tag.Boolean.One : Tag.Boolean.Zero));

			// Unused interface
			PublicDefinitions.Add(Tag.CppContents.Def.WithImmediatePhysX + Tag.Boolean.Zero); // "WITH_IMMEDIATE_PHYSX=0" 
		}

		// Determines if this module can be precompiled for the current target.
		internal bool IsValidForTarget(FileReference ModuleRulesFile)
		{
			if(Type == ModuleRules.ModuleType.CPlusPlus)
			{
				switch (PrecompileForTargets)
				{
					case ModuleRules.PrecompileTargetsType.None:
						return false;
					case ModuleRules.PrecompileTargetsType.Default:
						return (Target.Type == TargetType.Editor || 
							!BuildTool.GetAllEngineDirectories(Tag.Directory.SourceCode + Tag.Directory.EngineAndEditor).Any(Dir => ModuleRulesFile.IsUnderDirectory(Dir)) || 
							Plugin != null);
					case ModuleRules.PrecompileTargetsType.Game:
						return (Target.Type == TargetType.Client || 
							    Target.Type == TargetType.Server || 
								Target.Type == TargetType.Game);
					case ModuleRules.PrecompileTargetsType.Editor:
						return (Target.Type == TargetType.Editor);
					case ModuleRules.PrecompileTargetsType.Any:
						return true;
				}
			}
			return false;
		}

		// Returns the module directory for a given subclass of the module 
		// (platform extensions add subclasses of ModuleRules to add in platform-specific settings)
		// return Directory where the subclass's .Build.cs lives, or null if not found
		public DirectoryReference GetModuleDirectoryForSubClass(Type SubClassType)
		{
			if (DirectoriesForModuleSubClasses == null)
			{
				return null;
			}

			if (DirectoriesForModuleSubClasses.TryGetValue(SubClassType, out DirectoryReference Directory))
			{
				return Directory;
			}
			return null;
		}

		// Returns the directories for all subclasses of this module
		// <returns>List of directories, or null if none were added</returns>
		public DirectoryReference[] GetModuleDirectoriesForAllSubClasses()
		{
			return DirectoriesForModuleSubClasses?.Values.ToArray();
		}
	}
#pragma warning restore IDE1006 // Naming Styles
}
