using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// Compiler configuration. 
	// This controls whether to use define debug macros and other compiler settings. 
	// Note that optimization level should be based on the bOptimizeCode variable rather thanthis setting, 
	// so it can be modified on a per-module basis without introducing an incompatibility between object files or PCHs.
	enum CppConfiguration
	{
		Debug,
		Development,
		Shipping
	}

	// Specifies which language standard to use. 
	// This enum should be kept in order, so that toolchains can check whether the requested setting is >= values that they support.
	public enum CppStandardVersion
	{
		Default, // Use the default standard version
		Cpp14,
		Cpp17,
		Latest,
	}

	// The optimization level that may be compilation targets for C# files.
	enum CSharpTargetConfiguration
	{
		Debug,
		Development,
	}

	// The possible interactions between a precompiled header and a C++ file being compiled.
	enum PCHAction
	{
		None,
		Include,
		Create
	}

	// Encapsulates the compilation output of compiling a set of C++ files.
	class CPPOutput
	{
		public List<FileItem> ObjectFiles               = new List<FileItem>(); // *.obj
		public List<FileItem> DebugDataFiles            = new List<FileItem>();
		public List<FileItem> ISPCGeneratedHeaderFiles  = new List<FileItem>(); // *.isph
		public FileItem       PCHFile                   = null;
	}

	// Encapsulates the environment that a C++ file is compiled in.
	// All memeber variables are determined by TargetRules.
	class CppCompileEnvironment
	{
		public List<FileItem> ForceIncludeFiles = new List<FileItem>();

		// List of files that need to be up to date before compile can proceed
		public List<FileItem> AdditionalPrerequisites = new List<FileItem>();

		public List<string> Definitions = new List<string>();

		// A list of additional frameworks whose include paths are needed.
		public List<BuildFramework> AdditionalFrameworks = new List<BuildFramework>();

		// Templates for shared precompiled headers
		public readonly List<PCHTemplate> SharedPCHs;

		// Ordered list of include paths for the module
		public HashSet<DirectoryReference> UserIncludePaths;

		// The include paths where changes to contained files won't cause dependent C++ source files to
		// be recompiled, unless BuildConfiguration.bCheckSystemHeadersForModification==true.
		public HashSet<DirectoryReference> SystemIncludePaths;

		// The platform to be compiled/linked for.
		public readonly BuildTargetPlatform Platform;

		// The configuration to be compiled/linked for.
		public readonly CppConfiguration Configuration;

		// Whether the compilation should create, use, or do nothing with the precompiled header.
		public PCHAction PCHAction = PCHAction.None;

		// Whether to warn about the use of shadow variables
		public WarningLevel ShadowVariableWarningLevel = WarningLevel.Warning;

		// How to treat unsafe implicit type cast warnings (e.g., double->float or int64->int32)
		public WarningLevel UnsafeTypeCastWarningLevel = WarningLevel.Off;

		// Which C++ standard to support. May not be compatible with all platforms.
		public CppStandardVersion CppStandard = CppStandardVersion.Default;

		// Additional arguments to pass to the compiler.
		public string AdditionalArguments = "";

		// The architecture that is being compiled/linked 
		//(empty string by default)
		public readonly string Architecture;

		// Cache of source file metadata
		public readonly SourceFileMetadataCache MetadataCache;

		// The name of the header file which is precompiled.
		public FileReference PCHIncludeFilename = null; // SharedPCH.$(OtherModuleName).$(AdditionalSuffixes).h

		// Whether artifacts from this compile are shared with other targets.
		// If so, we should not apply any target-wide modifications to the compile environment.
		public bool bUseSharedBuildEnvironment;

		public bool bUseRTTI     = false; // Use run time type information
		public bool bUseInlining = false; // Enable inlining.
		public bool bCompileISPC = false; // Whether to compile ISPC files.

		// Note that by enabling this you are changing the minspec for the PC platform, and the resultant executable will crash on machines without AVX support.
		public bool bUseAVX = false;
		public bool bEnableBufferSecurityChecks = true; // This should usually be enabled as it prevents severe security risks.

		// If unity builds are enabled this can be used to override if this specific module will build using Unity.
		// This is set using the per module configurations in BuildConfiguration.
		public bool bUseUnity = false;
		 
		// The number of source files in this module before unity build will be activated for that module.  
		// If set to anything besides -1, will override the default setting which is controlled by MinGameModuleSourceFilesForUnityBuild
		public int MinSourceFilesForUnityBuildOverride = 0;

		// The minimum number of files that must use a pre-compiled header before it will be created and used.
		public int MinFilesUsingPrecompiledHeaderOverride = 0;

		public bool bBuildLocallyWithSNDBS = false; // Module uses a #import so must be built locally when compiling with SN-DBS
		public bool bEnableExceptions = false;
		public bool bEnableObjectiveCExceptions = false;
		public bool bEnableUndefinedIdentifierWarnings = true; // Whether to warn about the use of undefined identifiers in #if expressions
		public bool bUndefinedIdentifierWarningsAsErrors = false;

		// True if compiler optimizations should be enabled.
		// This setting is distinct from the configuration (see CPPTargetConfiguration).
		public bool bOptimizeCode = false;

		// Whether to optimize for minimal code size
		public bool bOptimizeForSize = false;

		// True if debug info should be created.
		public bool bCreateDebugInfo = true;

		// True if we're compiling .cpp files that will go into a library (.lib file)
		public bool bIsBuildingLibrary = false;

		// True if we're compiling a DLL
		public bool bIsBuildingDLL = false;

		// Whether we should compile using the statically-linked CRT. This is not widely supported for the whole engine, but is required for programs that need to run without dependencies.
		public bool bUseStaticCRT = false;

		// Whether to use the debug CRT in debug configurations
		public bool bUseDebugCRT = false;

		// Whether to omit frame pointers or not.
		// Disabling is useful for e.g. memory profiling on the PC
		public bool bOmitFramePointers = true;

		// Whether we should compile with support for OS X 10.9 Mavericks. Used for some tools that we need to be compatible with this version of OS X.
		public bool bEnableOSX109Support = false;

		// Whether PDB files should be used for Visual C++ builds.
		public bool bUsePDBFiles = false;

		// Whether to just preprocess source files
		public bool bPreprocessOnly = false;

		// Whether to support edit and continue.  Only works on Microsoft compilers in 32-bit compiles.
		public bool bSupportEditAndContinue;

		// Whether to use incremental linking or not.
		public bool bUseIncrementalLinking;

		// Whether to allow the use of LTCG (link time code generation) 
		public bool bAllowLTCG;

        // Whether to enable Profile Guided Optimization (PGO) instrumentation in this build.
        public bool bPGOProfile;
        
        // Whether to optimize this build with Profile Guided Optimization (PGO).
        public bool bPGOOptimize;

        // Platform specific directory where PGO profiling data is stored.
        public string PGODirectory;

        // Platform specific filename where PGO profiling data is saved.
        public string PGOFilenamePrefix;

		// Whether to log detailed timing info from the compiler
		public bool bPrintTimingInfo;

		// Whether to output a dependencies file along with the output build products
		public bool bGenerateDependenciesFile = true;

		// When enabled, allows XGE to compile pre-compiled header files on remote machines.  Otherwise, PCHs are always generated locally.
		public bool bAllowRemotelyCompiledPCHs = false;

		// Whether headers in system paths should be checked for modification when determining outdated actions.
		public bool bCheckSystemHeadersForModification;

		// The file containing the precompiled header data.
		public FileItem PrecompiledHeaderFile = null;

		// Whether or not UHT is being built
		public bool bHackHeaderGenerator;

		// Whether to hide symbols by default
		public bool bHideSymbolsByDefault = true;

        public CppCompileEnvironment(BuildTargetPlatform Platform, CppConfiguration Configuration, string Architecture, SourceFileMetadataCache MetadataCache)
		{
			this.Platform = Platform;
			this.Configuration = Configuration;
			this.Architecture = Architecture;
			this.MetadataCache = MetadataCache;
			this.SharedPCHs = new List<PCHTemplate>();
			this.UserIncludePaths = new HashSet<DirectoryReference>();
			this.SystemIncludePaths = new HashSet<DirectoryReference>();
		}
		
		public CppCompileEnvironment(CppCompileEnvironment Other)
		{
			Platform = Other.Platform;
			Configuration = Other.Configuration;
			Architecture = Other.Architecture;
			MetadataCache = Other.MetadataCache;
			SharedPCHs = Other.SharedPCHs;
			PCHIncludeFilename = Other.PCHIncludeFilename;
			PCHAction = Other.PCHAction;
			bUseSharedBuildEnvironment = Other.bUseSharedBuildEnvironment;
			bUseRTTI = Other.bUseRTTI;
			bUseInlining = Other.bUseInlining;
			bCompileISPC = Other.bCompileISPC;
			bUseAVX = Other.bUseAVX;
			bUseUnity = Other.bUseUnity;
			MinSourceFilesForUnityBuildOverride = Other.MinSourceFilesForUnityBuildOverride;
			MinFilesUsingPrecompiledHeaderOverride = Other.MinFilesUsingPrecompiledHeaderOverride;
			bBuildLocallyWithSNDBS = Other.bBuildLocallyWithSNDBS;
			bEnableExceptions = Other.bEnableExceptions;
			bEnableObjectiveCExceptions = Other.bEnableObjectiveCExceptions;
			ShadowVariableWarningLevel = Other.ShadowVariableWarningLevel;
			UnsafeTypeCastWarningLevel = Other.UnsafeTypeCastWarningLevel;
			bUndefinedIdentifierWarningsAsErrors = Other.bUndefinedIdentifierWarningsAsErrors;
			bEnableUndefinedIdentifierWarnings = Other.bEnableUndefinedIdentifierWarnings;
			bOptimizeCode = Other.bOptimizeCode;
			bOptimizeForSize = Other.bOptimizeForSize;
			bCreateDebugInfo = Other.bCreateDebugInfo;
			bIsBuildingLibrary = Other.bIsBuildingLibrary;
			bIsBuildingDLL = Other.bIsBuildingDLL;
			bUseStaticCRT = Other.bUseStaticCRT;
			bUseDebugCRT = Other.bUseDebugCRT;
			bOmitFramePointers = Other.bOmitFramePointers;
			bEnableOSX109Support = Other.bEnableOSX109Support;
			bUsePDBFiles = Other.bUsePDBFiles;
			bPreprocessOnly = Other.bPreprocessOnly;
			bSupportEditAndContinue = Other.bSupportEditAndContinue;
			bUseIncrementalLinking = Other.bUseIncrementalLinking;
			bAllowLTCG = Other.bAllowLTCG;
			bPGOOptimize = Other.bPGOOptimize;
			bPGOProfile = Other.bPGOProfile;
			PGOFilenamePrefix = Other.PGOFilenamePrefix;
			PGODirectory = Other.PGODirectory;
			bPrintTimingInfo = Other.bPrintTimingInfo;
			bGenerateDependenciesFile = Other.bGenerateDependenciesFile;
			bAllowRemotelyCompiledPCHs = Other.bAllowRemotelyCompiledPCHs;
			UserIncludePaths = new HashSet<DirectoryReference>(Other.UserIncludePaths);
			SystemIncludePaths = new HashSet<DirectoryReference>(Other.SystemIncludePaths);
			bCheckSystemHeadersForModification = Other.bCheckSystemHeadersForModification;
			ForceIncludeFiles.AddRange(Other.ForceIncludeFiles);
			AdditionalPrerequisites.AddRange(Other.AdditionalPrerequisites);
			Definitions.AddRange(Other.Definitions);
			AdditionalArguments = Other.AdditionalArguments;
			AdditionalFrameworks.AddRange(Other.AdditionalFrameworks);
			PrecompiledHeaderFile = Other.PrecompiledHeaderFile;
			bHackHeaderGenerator = Other.bHackHeaderGenerator;
			bHideSymbolsByDefault = Other.bHideSymbolsByDefault;
			CppStandard = Other.CppStandard;
		}
	}
}
