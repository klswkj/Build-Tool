using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
    // Encapsulates the environment that is used to link object files.
    internal sealed class LinkEnvironment
	{
		public readonly BuildTargetPlatform Platform;  // The platform to be compiled/linked for.
		public readonly CppConfiguration Configuration; // The configuration to be compiled/linked for.
		public readonly string Architecture;            // The architecture that is being compiled/linked (empty string by default)

		public DirectoryReference  OutputDirectory;                             // The directory to put the non-executable files in (PDBs, import library, etc)
		public List<FileReference> OutputFilePaths = new List<FileReference>(); // The file path for the executable file that is output by the linker.
		public DirectoryReference  IntermediateDirectory;                       // DirectoryReference.Combine(Generated + TypeLibrary.Header)
		public DirectoryReference  LocalShadowDirectory = null;                 // The directory to shadow source files in for syncing to remote compile servers

		// Returns the OutputFilePath is there is only one entry in OutputFilePaths
		public FileReference OutputFilePath
		{
			get
			{
				if (OutputFilePaths.Count != 1)
				{
					throw new BuildException("Attempted to use LinkEnvironmentConfiguration.OutputFilePath property, but there are multiple (or no) OutputFilePaths. You need to handle multiple in the code that called this (size = {0})", OutputFilePaths.Count);
				}
				return OutputFilePaths[0];
			}
		}

		public List<DirectoryReference> LibraryPaths        = new List<DirectoryReference>(); // A list of the paths used to find libraries.
		public List<string>             AdditionalLibraries = new List<string>(); // A list of additional libraries to link in.
		public List<string>             ExcludedLibraries   = new List<string>(); // A list of libraries to exclude from linking.
		public List<string>             RuntimeLibraryPaths = new List<string>(); // Paths to add as search paths for runtime libraries
		public string                   AdditionalArguments = ""; // Additional arguments to pass to the linker.

		// A list of the dynamically linked libraries
		// that shouldn't be loaded until they are first called into.
		public List<string> DelayLoadDLLs = new List<string>();

		// Provides a Module Definition File (.def) to the linker to describe various attributes of a DLL.
		// Necessary when exporting functions by ordinal values instead of by name.
		public string ModuleDefinitionFile;
		
		public List<FileItem> InputFiles           = new List<FileItem>(); // A list of the object files to be linked.
		public List<FileItem> DefaultResourceFiles = new List<FileItem>(); // The default resource file to link in to every binary if a custom one is not provided
		public List<FileItem> CommonResourceFiles  = new List<FileItem>(); // Resource files which should be compiled into every binary
		public List<string>   IncludeFunctions     = new List<string>();   // List of functions that should be exported from this module // IMPLEMENT_MODULE_$(ModuleRulesFileName)
		public List<ReceiptProperty> AdditionalProperties = new List<ReceiptProperty>(); // All the additional properties from the modules linked into this binary

		// The iOS/Mac frameworks to link in
		public List<string>              Frameworks                = new List<string>();
		public List<string>              WeakFrameworks            = new List<string>();
		public List<BuildFramework>      AdditionalFrameworks      = new List<BuildFramework>();      // A list of additional frameworks to link in.
		public List<BuildBundleResource> AdditionalBundleResources = new List<BuildBundleResource>(); // iOS/Mac resources that should be copied to the app bundle
		public DirectoryReference        BundleDirectory;                                             // On Mac, indicates the path to the target's application bundle
		public string                    BundleVersion;                                               // Bundle version for Mac apps
		public string                    InstallName;                                                 // When building a dynamic library on Apple platforms, specifies the installed name for other binaries that link against it.

		public int DefaultStackSize = 5000000; // The default stack memory size allocation
		public int DefaultStackSizeCommit = 0; // The amount of the default stack size to commit initially. Set to 0 to allow the OS to decide.

		// If set, overrides the program entry function on Windows platform.
		// This is used by the base Engine program so we can link in either command-line mode or
		// windowed mode without having to recompile the Launch module.
		public string WindowsEntryPointOverride = String.Empty;

		public string PGODirectory;      // Platform specific directory where PGO profiling data is stored.
		public string PGOFilenamePrefix; // Platform specific filename where PGO profiling data is saved.

		// True if runtime symbols files should be generated as a post build step for some platforms.
		// These files are used by the engine to resolve symbol names of callstack backtraces in logs.
		public bool bGenerateRuntimeSymbolFiles = true;

		public bool bCreateDebugInfo              = true;  // True if debug info should be created.
		public bool bDisableSymbolCache           = false; // True if debug symbols that are cached for some platforms should not be created.
		public bool bIsBuildingLibrary            = false; // True if we're compiling .cpp files that will go into a library (.lib file)
		public bool bIsBuildingDLL                = false; // True if we're compiling a DLL
		public bool bIsBuildingConsoleApplication = false; // True if this is a console application that's being build
		public bool bIsBuildingDotNetAssembly     = false; // True if we're building a .NET assembly (e.g. C# project)

		// True if we're building a EXE/DLL target with an import library, and that library is needed by a dependency that
		// we're directly dependent on.
		public bool bIsCrossReferenced = false;

		// True if the application we're linking has any exports, and we should be expecting the linker to
		// generate a .lib and/or .exp file along with the target output file
		public bool bHasExports = true;

		public bool bOptimizeForSize = false;  // Whether to optimize for minimal code size
		public bool bOmitFramePointers = true; // Whether to omit frame pointers or not. Disabling is useful for e.g. memory profiling on the PC
		public bool bSupportEditAndContinue;   // Whether to support edit and continue.  Only works on Microsoft compilers in 32-bit compiles.
		public bool bUseIncrementalLinking;    // Whether to use incremental linking or not.
		public bool bAllowLTCG;                // Whether to allow the use of LTCG (link time code generation) 
		public bool bPGOProfile;               // Whether to enable Profile Guided Optimization (PGO) instrumentation in this build.
		public bool bPGOOptimize;              // Whether to optimize this build with Profile Guided Optimization (PGO).
		public bool bCreateMapFile;            // Whether to request the linker create a map file as part of the build
		public bool bAllowASLR;                // Whether to allow the use of ASLR (address space layout randomization) if supported.
		public bool bUsePDBFiles;              // Whether PDB files should be used for Visual C++ builds.
		public bool bUseFastPDBLinking;        // Whether to use the :FASTLINK option when building with /DEBUG to create local PDBs
		public bool bIgnoreUnresolvedSymbols;  // Whether to ignore dangling (i.e. unresolved external) symbols in modules
		public bool bPrintTimingInfo;          // Whether to log detailed timing information

		public LinkEnvironment(BuildTargetPlatform Platform, CppConfiguration Configuration, string Architecture)
		{
			this.Platform = Platform;
			this.Configuration = Configuration;
			this.Architecture = Architecture;
		}

		public LinkEnvironment(LinkEnvironment Other)
		{
			Platform = Other.Platform;
			Configuration = Other.Configuration;
			Architecture = Other.Architecture;
			BundleDirectory = Other.BundleDirectory;
			OutputDirectory = Other.OutputDirectory;
			IntermediateDirectory = Other.IntermediateDirectory;
			LocalShadowDirectory = Other.LocalShadowDirectory;
			OutputFilePaths = Other.OutputFilePaths.ToList();
			LibraryPaths.AddRange(Other.LibraryPaths);
			ExcludedLibraries.AddRange(Other.ExcludedLibraries);
			AdditionalLibraries.AddRange(Other.AdditionalLibraries);
			RuntimeLibraryPaths.AddRange(Other.RuntimeLibraryPaths);
			Frameworks.AddRange(Other.Frameworks);
			AdditionalFrameworks.AddRange(Other.AdditionalFrameworks);
			WeakFrameworks.AddRange(Other.WeakFrameworks);
			AdditionalBundleResources.AddRange(Other.AdditionalBundleResources);
			DelayLoadDLLs.AddRange(Other.DelayLoadDLLs);
			AdditionalArguments = Other.AdditionalArguments;
			bCreateDebugInfo = Other.bCreateDebugInfo;
			bGenerateRuntimeSymbolFiles = Other.bGenerateRuntimeSymbolFiles;
			bIsBuildingLibrary = Other.bIsBuildingLibrary;
            bDisableSymbolCache = Other.bDisableSymbolCache;
			bIsBuildingDLL = Other.bIsBuildingDLL;
			bIsBuildingConsoleApplication = Other.bIsBuildingConsoleApplication;
			WindowsEntryPointOverride = Other.WindowsEntryPointOverride;
			bIsCrossReferenced = Other.bIsCrossReferenced;
			bHasExports = Other.bHasExports;
			bIsBuildingDotNetAssembly = Other.bIsBuildingDotNetAssembly;
			DefaultStackSize = Other.DefaultStackSize;
			DefaultStackSizeCommit = Other.DefaultStackSizeCommit;
			bOptimizeForSize = Other.bOptimizeForSize;
			bOmitFramePointers = Other.bOmitFramePointers;
			bSupportEditAndContinue = Other.bSupportEditAndContinue;
			bUseIncrementalLinking = Other.bUseIncrementalLinking;
			bAllowLTCG = Other.bAllowLTCG;
            bPGOOptimize = Other.bPGOOptimize;
            bPGOProfile = Other.bPGOProfile;
            PGODirectory = Other.PGODirectory;
            PGOFilenamePrefix = Other.PGOFilenamePrefix;
            bCreateMapFile = Other.bCreateMapFile;
            bAllowASLR = Other.bAllowASLR;
			bUsePDBFiles = Other.bUsePDBFiles;
			bUseFastPDBLinking = Other.bUseFastPDBLinking;
			bIgnoreUnresolvedSymbols = Other.bIgnoreUnresolvedSymbols;
			bPrintTimingInfo = Other.bPrintTimingInfo;
			BundleVersion = Other.BundleVersion;
			InstallName = Other.InstallName;
			InputFiles.AddRange(Other.InputFiles);
			DefaultResourceFiles.AddRange(Other.DefaultResourceFiles);
			CommonResourceFiles.AddRange(Other.CommonResourceFiles);
			IncludeFunctions.AddRange(Other.IncludeFunctions);
			ModuleDefinitionFile = Other.ModuleDefinitionFile;
			AdditionalProperties.AddRange(Other.AdditionalProperties);
        }
	}
}
