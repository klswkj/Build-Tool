using BuildToolUtilities;

namespace BuildTool
{
	// Global settings for building. Should not contain any target-specific settings.
	class BuildConfiguration
	{
        // Whether to ignore import library files that are out of date when building targets. Set this to true to improve iteration time.
        // By default, we do not bother re-linking targets if only a dependent .lib has changed, as chances are that
        // the import library was not actually different unless a dependent header file of this target was also changed,
        // in which case the target would automatically be rebuilt.
        [XMLConfigFile]
		public bool bIgnoreOutdatedImportLibraries = true;

		// Use existing static libraries for all engine modules in this target.		
		[CommandLine(ReservedCommand = "-UsePrecompiled")]
		public bool bUsePrecompiled = false;

		// Whether debug info should be written to the console.
		[XMLConfigFile]
		public bool bPrintDebugInfo = false;

		// Whether to log detailed action stats. This forces local execution.
		[XMLConfigFile]
		public bool bLogDetailedActionStats = false;

		// Whether the hybrid executor will be used (a remote executor and local executor).
		[XMLConfigFile]
		public bool bAllowHybridExecutor = false;

		// Whether XGE may be used.
		[XMLConfigFile]
		[CommandLine(ReservedCommand = "-NoXGE", Value = "false")]
		public bool bAllowXGE = true;

#if FASTBUILD
		[XMLConfigFile]
		[CommandLine(ReservedCommand = "-NoFASTBuild", Value = "false")]
		public bool bAllowFASTBuild = true;
#endif
		// Whether SN-DBS may be used.
		[XMLConfigFile]
		public bool bAllowSNDBS = true;

		[XMLConfigFile]
		[CommandLine(ReservedCommand = "-NoUBTMakefiles", Value = "false")]
		public bool bUseBuildToolMakefiles = true;

		// Whether DMUCS/Distcc may be used.
		// Distcc requires some setup -- so by default, disable it so that we do not break local or remote building.
		[XMLConfigFile]
		public bool bAllowDistcc = false;

		// Whether to allow using parallel executor on Windows.		
		[XMLConfigFile]
		public bool bAllowParallelExecutor = true;

		// Number of actions that can be executed in parallel.
		// If 0 then code will pick a default based on the number of cores available.
		// Only applies to the ParallelExecutor		
		[XMLConfigFile]
		[CommandLine(ReservedCommand = "-MaxParallelActions")]
		public int MaxParallelActions = 0;

		// If true, force header regeneration. Intended for the build machine.
		[CommandLine(ReservedCommand = "-ForceHeaderGeneration")]
		[XMLConfigFile(Category = "UEBuildConfiguration")]
		public bool bForceHeaderGeneration = false;

		// If true, do not build UHT, assume it is already built.
		[CommandLine(ReservedCommand = "-NoBuildUHT")]
		[XMLConfigFile(Category = "UEBuildConfiguration")]
		public bool bDoNotBuildUHT = false;

		// If true, fail if any of the generated header files is out of date.
		[CommandLine(ReservedCommand = "-FailIfGeneratedCodeChanges")]
		[XMLConfigFile(Category = "UEBuildConfiguration")]
		public bool bFailIfGeneratedCodeChanges = false;

		// True if hot-reload from IDE is allowed.
		[CommandLine(ReservedCommand = "-NoHotReloadFromIDE", Value="false")]
		[XMLConfigFile(Category = "UEBuildConfiguration")]
		public bool bAllowHotReloadFromIDE = true;

		// If true, the Debug version of HeaderTool will be built and run instead of the Development version.
		[XMLConfigFile(Category = "UEBuildConfiguration")]
		public bool bForceDebugHeaderTool = false;

		// Whether to skip compiling rules assemblies and just assume they are valid		
		[CommandLine(ReservedCommand = "-SkipRulesCompile")]
		public bool bSkipRulesCompile = false;

		// Maximum recommended root path length.		
		[XMLConfigFile(Category = "WindowsPlatform")]
		public int MaxRootPathLength = 50;

		// Maximum length of a path relative to the root directory.
		// Used on Windows to ensure paths are portable between machines. Defaults to off.		
		[XMLConfigFile(Category = "WindowsPlatform")]
		public int MaxNestedPathLength = 200;
	}
}
