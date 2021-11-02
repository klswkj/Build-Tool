using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Linq;
using Microsoft.VisualStudio.Setup.Configuration;
using System.Runtime.InteropServices;
using BuildToolUtilities;

namespace BuildTool
{
#pragma warning disable IDE1006 // Naming Styles
	// Available compiler toolchains on Windows platform
	public enum WindowsCompiler
	{
		Default, // Use the default compiler.
		Clang, // Use Clang for Windows, using the clang-cl driver.
		Intel, // Use the Intel C++ compiler (ISPC x)
		VisualStudio2015_DEPRECATED, // Visual Studio 2015 (Visual C++ 14.0)

		// Visual Studio 2015 (Visual C++ 14.0)
		[Obsolete]
		VisualStudio2015 = VisualStudio2015_DEPRECATED,
		VisualStudio2017, // Visual Studio 2017 (Visual C++ 15.0)
		VisualStudio2019, // Visual Studio 2019 (Visual C++ 16.0)
	}

	// Which static analyzer to use
	public enum WindowsStaticAnalyzer
	{
		None,      // Do not perform static analysis
		VisualCpp, // Use the built-in Visual C++ static analyzer
		PVSStudio, // Use PVS-Studio for static analysis
	}

	// Available architectures on Windows platform
	public enum WindowsArchitecture
	{
		x86,   // x86
		x64,   // x64
		ARM32, // ARM
		ARM64, // ARM64
	}

	// Windows-specific target settings
	public sealed class WindowsTargetRules
	{
		// The target rules which owns this object. Used to resolve some properties.
		private readonly TargetRules Target;

		// Version of the compiler toolchain to use on Windows platform. A value of "default" will be changed to a specific version at UBT start up.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings", "CompilerVersion")]
		[XMLConfigFile(Category = "WindowsPlatform")]
		[CommandLine("-2015", Value = "VisualStudio2015")]
		[CommandLine("-2017", Value = "VisualStudio2017")]
		[CommandLine("-2019", Value = "VisualStudio2019")]
		public WindowsCompiler Compiler = WindowsCompiler.Default;

		public WindowsArchitecture Architecture { get; internal set; } = WindowsArchitecture.x64;

		// The specific toolchain version to use.
		// This may be a specific version number (for example, "14.13.26128"), or the string "Latest", to select the newest available version.
		// By default, and if it is available, we use the toolchain version indicated by WindowsPlatform.DefaultToolChainVersion
		// (otherwise, we use the latest version).
		[XMLConfigFile(Category = "WindowsPlatform")]
		public string CompilerVersion = null;

		// The specific Windows SDK version to use.
		// This may be a specific version number (for example, "8.1", "10.0" or "10.0.10150.0"), or the string "Latest", to select the newest available version.
		// By default, and if it is available, we use the Windows SDK version indicated by WindowsPlatform.DefaultWindowsSdkVersion
		// (otherwise, we use the latest version).
		[XMLConfigFile(Category = "WindowsPlatform")]
		public string WindowsSdkVersion = null;

		// Value for the WINVER macro, defining the minimum supported Windows version.
		public int TargetWindowsVersion = 0x601;
		
		// Enable PIX debugging (automatically disabled in Shipping and Test configs)
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings", "bEnablePIXProfiling")]
		public bool bPixProfilingEnabled = true;
		
		// Enable building with the Win10 SDK instead of the older Win8.1 SDK 
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings", "bUseWindowsSDK10")]
		public bool bUseWindowsSDK10 = false;
		
		// Enables runtime ray tracing support.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings", "bEnableRayTracing")]
		public bool bEnableRayTracing = false;

		// The name of the company (author, provider) that created the project.
		[ConfigFile(ConfigHierarchyType.Game, "/Script/EngineSettings.GeneralProjectSettings", "CompanyName")]
		public string CompanyName;

		// The project's copyright and/or trademark notices.
		[ConfigFile(ConfigHierarchyType.Game, "/Script/EngineSettings.GeneralProjectSettings", "CopyrightNotice")]
		public string CopyrightNotice;

		// The product name.
		[ConfigFile(ConfigHierarchyType.Game, "/Script/EngineSettings.GeneralProjectSettings", "ProjectName")]
		public string ProductName;
		
		// The static analyzer to use.
		[XMLConfigFile(Category = "WindowsPlatform")]
		[CommandLine("-StaticAnalyzer")]
		public WindowsStaticAnalyzer StaticAnalyzer = WindowsStaticAnalyzer.None;

		// Whether we should export a file containing .obj to source file mappings.
		[XMLConfigFile]
		[CommandLine("-ObjSrcMap")]
		public string ObjSrcMapFile = null;

		// Provides a Module Definition File (.def) to the linker to describe various attributes of a DLL.
		// Necessary when exporting functions by ordinal values instead of by name.
		public string ModuleDefinitionFile;

		// Specifies the path to a manifest file for the linker to embed.
		// Defaults to the manifest in Engine/Build/Windows/Resources. Can be assigned to null
		// if the target wants to specify its own manifest.
		public string ManifestFile;

		// Enables strict standard conformance mode (/permissive-) in VS2017+.
		[XMLConfigFile(Category = "WindowsPlatform")]
		[CommandLine("-Strict")]
		public bool bStrictConformanceMode = false;

		// VS2015 updated some of the CRT definitions but not all of the Windows SDK has been updated to match.
		// Microsoft provides legacy_stdio_definitions library to enable building with VS2015 until they fix everything up.
		public bool bNeedsLegacyStdioDefinitionsLib 
			=> Compiler == WindowsCompiler.VisualStudio2015_DEPRECATED || 
			   Compiler == WindowsCompiler.VisualStudio2017            || 
			   Compiler == WindowsCompiler.VisualStudio2019            || 
			   Compiler == WindowsCompiler.Clang;

		// The stack size when linking
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings")]
		public int DefaultStackSize /*= 12000000*/;

		// The stack size to commit when linking
		[RequiresUniqueBuildEnvironment]
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings")]
		public int DefaultStackSizeCommit;

		// Determines the amount of memory that the compiler allocates to construct precompiled headers (/Zm).
		// A scaling factor that determines the amount of memory that the compiler uses to construct precompiled headers.

		// The factor argument is a percentage of the default size of a compiler-defined work buffer.
		// The default value of factor is 100 (percent) (75mb), but you can specify larger or smaller amounts.
		[XMLConfigFile(Category = "WindowsPlatform")]
		public int PCHMemoryAllocationFactor = 0;

		// True if we allow using addresses larger than 2GB on 32 bit builds
		public bool bBuildLargeAddressAwareBinary = true;

		// Create an image that can be hot patched (/FUNCTIONPADMIN)
		public bool bCreateHotPatchableImage
		{
			get => bCreateHotPatchableImagePrivate ?? Target.bWithLiveCoding;
			set => bCreateHotPatchableImagePrivate = true;
		}
		private bool? bCreateHotPatchableImagePrivate;

		// Strip unreferenced symbols (/OPT:REF)
		public bool bStripUnreferencedSymbols
		{
			get { return BStripUnreferencedSymbolsPrivate ?? 
					(Target.Configuration == TargetConfiguration.Test || Target.Configuration == TargetConfiguration.Shipping) 
					&& !Target.bWithLiveCoding; }
			set { BStripUnreferencedSymbolsPrivate = value; }
		}

		private bool? bStripUnreferencedSymbolsPrivate;

        // Merge identical COMDAT sections together (/OPT:ICF)
        public bool bMergeIdenticalCOMDATs
        {
            get { return bMergeIdenticalCOMDATsPrivate ??
                       ((Target.Configuration == TargetConfiguration.Test || Target.Configuration == TargetConfiguration.Shipping)
                       && !Target.bWithLiveCoding); }
			set { bMergeIdenticalCOMDATsPrivate = value; }
        }

        private bool? bMergeIdenticalCOMDATsPrivate;

		// Whether to put global symbols in their own sections (/Gw), allowing the linker to discard any that are unused.
		public bool bOptimizeGlobalData = true;

		// (Experimental) Appends the -ftime-trace argument to the command line for Clang to output a JSON file containing a timeline for the compile. 
		// See : http://aras-p.info/blog/2019/01/16/time-trace-timeline-flame-chart-profiler-for-Clang/
		[XMLConfigFile(Category = "WindowsPlatform")]
		public bool bClangTimeTrace = false;

		// Outputs compile timing information so that it can be analyzed.
		[XMLConfigFile(Category = "WindowsPlatform")]
		public bool bCompilerTrace = false;

		// Print out files that are included by each source file
		[CommandLine("-ShowIncludes")]
		[XMLConfigFile(Category = "WindowsPlatform")]
		public bool bShowIncludes = false;

		// Bundle a working version of dbghelp.dll with the application, and use this to generate minidumps.
		// This works around a bug with the Windows 10 Fall Creators Update (1709)
		// where rich PE headers larger than a certain size would result in corrupt minidumps.
		public bool bUseBundledDbgHelp = true;

		public PVSTargetSettings PVS = new PVSTargetSettings();

		// The Visual C++ environment to use for this target.
		// Only initialized after all the target settings are finalized, in ValidateTarget().
		internal VCEnvironment Environment;

		public string ToolChainDir => Environment?.ToolChainDir.FullName;

		public string ToolChainVersion => Environment?.ToolChainVersion.ToString();

		public string WindowsSdkDir => Environment?.WindowsSdkDir.FullName;
		
		public string DiaSdkDir
		=> WindowsPlatform.FindDiaSdkDirs(Environment.Compiler).Select(x => x.FullName).FirstOrDefault();

		public bool? BStripUnreferencedSymbolsPrivate { get => bStripUnreferencedSymbolsPrivate; set => bStripUnreferencedSymbolsPrivate = value; }

		// When using a Visual Studio compiler, returns the version name as a string
		// <returns>The Visual Studio compiler version name (e.g. "2015")</returns>
		public string GetVisualStudioCompilerVersionName()
		{
			switch (Compiler)
			{
				case WindowsCompiler.Clang:
				case WindowsCompiler.Intel:
				case WindowsCompiler.VisualStudio2015_DEPRECATED:
				case WindowsCompiler.VisualStudio2017:
				case WindowsCompiler.VisualStudio2019:
					return "2015"; // VS2017 is backwards compatible with VS2015 compiler

				default:
					throw new BuildException("Unexpected WindowsCompiler version for GetVisualStudioCompilerVersionName().  Either not using a Visual Studio compiler or switch block needs to be updated");
			}
		}

		// Constructor <param name="Target">The target rules which owns this object</param>
		internal WindowsTargetRules(TargetRules Target)
		{
			this.Target = Target;

			ManifestFile = FileReference.Combine
			(
				BuildTool.EngineDirectory,
				"Build",
				"Windows",
				"Resources",
				String.Format("Default-{0}.manifest", Target.Platform)
			).FullName;
		}
	} // End public sealed class WindowsTargetRules

	// Read-only wrapper for Windows-specific target settings
	public class ReadOnlyWindowsTargetRules
	{
		// The private mutable settings object
		private readonly WindowsTargetRules Inner;

		// Constructor
		// <param name="Inner">The settings object to wrap</param>
		public ReadOnlyWindowsTargetRules(WindowsTargetRules Inner)
		{
			this.Inner = Inner;
			this.PVS = new ReadOnlyPVSTargetSettings(Inner.PVS);
		}

		// Accessors for fields on the inner TargetRules instance
		#region Read-only accessor properties 
#if !__MonoCS__
#endif
		public WindowsCompiler Compiler => Inner.Compiler;

		public WindowsArchitecture Architecture => Inner.Architecture;

		public string CompilerVersion => Inner.CompilerVersion;

		public string WindowsSdkVersion => Inner.WindowsSdkVersion;

		public int TargetWindowsVersion => Inner.TargetWindowsVersion;

		public bool bPixProfilingEnabled => Inner.bPixProfilingEnabled;

		public bool bUseWindowsSDK10 => Inner.bUseWindowsSDK10;

		public bool bEnableRayTracing => Inner.bEnableRayTracing;

		public string CompanyName => Inner.CompanyName;

		public string CopyrightNotice => Inner.CopyrightNotice;

		public string ProductName => Inner.ProductName;

		public WindowsStaticAnalyzer StaticAnalyzer => Inner.StaticAnalyzer;

		public string ObjSrcMapFile => Inner.ObjSrcMapFile;

		public string ModuleDefinitionFile => Inner.ModuleDefinitionFile;

		public string ManifestFile => Inner.ManifestFile;

		public bool bNeedsLegacyStdioDefinitionsLib => Inner.bNeedsLegacyStdioDefinitionsLib;

		public bool bStrictConformanceMode => Inner.bStrictConformanceMode;

		public int DefaultStackSize => Inner.DefaultStackSize;

		public int DefaultStackSizeCommit => Inner.DefaultStackSizeCommit;

		public int PCHMemoryAllocationFactor => Inner.PCHMemoryAllocationFactor;

		public bool bBuildLargeAddressAwareBinary => Inner.bBuildLargeAddressAwareBinary;

		public bool bCreateHotpatchableImage => Inner.bCreateHotPatchableImage;

		public bool bStripUnreferencedSymbols => Inner.bStripUnreferencedSymbols;

		public bool bMergeIdenticalCOMDATs => Inner.bMergeIdenticalCOMDATs;

		public bool bOptimizeGlobalData => Inner.bOptimizeGlobalData;

		public bool bClangTimeTrace => Inner.bClangTimeTrace;

		public bool bCompilerTrace => Inner.bCompilerTrace;

		public bool bShowIncludes => Inner.bShowIncludes;

		public string GetVisualStudioCompilerVersionName() 
			=> Inner.GetVisualStudioCompilerVersionName();

		public bool bUseBundledDbgHelp => Inner.bUseBundledDbgHelp;

		public ReadOnlyPVSTargetSettings PVS { get; private set; }

		internal VCEnvironment Environment => Inner.Environment;

		public string ToolChainDir => Inner.ToolChainDir;

		public string ToolChainVersion => Inner.ToolChainVersion;

		public string WindowsSdkDir => Inner.WindowsSdkDir;

		public string DiaSdkDir => Inner.DiaSdkDir;
		
		public string GetArchitectureSubpath()
		{
			return WindowsExports.GetArchitectureSubpath(Architecture);
		}

#if !__MonoCS__
#endif
		#endregion Read-only accessor properties 
	}

	internal sealed class WindowsPlatform : BuildPlatform
	{
		// The default compiler version to be used, if installed.
		static readonly VersionNumber DefaultClangToolChainVersion = VersionNumber.Parse("9.0.0");

		// The compiler toolchains to be used if installed, the first installed in the list will be used.
		static readonly VersionNumber[] PreferredVisualStudioToolChainVersion = new VersionNumber[]
		{
			VersionNumber.Parse("14.24.28315"), // VS2019 v16.4.3 (installed to 14.24.28314 folder)
			VersionNumber.Parse("14.22.27905"), // VS2019 v16.2.3
			VersionNumber.Parse("14.16.27023.2"), // VS2017 v15.9.15
			VersionNumber.Parse("14.16.27023"), // fallback to VS2017 15.9 toolchain, microsoft updates these in places so for local installs only this version number is present
		};

		// The default Windows SDK version to be used, if installed.
		static readonly VersionNumber[] PreferredWindowsSdkVersions = new VersionNumber[]
		{
			VersionNumber.Parse("10.0.18362.0"),
			VersionNumber.Parse("10.0.16299.0")
		};

		private static readonly Dictionary<WindowsCompiler, List<DirectoryReference>>                      CachedVSInstallDirs = new Dictionary<WindowsCompiler, List<DirectoryReference>>();
		private static readonly Dictionary<WindowsCompiler, Dictionary<VersionNumber, DirectoryReference>> CachedToolChainDirs = new Dictionary<WindowsCompiler, Dictionary<VersionNumber, DirectoryReference>>();
		private static readonly Dictionary<WindowsCompiler, List<DirectoryReference>>                      CachedDiaSdkDirs    = new Dictionary<WindowsCompiler, List<DirectoryReference>>();
		private static IReadOnlyDictionary<VersionNumber, DirectoryReference> CachedWindowsSdkDirs;
		private static IReadOnlyDictionary<VersionNumber, DirectoryReference> CachedUniversalCrtDirs;

		public static readonly bool bAllowClangLinker = false; // compiling with Clang, otherwise we use the MSVC linker
		public static readonly bool bAllowICLLinker   = true; // compiling with ICL, otherwise we use the MSVC linker

		public static readonly bool bBuildLargeAddressAwareBinary = true; // True if we allow using addresses larger than 2GB on 32 bit builds
		private readonly WindowsPlatformSDK SDK;

		// Gets the platform name that should be used.
		public override string GetPlatformName() => "Windows";

		// If this platform can be compiled with SN-DBS
		public override bool CanUseSNDBS() => true;

		public WindowsPlatform(BuildTargetPlatform InPlatform, WindowsPlatformSDK InSDK)
			: base(InPlatform)
		{
			SDK = InSDK;
		}

        // Whether the required external SDKs are installed for this platform. Could be either a manual install or an AutoSDK.
        public SDKStatus GetHasRequiredSDKsInstalled() => SDK.HasRequiredSDKsInstalled();

        public override SDKStatus HasRequiredSDKsInstalled() => SDKStatus.Valid;

        // Creates the VCEnvironment object used to control compiling and other tools.
        // Virtual to allow other platforms to override behavior
        private /*virtual*/ VCEnvironment CreateVCEnvironment(TargetRules StandardTarget)
		{
			return VCEnvironment.Create(StandardTarget.WindowsPlatform.Compiler, Platform, StandardTarget.WindowsPlatform.Architecture, StandardTarget.WindowsPlatform.CompilerVersion, StandardTarget.WindowsPlatform.WindowsSdkVersion, null);
		}

		// Validate a target's settings
		public override void ValidateTarget(TargetRules Target)
		{
			if (Platform == BuildTargetPlatform.HoloLens && Target.Architecture.ToLower() == "arm64")
			{
				Target.WindowsPlatform.Architecture = WindowsArchitecture.ARM64;
				Log.TraceInformation("Using Windows ARM64 architecture");
			}
			else if (Platform == BuildTargetPlatform.Win64)
			{
				Target.WindowsPlatform.Architecture = WindowsArchitecture.x64;
			}
			else if (Platform == BuildTargetPlatform.Win32)
			{
				Target.WindowsPlatform.Architecture = WindowsArchitecture.x86;
			}

			// Disable Simplygon support if compiling against the NULL RHI.
			if (Target.GlobalDefinitions.Contains("USE_NULL_RHI=1"))
			{				
				Target.bCompileCEF3 = false;
			}

			// Set the compiler version if necessary
			if (Target.WindowsPlatform.Compiler == WindowsCompiler.Default)
			{
				if (Target.WindowsPlatform.StaticAnalyzer == WindowsStaticAnalyzer.PVSStudio && HasCompiler(WindowsCompiler.VisualStudio2017))
				{
					Target.WindowsPlatform.Compiler = WindowsCompiler.VisualStudio2017;
				}
				else
				{
					Target.WindowsPlatform.Compiler = GetDefaultCompiler(Target.ProjectFile);
				}
			}

			// Disable linking if we're using a static analyzer
			if(Target.WindowsPlatform.StaticAnalyzer != WindowsStaticAnalyzer.None)
			{
				Target.bDisableLinking = true;
			}

			// Disable PCHs for PVS studio
			if(Target.WindowsPlatform.StaticAnalyzer == WindowsStaticAnalyzer.PVSStudio)
			{
				Target.bUsePCHFiles = false;
			}

			// Override PCH settings
			if (Target.WindowsPlatform.Compiler == WindowsCompiler.Intel)
			{
				Target.NumIncludedBytesPerUnityCPP = Math.Min(Target.NumIncludedBytesPerUnityCPP, 256 * 1024);

				Target.bUseSharedPCHs = false;

				Target.bUsePCHFiles = false;
			}

			// E&C support.
			if (Target.bSupportEditAndContinue || Target.bAdaptiveUnityEnablesEditAndContinue)
			{
				Target.bUseIncrementalLinking = true;
			}
			if (Target.bAdaptiveUnityEnablesEditAndContinue && !Target.bAdaptiveUnityDisablesPCH && !Target.bAdaptiveUnityCreatesDedicatedPCH)
			{
				throw new BuildException("bAdaptiveUnityEnablesEditAndContinue requires bAdaptiveUnityDisablesPCH or bAdaptiveUnityCreatesDedicatedPCH");
			}

			// If we're using PDB files and PCHs, the generated code needs to be compiled with the same options as the PCH.
			if ((Target.bUsePDBFiles || Target.bSupportEditAndContinue) && Target.bUsePCHFiles)
			{
				Target.bDisableDebugInfoForGeneratedCode = false;
			}

			Target.bCompileISPC = true;

			// Initialize the VC environment for the target, and set all the version numbers to the concrete values we chose
			Target.WindowsPlatform.Environment = CreateVCEnvironment(Target);

			// pull some things from it
			Target.WindowsPlatform.Compiler = Target.WindowsPlatform.Environment.Compiler;
			Target.WindowsPlatform.CompilerVersion = Target.WindowsPlatform.Environment.CompilerVersion.ToString();
			Target.WindowsPlatform.WindowsSdkVersion = Target.WindowsPlatform.Environment.WindowsSdkVersion.ToString();

			// @Todo: Still getting reports of frequent OOM issues with this enabled as of 15.7.
			// Enable fast PDB linking if we're on VS2017 15.7 or later. Previous versions have OOM issues with large projects.
			/*
			    if(!Target.bFormalBuild &&
			    !Target.bUseFastPDBLinking.HasValue &&
			    WindowsCompiler.VisualStudio2017 <= Target.WindowsPlatform.Compiler)
			    {
			        VersionNumber Version;
			        DirectoryReference ToolChainDir;
			        if(TryGetVCToolChainDir(Target.WindowsPlatform.Compiler, Target.WindowsPlatform.CompilerVersion, out Version, out ToolChainDir) && Version >= new VersionNumber(14, 14, 26316))
			        {
			            Target.bUseFastPDBLinking = true;
			//		}
			//	}
			*/
		}

		// Gets the default compiler which should be used, if it's not set explicitly by the target, command line, or config file.
		// <returns>The default compiler version</returns>
		internal static WindowsCompiler GetDefaultCompiler(FileReference ProjectFile)
		{
			// If there's no specific compiler set, try to pick the matching compiler for the selected IDE
			if(ProjectFileGeneratorSettings.Format != null)
			{
				foreach(ProjectFileFormat Format in ProjectFileGeneratorSettings.ParseFormatList(ProjectFileGeneratorSettings.Format))
				{
					if (Format == ProjectFileFormat.VisualStudio2019)
					{
						return WindowsCompiler.VisualStudio2019;
					}
					else if (Format == ProjectFileFormat.VisualStudio2017)
					{
						return WindowsCompiler.VisualStudio2017;
					}
				} 
			}

			// Also check the default format for the Visual Studio project generator
			if (XMLConfig.TryGetValue(typeof(VCProjectFileGenerator), "Version", out object ProjectFormatObject))
			{
				VCProjectFileFormat ProjectFormat = (VCProjectFileFormat)ProjectFormatObject;
				if (ProjectFormat == VCProjectFileFormat.VisualStudio2019)
				{
					return WindowsCompiler.VisualStudio2019;
				}
				else if (ProjectFormat == VCProjectFileFormat.VisualStudio2017)
				{
					return WindowsCompiler.VisualStudio2017;
				}
			}

			// Check the editor settings too
			if (ProjectFileGenerator.GetPreferredSourceCodeAccessor(ProjectFile, out ProjectFileFormat PreferredAccessor))
			{
				if (PreferredAccessor == ProjectFileFormat.VisualStudio2019)
				{
					return WindowsCompiler.VisualStudio2019;
				}
				else if (PreferredAccessor == ProjectFileFormat.VisualStudio2017)
				{
					return WindowsCompiler.VisualStudio2017;
				}
			}

			// Second, default based on what's installed, test for 2015 first
			if (HasCompiler(WindowsCompiler.VisualStudio2019))
			{
				return WindowsCompiler.VisualStudio2019;
			}
			if (HasCompiler(WindowsCompiler.VisualStudio2017))
			{
				return WindowsCompiler.VisualStudio2017;
			}

			// If we do have a Visual Studio installation, but we're missing just the C++ parts, warn about that.
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			if (TryGetVSInstallDir(WindowsCompiler.VisualStudio2019, out DirectoryReference VSInstallDir))
			{
				Log.TraceWarning("Visual Studio 2019 is installed, but is missing the C++ toolchain. Please verify that the \"VC++ 2019 toolset\" component is selected in the Visual Studio 2019 installation options.");
			}
			else if (TryGetVSInstallDir(WindowsCompiler.VisualStudio2017, out VSInstallDir))
			{
				Log.TraceWarning("Visual Studio 2017 is installed, but is missing the C++ toolchain. Please verify that the \"VC++ 2017 toolset\" component is selected in the Visual Studio 2017 installation options.");
			}
			else
			{
				Log.TraceWarning("No Visual C++ installation was found. Please download and install Visual Studio 2017 with C++ components.");
			}
#pragma warning restore IDE0059 // Unnecessary assignment of a value

			// Finally, default to VS2019 anyway
			return WindowsCompiler.VisualStudio2019;
		}

		// Returns the human-readable name of the given compiler
		public static string GetCompilerName(WindowsCompiler Compiler)
		{
			switch (Compiler)
			{
				case WindowsCompiler.VisualStudio2015_DEPRECATED:
					return "Visual Studio 2015";
				case WindowsCompiler.VisualStudio2017:
					return "Visual Studio 2017";
				case WindowsCompiler.VisualStudio2019:
					return "Visual Studio 2019";
				default:
					return Compiler.ToString();
			}
		}

		// Get the first Visual Studio install directory for the given compiler version.
		// Note that it is possible for the compiler toolchain to be installed without Visual Studio.
		
		// <param name="Compiler">Version of the toolchain to look for.</param>
		// <param name="InstallDir">On success, the directory that Visual Studio is installed to.</param>
		// <returns>True if the directory was found, false otherwise.</returns>
		public static bool TryGetVSInstallDir(WindowsCompiler Compiler, out DirectoryReference InstallDir)
		{
			if (BuildHostPlatform.Current.Platform != BuildTargetPlatform.Win64 && 
				BuildHostPlatform.Current.Platform != BuildTargetPlatform.Win32)
			{
				InstallDir = null;
				return false;
			}

			List<DirectoryReference> InstallDirs = FindVSInstallDirs(Compiler);
			if(InstallDirs.Count == 0)
			{
				InstallDir = null;
				return false;
			}
			else
			{
				InstallDir = InstallDirs[0];
				return true;
			}
		}

		// Read the Visual Studio install directory for the given compiler version.
		// Note that it is possible for the compiler toolchain to be installed without Visual Studio.
		private static List<DirectoryReference> FindVSInstallDirs(WindowsCompiler CompilerToLookFor)
		{
			if (!CachedVSInstallDirs.TryGetValue(CompilerToLookFor, out List<DirectoryReference> InstallDirs))
			{
				InstallDirs = new List<DirectoryReference>();
				if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32)
				{
					if (CompilerToLookFor == WindowsCompiler.VisualStudio2015_DEPRECATED)
					{
						// VS2015 just installs one toolchain; use that.
						if (TryReadInstallDirRegistryKey32("Microsoft\\VisualStudio\\SxS\\VS7", "14.0", out DirectoryReference InstallDir))
						{
							InstallDirs.Add(InstallDir);
						}
					}
					else if (CompilerToLookFor == WindowsCompiler.VisualStudio2017 || 
						     CompilerToLookFor == WindowsCompiler.VisualStudio2019)
					{
						SortedDictionary<int, DirectoryReference> SortedInstallDirs = new SortedDictionary<int, DirectoryReference>();
						try
						{
							SetupConfiguration Setup = new SetupConfiguration();
							IEnumSetupInstances Enumerator = Setup.EnumAllInstances();

							ISetupInstance[] Instances = new ISetupInstance[1];
							for (;;)
							{
								{
									Enumerator.Next(1, Instances, out int NumFetched);

									if (NumFetched == 0)
									{
										break;
									}
								}

								ISetupInstance2 Instance = (ISetupInstance2)Instances[0];
								if ((Instance.GetState() & InstanceState.Local) == InstanceState.Local)
								{
									string VersionString = Instance.GetInstallationVersion();

									if (VersionNumber.TryParse(VersionString, out VersionNumber Version))
									{
										VersionNumber Version2019 = new VersionNumber(16);
										if (CompilerToLookFor == WindowsCompiler.VisualStudio2019 && Version < Version2019)
										{
											continue;
										}
										else if (CompilerToLookFor == WindowsCompiler.VisualStudio2017 && Version2019 <= Version)
										{
											continue;
										}
									}

									int SortOrder = SortedInstallDirs.Count;

									if (Instance is ISetupInstanceCatalog Catalog && 
										Catalog.IsPrerelease())
									{
										SortOrder |= (1 << 16);
									}

									string ProductId = Instance.GetProduct().GetId();
									if (ProductId.Equals("Microsoft.VisualStudio.Product.WDExpress", StringComparison.Ordinal))
									{
										SortOrder |= (1 << 17);
									}

									Log.TraceLog("Found Visual Studio installation: {0} (Product={1}, Version={2}, Sort={3})", 
										Instance.GetInstallationPath(), ProductId, VersionString, SortOrder);

									SortedInstallDirs.Add(SortOrder, new DirectoryReference(Instance.GetInstallationPath()));
								}
							}
						}
						catch
						{
							// throw new BuildException("");
						}
						InstallDirs.AddRange(SortedInstallDirs.Values);
					}
					else
					{
						throw new BuildException("Unsupported compiler version ({0})", CompilerToLookFor);
					}
				}

				CachedVSInstallDirs.Add(CompilerToLookFor, InstallDirs);
			}

			return InstallDirs;
		}

		// Determines the directory containing the MSVC toolchain
		// <param name="Compiler">Major version of the compiler to use</param>
		// <returns>Map of version number to directories</returns>
		private static Dictionary<VersionNumber, DirectoryReference> FindToolChainDirs(WindowsCompiler CompilerToUse)
		{
			if (!CachedToolChainDirs.TryGetValue(CompilerToUse, out Dictionary<VersionNumber, DirectoryReference> ToolChainVersionToDir))
			{
				ToolChainVersionToDir = new Dictionary<VersionNumber, DirectoryReference>();
				if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || 
					BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32)
				{
					if (CompilerToUse == WindowsCompiler.Clang)
					{
						// Check for a manual installation
						DirectoryReference InstallDir = DirectoryReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFiles), "LLVM");
						if (IsValidToolChainDirClang(InstallDir))
						{
							FileReference CompilerFile = FileReference.Combine(InstallDir, "bin", "clang-cl.exe");
							if (FileReference.Exists(CompilerFile))
							{
								FileVersionInfo VersionInfo = FileVersionInfo.GetVersionInfo(CompilerFile.FullName);
								VersionNumber Version = new VersionNumber(VersionInfo.FileMajorPart, VersionInfo.FileMinorPart, VersionInfo.FileBuildPart);
								ToolChainVersionToDir[Version] = InstallDir;
							}
						}

						// Check for AutoSDK paths
						if (BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference AutoSdkDir))
						{
							DirectoryReference ClangBaseDir = DirectoryReference.Combine(AutoSdkDir, "Win64", "LLVM");
							if (DirectoryReference.Exists(ClangBaseDir))
							{
								foreach (DirectoryReference ToolChainDir in DirectoryReference.EnumerateDirectories(ClangBaseDir))
								{
									if (VersionNumber.TryParse(ToolChainDir.GetDirectoryName(), out VersionNumber Version) && 
										IsValidToolChainDirClang(ToolChainDir))
									{
										ToolChainVersionToDir[Version] = ToolChainDir;
									}
								}
							}
						}
					}
					else if (CompilerToUse == WindowsCompiler.Intel)
					{
						// Just check for a manual installation
						DirectoryReference InstallDir = DirectoryReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86), "IntelSWTools", "compilers_and_libraries", "windows");
						if (DirectoryReference.Exists(InstallDir))
						{
							FileReference IclPath = FileReference.Combine(InstallDir, "bin", "intel64", "icl.exe");
							if (FileReference.Exists(IclPath))
							{
								FileVersionInfo VersionInfo = FileVersionInfo.GetVersionInfo(IclPath.FullName);
								VersionNumber   Version     = new VersionNumber(VersionInfo.FileMajorPart, VersionInfo.FileMinorPart, VersionInfo.FileBuildPart);
								
								ToolChainVersionToDir[Version] = InstallDir;
							}
						}
					}
					else if (CompilerToUse == WindowsCompiler.VisualStudio2015_DEPRECATED)
					{
						// VS2015 just installs one toolchain; use that.
						List<DirectoryReference> InstallDirs = FindVSInstallDirs(CompilerToUse);
						foreach (DirectoryReference InstallDir in InstallDirs)
						{
							DirectoryReference ToolChainBaseDir = DirectoryReference.Combine(InstallDir, "VC");
							if (IsValidToolChainDir2015(ToolChainBaseDir))
							{
								ToolChainVersionToDir[new VersionNumber(14, 0)] = ToolChainBaseDir;
							}
						}
					}
					else if (CompilerToUse == WindowsCompiler.VisualStudio2017 || 
						     CompilerToUse == WindowsCompiler.VisualStudio2019)
					{
						// Enumerate all the manually installed toolchains
						List<DirectoryReference> InstallDirs = FindVSInstallDirs(CompilerToUse);
						foreach (DirectoryReference InstallDir in InstallDirs)
						{
							DirectoryReference ToolChainBaseDir = DirectoryReference.Combine(InstallDir, "VC", "Tools", "MSVC");
							if (DirectoryReference.Exists(ToolChainBaseDir))
							{
								foreach (DirectoryReference ToolChainDir in DirectoryReference.EnumerateDirectories(ToolChainBaseDir))
								{
									if (IsValidToolChainDir2017or2019(ToolChainDir, out VersionNumber Version))
									{
										Log.TraceLog("Found Visual Studio toolchain: {0} (Version={1})", ToolChainDir, Version);
										if (!ToolChainVersionToDir.ContainsKey(Version))
										{
											ToolChainVersionToDir[Version] = ToolChainDir;
										}
									}
								}
							}
						}

						// Enumerate all the AutoSDK toolchains
						if (BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference PlatformDir))
						{
							DirectoryReference ToolChainBaseDir = DirectoryReference.Combine(PlatformDir, "Win64", (CompilerToUse == WindowsCompiler.VisualStudio2019) ? "VS2019" : "VS2017");
							if (DirectoryReference.Exists(ToolChainBaseDir))
							{
								foreach (DirectoryReference ToolChainDir in DirectoryReference.EnumerateDirectories(ToolChainBaseDir))
								{
									if (IsValidToolChainDir2017or2019(ToolChainDir, out VersionNumber Version))
									{
										Log.TraceLog("Found Visual Studio toolchain: {0} (Version={1})", ToolChainDir, Version);
										if (!ToolChainVersionToDir.ContainsKey(Version))
										{
											ToolChainVersionToDir[Version] = ToolChainDir;
										}
									}
								}
							}
						}
					}
					else
					{
						throw new BuildException("Unsupported compiler version ({0})", CompilerToUse);
					}
				}

				CachedToolChainDirs.Add(CompilerToUse, ToolChainVersionToDir);
			}

			return ToolChainVersionToDir;
		}

		// Checks if the given directory contains a valid Clang toolchain
		static bool IsValidToolChainDirClang(DirectoryReference ToolChainDirToCheck)
		{
			return FileReference.Exists(FileReference.Combine(ToolChainDirToCheck, "bin", "clang-cl.exe"));
		}

		// Checks if the given directory contains a valid Visual Studio 2015 toolchain
		static bool IsValidToolChainDir2015(DirectoryReference ToolChainDirToCheck)
		{
			return FileReference.Exists(FileReference.Combine(ToolChainDirToCheck, "bin", "amd64", "cl.exe")) || 
				   FileReference.Exists(FileReference.Combine(ToolChainDirToCheck, "bin", "x86_amd64", "cl.exe"));
		}

		// Determines if the given path is a valid Visual C++ version number
		static bool IsValidToolChainDir2017or2019(DirectoryReference ToolChainDir, out VersionNumber ToolChainVersion)
		{
			FileReference CompilerExe = FileReference.Combine(ToolChainDir, "bin", "Hostx86", "x64", "cl.exe");
			if(!FileReference.Exists(CompilerExe))
			{
				CompilerExe = FileReference.Combine(ToolChainDir, "bin", "Hostx64", "x64", "cl.exe");
				if(!FileReference.Exists(CompilerExe))
				{
					ToolChainVersion = null;
					return false;
				}
			}

			FileVersionInfo VersionInfo = FileVersionInfo.GetVersionInfo(CompilerExe.FullName);
			if (VersionInfo.ProductMajorPart != 0)
			{
				ToolChainVersion = new VersionNumber(VersionInfo.ProductMajorPart, VersionInfo.ProductMinorPart, VersionInfo.ProductBuildPart);
				return true;
			}

			return VersionNumber.TryParse(ToolChainDir.GetDirectoryName(), out ToolChainVersion);
		}

		// Checks if the given directory contains a 64-bit toolchain.
		private static bool Has64BitToolChain(DirectoryReference ToolChainDir)
		{
			return FileReference.Exists(FileReference.Combine(ToolChainDir, "bin", "amd64", "cl.exe")) || 
				   FileReference.Exists(FileReference.Combine(ToolChainDir, "bin", "Hostx64", "x64", "cl.exe"));
		}

		// Determines if an IDE for the given compiler is installed.
		public static bool HasIDE(WindowsCompiler Compiler)
		{
			return 0 < FindVSInstallDirs(Compiler).Count;
		}

		// Determines if a given compiler is installed
		public static bool HasCompiler(WindowsCompiler Compiler)
		{
			return 0 < FindToolChainDirs(Compiler).Count;
		}

		// Checks if a given Visual C++ toolchain version is compatible
		static bool IsCompatibleVisualCppToolChain(VersionNumber Version)
		{
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			return IsCompatibleVisualCppToolChain(Version, out string OutMessage);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
		}

		// Checks if a given Visual C++ toolchain version is compatible
		static bool IsCompatibleVisualCppToolChain(VersionNumber Version, out string Message)
		{
			if (new VersionNumber(14, 23, 0) <= Version && Version < new VersionNumber(14, 23, 28107))
			{
				Message = String.Format("The Visual C++ 14.23 toolchain is known to have code-generation issues. Please install an earlier or later toolchain from the Visual Studio installer. See here for more information: https://developercommunity.visualstudio.com/content/problem/734585/msvc-142328019-compilation-bug.html");
				return false;
			}
			else
			{
				Message = null;
				return true;
			}
		}

		// Determines the directory containing the MSVC toolchain
		public static bool TryGetToolChainDir
		(
			WindowsCompiler Compiler,
			string CompilerVersion,
			out VersionNumber OutToolChainVersion,
			out DirectoryReference OutToolChainDir
		)
		{
			// Find all the installed toolchains
			Dictionary<VersionNumber, DirectoryReference> ToolChainVersionToDir = FindToolChainDirs(Compiler);

			// Figure out the actual version number that we want
			VersionNumber ToolChainVersion = null;
			if(CompilerVersion != null)
			{
				if(String.Compare(CompilerVersion, "Latest", StringComparison.InvariantCultureIgnoreCase) == 0 && 0 < ToolChainVersionToDir.Count)
				{
					ToolChainVersion = ToolChainVersionToDir.OrderBy(x => IsCompatibleVisualCppToolChain(x.Key)).ThenBy(x => Has64BitToolChain(x.Value)).ThenBy(x => x.Key).Last().Key;
				}
				else if(!VersionNumber.TryParse(CompilerVersion, out ToolChainVersion))
				{
					throw new BuildException("Unable to find {0} toolchain; '{1}' is an invalid version", GetCompilerName(Compiler), CompilerVersion);
				}
			}
			else
			{
				VersionNumber[] PotentialToolchains;
				if(Compiler == WindowsCompiler.Clang)
				{
					PotentialToolchains = new VersionNumber[] { DefaultClangToolChainVersion };
				}
				else
				{
					PotentialToolchains = PreferredVisualStudioToolChainVersion;
				}

				foreach (VersionNumber PreferredToolchain in PotentialToolchains)
				{
					if (ToolChainVersionToDir.TryGetValue(PreferredToolchain, out DirectoryReference DefaultToolChainDir) &&
						(Compiler == WindowsCompiler.Clang || Has64BitToolChain(DefaultToolChainDir)))
					{
						ToolChainVersion = PreferredToolchain;
						break;
					}
				}

				// if we failed to find any of our preferred toolchains we pick the newest (highest version number)
				if (ToolChainVersion == null && 
					0 < ToolChainVersionToDir.Count)
				{
					ToolChainVersion = ToolChainVersionToDir.OrderBy(x => IsCompatibleVisualCppToolChain(x.Key)).ThenBy(x => Has64BitToolChain(x.Value)).ThenBy(x => x.Key).Last().Key;
				}
			}

			// Check it's valid
			if (ToolChainVersion != null && 
				!IsCompatibleVisualCppToolChain(ToolChainVersion, out string Message))
			{
				throw new BuildException(Message);
			}

			// Get the actual directory for this version
			if (ToolChainVersion != null && 
				ToolChainVersionToDir.TryGetValue(ToolChainVersion, out OutToolChainDir))
			{
				OutToolChainVersion = ToolChainVersion;
				return true;
			}

			// Fail in Trying to get ToolChainDir.
			OutToolChainVersion = null;
			OutToolChainDir     = null;
			return false;
		}

		static readonly KeyValuePair<RegistryKey, string>[] InstallDirRoots = 
		{
			new KeyValuePair<RegistryKey, string>(Registry.CurrentUser, "SOFTWARE\\"),
			new KeyValuePair<RegistryKey, string>(Registry.LocalMachine, "SOFTWARE\\"),
			new KeyValuePair<RegistryKey, string>(Registry.CurrentUser,  "SOFTWARE\\Wow6432Node\\"),
			new KeyValuePair<RegistryKey, string>(Registry.LocalMachine, "SOFTWARE\\Wow6432Node\\")
		};

		// Reads an install directory for a 32-bit program from a registry key.
		// This checks for per-user and machine wide settings, and under the Wow64 virtual keys
		// (HKCU\SOFTWARE, HKLM\SOFTWARE, HKCU\SOFTWARE\Wow6432Node, HKLM\SOFTWARE\Wow6432Node).
		static bool TryReadInstallDirRegistryKey32(string KeySuffixToRead, string ValueNameToBeRead, out DirectoryReference OutInstallDir)
		{
			foreach (KeyValuePair<RegistryKey, string> InstallRoot in InstallDirRoots)
			{
				using (RegistryKey Key = InstallRoot.Key.OpenSubKey(InstallRoot.Value + KeySuffixToRead))
				{
					if (Key != null && 
						TryReadDirRegistryKey(Key.Name, ValueNameToBeRead, out OutInstallDir))
					{
						return true;
					}
				}
			}
			OutInstallDir = null;
			return false;
		}

		// For each root location relevant to install dirs, look for the given key and add its subkeys to the set of subkeys to return.
		// This checks for per-user and machine wide settings, and under the Wow64 virtual keys
		// (HKCU\SOFTWARE, HKLM\SOFTWARE, HKCU\SOFTWARE\Wow6432Node, HKLM\SOFTWARE\Wow6432Node).
		static string[] ReadInstallDirSubKeys32(string KeyName)
		{
			HashSet<string> OutAllSubKeys = new HashSet<string>(StringComparer.Ordinal);
			foreach (KeyValuePair<RegistryKey, string> Root in InstallDirRoots)
			{
				using (RegistryKey Key = Root.Key.OpenSubKey(Root.Value + KeyName))
				{
					if (Key == null)
					{
						continue;
					}

					foreach (string SubKey in Key.GetSubKeyNames())
					{
						OutAllSubKeys.Add(SubKey);
					}
				}
			}
			return OutAllSubKeys.ToArray();
		}

		// Attempts to reads a directory name stored in a registry key
		static bool TryReadDirRegistryKey(string RegistryKeyName, string RegistryValueName, out DirectoryReference Value)
		{
			string StringValue = Registry.GetValue(RegistryKeyName, RegistryValueName, null) as string;
			if (StringValue.HasValue())
			{
				Value = new DirectoryReference(StringValue);
				return true;
			}
			else
			{
				Value = null;
				return false;
			}
		}

		// Gets the path to MSBuild. This mirrors the logic in GetMSBuildPath.bat
		public static bool TryGetMsBuildPath(out FileReference OutLocation)
		{
			// Get the Visual Studio 2019 install directory
			List<DirectoryReference> InstallDirs2019 = WindowsPlatform.FindVSInstallDirs(WindowsCompiler.VisualStudio2019);
			foreach (DirectoryReference InstallDir in InstallDirs2019)
			{
				FileReference MsBuildLocation = FileReference.Combine(InstallDir, "MSBuild", "Current", "Bin", "MSBuild.exe");
				if (FileReference.Exists(MsBuildLocation))
				{
					OutLocation = MsBuildLocation;
					return true;
				}
			}

			// Get the Visual Studio 2017 install directory
			List<DirectoryReference> InstallDirs2017 = WindowsPlatform.FindVSInstallDirs(WindowsCompiler.VisualStudio2017);
			foreach (DirectoryReference InstallDir in InstallDirs2017)
			{
				FileReference MsBuildLocation = FileReference.Combine(InstallDir, "MSBuild", "15.0", "Bin", "MSBuild.exe");
				if(FileReference.Exists(MsBuildLocation))
				{
					OutLocation = MsBuildLocation;
					return true;
				}
			}

			// Try to get the MSBuild 14.0 path directly (see https://msdn.microsoft.com/en-us/library/hh162058(v=vs.120).aspx)
			FileReference ToolPath 
				= FileReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86), "MSBuild", "14.0", "bin", "MSBuild.exe");
			if(FileReference.Exists(ToolPath))
			{
				OutLocation = ToolPath;
				return true;
			}

			// Check for older versions of MSBuild. These are registered as separate versions in the registry.
			if (TryReadMsBuildInstallPath("Microsoft\\MSBuild\\ToolsVersions\\14.0", "MSBuildToolsPath", "MSBuild.exe", out ToolPath))
			{
				OutLocation = ToolPath;
				return true;
			}
			if (TryReadMsBuildInstallPath("Microsoft\\MSBuild\\ToolsVersions\\12.0", "MSBuildToolsPath", "MSBuild.exe", out ToolPath))
			{
				OutLocation = ToolPath;
				return true;
			}
			if (TryReadMsBuildInstallPath("Microsoft\\MSBuild\\ToolsVersions\\4.0", "MSBuildToolsPath", "MSBuild.exe", out ToolPath))
			{
				OutLocation = ToolPath;
				return true;
			}

			OutLocation = null;
			return false;
		}

		// Gets the MSBuild path, and throws an exception on failure.
		public static FileReference GetMsBuildEXEPath()
		{
			if (!TryGetMsBuildPath(out FileReference Location))
			{
				throw new BuildException("Unable to find installation of MSBuild.");
			}
			return Location;
		}

		public static string GetArchitectureSubpath(WindowsArchitecture arch)
		{
			string archPath = "Unknown";
			if (arch == WindowsArchitecture.x86)
			{
				archPath = "x86";
			}
			else if (arch == WindowsArchitecture.ARM32)
			{
				archPath = "arm";
			}
			else if (arch == WindowsArchitecture.x64)
			{
				archPath = "x64";
			}
			else if (arch == WindowsArchitecture.ARM64)
			{
				archPath = "arm64";
			}
			return archPath;
		}

		// Function to query the registry under HKCU/HKLM Win32/Wow64 software registry keys for a certain install directory.
		// This mirrors the logic in GetMSBuildPath.bat
		static bool TryReadMsBuildInstallPath(string KeyRelativePath, string KeyName, string MsBuildRelativePath, out FileReference OutMsBuildPath)
		{
			string[] KeyBasePaths =
			{
				@"HKEY_CURRENT_USER\SOFTWARE\",
				@"HKEY_LOCAL_MACHINE\SOFTWARE\",
				@"HKEY_CURRENT_USER\SOFTWARE\Wow6432Node\",
				@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\"
			};

			foreach (string KeyBasePath in KeyBasePaths)
			{
				if (Registry.GetValue(KeyBasePath + KeyRelativePath, KeyName, null) is string Value)
				{
					FileReference MsBuildPath = FileReference.Combine(new DirectoryReference(Value), MsBuildRelativePath);
					if (FileReference.Exists(MsBuildPath))
					{
						OutMsBuildPath = MsBuildPath;
						return true;
					}
				}
			}

			OutMsBuildPath = null;
			return false;
		}

		// Determines the directory containing the MSVC toolchain
		public static List<DirectoryReference> FindDiaSdkDirs(WindowsCompiler Compiler)
		{
			if (!CachedDiaSdkDirs.TryGetValue(Compiler, out List<DirectoryReference> ResultDiaSdkDirs))
			{
				ResultDiaSdkDirs = new List<DirectoryReference>();

				if (BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference PlatformDir))
				{
					DirectoryReference DiaSdkDir = DirectoryReference.Combine(PlatformDir, "Win64", "DIA SDK", (Compiler == WindowsCompiler.VisualStudio2019) ? "VS2019" : "VS2017");
					if (IsValidDiaSdkDir(DiaSdkDir))
					{
						ResultDiaSdkDirs.Add(DiaSdkDir);
					}
				}

				List<DirectoryReference> VisualStudioDirs = FindVSInstallDirs(Compiler);
				foreach (DirectoryReference VisualStudioDir in VisualStudioDirs)
				{
					DirectoryReference DiaSdkDir = DirectoryReference.Combine(VisualStudioDir, "DIA SDK");
					if (IsValidDiaSdkDir(DiaSdkDir))
					{
						ResultDiaSdkDirs.Add(DiaSdkDir);
					}
				}
			}
			return ResultDiaSdkDirs;
		}

		// Determines if a directory contains a valid DIA SDK
		static bool IsValidDiaSdkDir(DirectoryReference DiaSdkDir)
		{
			return FileReference.Exists(FileReference.Combine(DiaSdkDir, "bin", "amd64", "msdia140.dll"));
		}

		// Updates the CachedWindowsSdkDirs and CachedUniversalCrtDirs variables
		private static void UpdateCachedWindowsSdks()
		{
			Dictionary<VersionNumber, DirectoryReference> WindowsSdkDirs   = new Dictionary<VersionNumber, DirectoryReference>();
			Dictionary<VersionNumber, DirectoryReference> UniversalCrtDirs = new Dictionary<VersionNumber, DirectoryReference>();

			// Enumerate the Windows 8.1 SDK, if present
			if (TryReadInstallDirRegistryKey32("Microsoft\\Microsoft SDKs\\Windows\\v8.1", "InstallationFolder", out DirectoryReference InstallDir_8_1))
			{
				if (FileReference.Exists(FileReference.Combine(InstallDir_8_1, "Include", "um", "windows.h")))
				{
					Log.TraceLog("Found Windows 8.1 SDK at {0}", InstallDir_8_1);
					VersionNumber Version_8_1 = new VersionNumber(8, 1);
					WindowsSdkDirs[Version_8_1] = InstallDir_8_1;
				}
			}

			// Find all the root directories for Windows 10 SDKs
			List<DirectoryReference> InstallDirs_10 = new List<DirectoryReference>();
			EnumerateSdkRootDirs(InstallDirs_10);

			// Enumerate all the Windows 10 SDKs
			foreach(DirectoryReference InstallDir_10 in InstallDirs_10.Distinct())
			{
				DirectoryReference IncludeRootDir = DirectoryReference.Combine(InstallDir_10, "Include");
				if(DirectoryReference.Exists(IncludeRootDir))
				{
					foreach(DirectoryReference IncludeDir in DirectoryReference.EnumerateDirectories(IncludeRootDir))
					{
						if (VersionNumber.TryParse(IncludeDir.GetDirectoryName(), out VersionNumber IncludeVersion))
						{
							if (FileReference.Exists(FileReference.Combine(IncludeDir, "um", "windows.h")))
							{
								Log.TraceLog("Found Windows 10 SDK version {0} at {1}", IncludeVersion, InstallDir_10);
								WindowsSdkDirs[IncludeVersion] = InstallDir_10;
							}
							if (FileReference.Exists(FileReference.Combine(IncludeDir, "ucrt", "corecrt.h")))
							{
								Log.TraceLog("Found Universal CRT version {0} at {1}", IncludeVersion, InstallDir_10);
								UniversalCrtDirs[IncludeVersion] = InstallDir_10;
							}
						}
					}
				}
			}

			CachedWindowsSdkDirs   = WindowsSdkDirs;
			CachedUniversalCrtDirs = UniversalCrtDirs;
		}
		
		// Finds all the installed Windows SDK versions
		public static IReadOnlyDictionary<VersionNumber, DirectoryReference> FindWindowsSdkDirs()
		{
			// Update the cache of install directories, if it's not set
			if(CachedWindowsSdkDirs == null)
			{
				UpdateCachedWindowsSdks();
			}
			return CachedWindowsSdkDirs;
		}
		
		// Finds all the installed Universal CRT versions
		public static IReadOnlyDictionary<VersionNumber, DirectoryReference> FindUniversalCrtDirs()
		{
			if(CachedUniversalCrtDirs == null)
			{
				UpdateCachedWindowsSdks();
			}
			return CachedUniversalCrtDirs;
		}

		// Enumerates all the Windows 10 SDK root directories
		private static void EnumerateSdkRootDirs(List<DirectoryReference> RootDirs)
		{
			if (TryReadInstallDirRegistryKey32("Microsoft\\Windows Kits\\Installed Roots", "KitsRoot10", out DirectoryReference RootDir))
			{
				Log.TraceLog("Found Windows 10 SDK root at {0} (1)", RootDir);
				RootDirs.Add(RootDir);
			}
			if (TryReadInstallDirRegistryKey32("Microsoft\\Microsoft SDKs\\Windows\\v10.0", "InstallationFolder", out RootDir))
			{
				Log.TraceLog("Found Windows 10 SDK root at {0} (2)", RootDir);
				RootDirs.Add(RootDir);
			}

			if (BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference HostAutoSdkDir))
			{
				DirectoryReference RootDirAutoSdk = DirectoryReference.Combine(HostAutoSdkDir, "Win64", "Windows Kits", "10");
				if (DirectoryReference.Exists(RootDirAutoSdk))
				{
					Log.TraceLog("Found Windows 10 AutoSDK root at {0}", RootDirAutoSdk);
					RootDirs.Add(RootDirAutoSdk);
				}
			}
		}

		
		// Determines the directory containing the Windows SDK toolchain
		
		// <param name="DesiredVersion">The desired Windows SDK version. This may be "Latest", a specific version number, or null. If null, the function will look for DefaultWindowsSdkVersion. Failing that, it will return the latest version.</param>
		// <param name="OutSdkVersion">Receives the version number of the selected Windows SDK</param>
		// <param name="OutSdkDir">Receives the root directory for the selected SDK</param>
		// <returns>True if the toolchain directory was found correctly</returns>
		public static bool TryGetWindowsSdkDir(string DesiredVersion, out VersionNumber OutSdkVersion, out DirectoryReference OutSdkDir)
		{
			UpdateCachedWindowsSdks();

			// Figure out which version number to look for
			VersionNumber WindowsSdkVersion = null;
			if(DesiredVersion.HasValue())
			{
				if(String.Compare(DesiredVersion, "Latest", StringComparison.InvariantCultureIgnoreCase) == 0 && 
					0 < CachedWindowsSdkDirs.Count)
				{
					WindowsSdkVersion = CachedWindowsSdkDirs.OrderBy(x => x.Key).Last().Key;
				}
				else if(!VersionNumber.TryParse(DesiredVersion, out WindowsSdkVersion))
				{
					throw new BuildException("Unable to find requested Windows SDK; '{0}' is an invalid version", DesiredVersion);
				}
			}
			else
			{
				WindowsSdkVersion = PreferredWindowsSdkVersions.FirstOrDefault(x => CachedWindowsSdkDirs.ContainsKey(x));
				if(WindowsSdkVersion == null && 
					0 < CachedWindowsSdkDirs.Count)
				{
					WindowsSdkVersion = CachedWindowsSdkDirs.OrderBy(x => x.Key).Last().Key;
				}
			}

			// Get the actual directory for this version
			if (WindowsSdkVersion != null && 
				CachedWindowsSdkDirs.TryGetValue(WindowsSdkVersion, out DirectoryReference SdkDir))
			{
				OutSdkDir     = SdkDir;
				OutSdkVersion = WindowsSdkVersion;
				return true;
			}
			else
			{
				OutSdkDir     = null;
				OutSdkVersion = null;
				return false;
			}
		}

		// Gets the installation directory for the NETFXSDK
		public static bool TryGetNetFxSdkInstallDir(out DirectoryReference OutInstallDir)
		{
			if (BuildPlatformSDK.TryGetHostPlatformAutoSDKDir(out DirectoryReference HostAutoSdkDir))
			{
				DirectoryReference NetFxDir_4_6 = DirectoryReference.Combine(HostAutoSdkDir, "Win64", "Windows Kits", "NETFXSDK", "4.6");
				if (FileReference.Exists(FileReference.Combine(NetFxDir_4_6, "Include", "um", "mscoree.h")))
				{
					OutInstallDir = NetFxDir_4_6;
					return true;
				}

				DirectoryReference NetFxDir_4_6_1 = DirectoryReference.Combine(HostAutoSdkDir, "Win64", "Windows Kits", "NETFXSDK", "4.6.1");
				if (FileReference.Exists(FileReference.Combine(NetFxDir_4_6_1, "Include", "um", "mscoree.h")))
				{
					OutInstallDir = NetFxDir_4_6_1;
					return true;
				}
			}

			string NetFxSDKKeyName = "Microsoft\\Microsoft SDKs\\NETFXSDK";
			string[] PreferredVersions = new string[] { "4.6.2", "4.6.1", "4.6" };
			foreach (string PreferredVersion in PreferredVersions)
			{
				if (TryReadInstallDirRegistryKey32(NetFxSDKKeyName + "\\" + PreferredVersion, "KitsInstallationFolder", out OutInstallDir))
				{
					return true;
				}
			}

			// If we didn't find one of our preferred versions for NetFXSDK, use the max version present on the system
			Version MaxVersion       = null;
			string  MaxVersionString = null;

			foreach (string ExistingVersionString in ReadInstallDirSubKeys32(NetFxSDKKeyName))
			{
				if (!Version.TryParse(ExistingVersionString, out Version ExistingVersion))
				{
					continue;
				}

				if (MaxVersion == null || 0 < ExistingVersion.CompareTo(MaxVersion))
				{
					MaxVersion       = ExistingVersion;
					MaxVersionString = ExistingVersionString;
				}
			}

			if (MaxVersionString != null)
			{
				return TryReadInstallDirRegistryKey32(NetFxSDKKeyName + "\\" + MaxVersionString, "KitsInstallationFolder", out OutInstallDir);
			}

			OutInstallDir = null;
			return false;
		}

		// Determines if the given name is a build product for a target.
		// <param name="FileName">The name to check</param>
		// <param name="NamePrefixes">Target or application names that may appear at the start of the build product name (eg. "Editor", "ShooterGameEditor")</param>
		// <param name="NameSuffixes">Suffixes which may appear at the end of the build product name</param>
		// <returns>True if the string matches the name of a build product, false otherwise</returns>
		public override bool IsBuildProduct(string FileName, string[] NamePrefixes, string[] NameSuffixes)
		=>  IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".exe")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dll")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dll.response") || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".lib")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".pdb")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".exp")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".obj")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".map")          || 
			IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".objpaths");

		// Get the extension to use for the given binary type
		public override string GetBinaryExtension(BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
					return ".dll";
				case BuildBinaryType.Executable:
					return ".exe";
				case BuildBinaryType.StaticLibrary:
					return ".lib";
			}
			return base.GetBinaryExtension(InBinaryType);
		}

		// Get the extensions to use for debug info for the given binary type
		public override string[] GetDebugInfoExtensions(ReadOnlyTargetRules Target, BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
				case BuildBinaryType.Executable:
					return new string[] {".pdb"};
			}
			return new string [] {};
		}

		public override bool HasDefaultBuildConfig(BuildTargetPlatform Platform, DirectoryReference ProjectPath)
		{
			if (Platform == BuildTargetPlatform.Win32)
			{
				string[] StringKeys = new string[] { "MinimumOSVersion" };

				// look up OS specific settings
				if (!DoProjectSettingsMatchDefault(Platform, ProjectPath, "/Script/WindowsTargetPlatform.WindowsTargetSettings",
					null, null, StringKeys))
				{
					return false;
				}
			}

			// check the base settings
			return base.HasDefaultBuildConfig(Platform, ProjectPath);
		}

		// Gets the application icon for a given project
		public static FileReference GetApplicationIcon(FileReference ProjectFile)
		{
			// Check if there's a custom icon
			if(ProjectFile != null)
			{
				FileReference IconFile = FileReference.Combine(ProjectFile.Directory, "Build", "Windows", "Application.ico");
				if(FileReference.Exists(IconFile))
				{
					return IconFile;
				}
			}

			// Otherwise use the default
			return FileReference.Combine(BuildTool.EngineDirectory, "Build", "Windows", "Resources", "Default.ico");
		}

		// Modify the rules for a newly created module, in a target that's being built for this platform.
		// This is not required - but allows for hiding details of a particular platform.
		public override void ModifyModuleRulesForActivePlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules InTargetRules)
		{
			bool bBuildShaderFormats = InTargetRules.bForceBuildShaderFormats;

			if (!InTargetRules.bBuildRequiresCookedData)
			{
				if (ModuleName == "TargetPlatform")
				{
					bBuildShaderFormats = true;
				}
			}

			// Allow standalone tools to use target platform modules, without needing Engine
			if (ModuleName == "TargetPlatform")
			{
				if (InTargetRules.bForceBuildTargetPlatforms)
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("WindowsTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("WindowsNoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("WindowsServerTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("WindowsClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("AllDesktopTargetPlatform");
				}

				if (bBuildShaderFormats)
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatD3D");
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatOpenGL");
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatVectorVM");

					InModuleRules.DynamicallyLoadedModuleNames.Remove("VulkanRHI");
					InModuleRules.DynamicallyLoadedModuleNames.Add("VulkanShaderFormat");
				}
			}

			if (ModuleName == "D3D11RHI")
			{
				// To enable platform specific D3D11 RHI Types
				InModuleRules.PrivateIncludePaths.Add("Runtime/Windows/D3D11RHI/Private/Windows");
			}

			if (ModuleName == "D3D12RHI")
			{
				if (InTargetRules.WindowsPlatform.bPixProfilingEnabled && InTargetRules.Platform != BuildTargetPlatform.Win32 && InTargetRules.Configuration != TargetConfiguration.Shipping)
				{
					// Define to indicate profiling enabled (64-bit only)
					InModuleRules.PublicDefinitions.Add("D3D12_PROFILING_ENABLED=1");
					InModuleRules.PublicDefinitions.Add("PROFILE");
					InModuleRules.PublicDependencyModuleNames.Add("WinPixEventRuntime");
				}
				else
				{
					InModuleRules.PublicDefinitions.Add("D3D12_PROFILING_ENABLED=0");
				}
			}

			// Delay-load D3D12 so we can use the latest features and still run on downlevel versions of the OS
			InModuleRules.PublicDelayLoadDLLs.Add("d3d12.dll");
		}

		// Setup the target environment for building
		public override void SetUpEnvironment(ReadOnlyTargetRules Target, CppCompileEnvironment CompileEnvironment, LinkEnvironment LinkEnvironment)
		{
			// @todo Remove this hack to work around broken includes
			CompileEnvironment.Definitions.Add("NDIS_MINIPORT_MAJOR_VERSION=0");

			CompileEnvironment.Definitions.Add("WIN32=1");
			if (Target.WindowsPlatform.bUseWindowsSDK10)
			{
				CompileEnvironment.Definitions.Add(String.Format("_WIN32_WINNT=0x{0:X4}", 0x0602));
				CompileEnvironment.Definitions.Add(String.Format("WINVER=0x{0:X4}", 0x0602));

			}
			else
			{
				CompileEnvironment.Definitions.Add(String.Format("_WIN32_WINNT=0x{0:X4}", Target.WindowsPlatform.TargetWindowsVersion));
				CompileEnvironment.Definitions.Add(String.Format("WINVER=0x{0:X4}", Target.WindowsPlatform.TargetWindowsVersion));
			}
			
			CompileEnvironment.Definitions.Add("PLATFORM_WINDOWS=1");
			CompileEnvironment.Definitions.Add("PLATFORM_MICROSOFT=1");

			// both Win32 and Win64 use Windows headers, so we enforce that here
			CompileEnvironment.Definitions.Add(Tag.CppContents.Def.OverridePlatformHeaderName + "=" + GetPlatformName()));

			// Ray tracing only supported on 64-bit Windows.
			if (Target.Platform == BuildTargetPlatform.Win64 && 
				Target.WindowsPlatform.bEnableRayTracing)
			{
				CompileEnvironment.Definitions.Add("RHI_RAYTRACING=1");
			}

			// Add path to Intel math libraries when using ICL based on target platform
			if (Target.WindowsPlatform.Compiler == WindowsCompiler.Intel)
			{
				string Result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "IntelSWTools", "compilers_and_libraries", "windows", "compiler", "lib", Target.Platform == BuildTargetPlatform.Win32 ? "ia32" : "intel64");
				if (!Directory.Exists(Result))
				{
					throw new BuildException("ICL was selected but the required math libraries were not found.  Could not find: " + Result);
				}

				LinkEnvironment.LibraryPaths.Add(new DirectoryReference(Result));
			}

			// Explicitly exclude the MS C++ runtime libraries we're not using,
			// to ensure other libraries we link with use the same runtime library as the engine.
			bool bUseDebugCRT = 
				Target.Configuration == TargetConfiguration.Debug && 
				Target.bDebugBuildsActuallyUseDebugCRT;

			if (!Target.bUseStaticCRT || bUseDebugCRT)
			{
				LinkEnvironment.ExcludedLibraries.Add("LIBCMT");  // MultiThreaded, static link /MT _MT
				LinkEnvironment.ExcludedLibraries.Add("LIBCPMT"); // ""
			}
			if (!Target.bUseStaticCRT || !bUseDebugCRT)
			{
				LinkEnvironment.ExcludedLibraries.Add("LIBCMTD");  // Mulithreaded, static link(debug) /MTd _DEBUG, _MT
				LinkEnvironment.ExcludedLibraries.Add("LIBCPMTD"); // ""
			}
			if (Target.bUseStaticCRT || bUseDebugCRT)
			{
				LinkEnvironment.ExcludedLibraries.Add("MSVCRT");  // Mulithreaded, dynmaic link(import library for MSVCR80.dll)
				LinkEnvironment.ExcludedLibraries.Add("MSVCPRT"); // ""
			}
			if (Target.bUseStaticCRT || !bUseDebugCRT)
			{
				LinkEnvironment.ExcludedLibraries.Add("MSVCRTD");  // Mulithreaded, dynmaic link(import library for MSVCR80.dll) (Debug)
				LinkEnvironment.ExcludedLibraries.Add("MSVCPRTD"); // Mulithreaded, dynmaic link(import library for MSVCP80D.dll)
			}

			LinkEnvironment.ExcludedLibraries.Add("LIBC");
			LinkEnvironment.ExcludedLibraries.Add("LIBCP");
			LinkEnvironment.ExcludedLibraries.Add("LIBCD");
			LinkEnvironment.ExcludedLibraries.Add("LIBCPD");

			//@todo ATL: Currently, only VSAccessor requires ATL (which is only used in editor builds)
			// When compiling games, we do not want to include ATL - and we can't when compiling games
			// made with Launcher build due to VS 2012 Express not including ATL.
			// If more modules end up requiring ATL, this should be refactored into a BuildTarget flag (bNeedsATL)
			// that is set by the modules the target includes to allow for easier tracking.
			// Alternatively, if VSAccessor is modified to not require ATL than we should always exclude the libraries.
			if (Target.LinkType == TargetLinkType.Monolithic &&
				(Target.Type == TargetType.Game || Target.Type == TargetType.Client || Target.Type == TargetType.Server))
			{
				LinkEnvironment.ExcludedLibraries.Add("atl");
				LinkEnvironment.ExcludedLibraries.Add("atls");
				LinkEnvironment.ExcludedLibraries.Add("atlsd");
				LinkEnvironment.ExcludedLibraries.Add("atlsn");
				LinkEnvironment.ExcludedLibraries.Add("atlsnd");
			}

			// Add the library used for the delayed loading of DLLs.
			LinkEnvironment.AdditionalLibraries.Add("delayimp.lib");

			//@todo - remove once FB implementation uses Http module
			if (Target.bCompileAgainstEngine)
			{
				// link against wininet (used by FBX and Facebook)
				LinkEnvironment.AdditionalLibraries.Add("wininet.lib");
			}

			// Compile and link with Win32 API libraries.
			LinkEnvironment.AdditionalLibraries.Add("rpcrt4.lib");
			//LinkEnvironment.AdditionalLibraries.Add("wsock32.lib");
			LinkEnvironment.AdditionalLibraries.Add("ws2_32.lib");
			LinkEnvironment.AdditionalLibraries.Add("dbghelp.lib");
			LinkEnvironment.AdditionalLibraries.Add("comctl32.lib");
			LinkEnvironment.AdditionalLibraries.Add("Winmm.lib");
			LinkEnvironment.AdditionalLibraries.Add("kernel32.lib");
			LinkEnvironment.AdditionalLibraries.Add("user32.lib");
			LinkEnvironment.AdditionalLibraries.Add("gdi32.lib");
			LinkEnvironment.AdditionalLibraries.Add("winspool.lib");
			LinkEnvironment.AdditionalLibraries.Add("comdlg32.lib");
			LinkEnvironment.AdditionalLibraries.Add("advapi32.lib");
			LinkEnvironment.AdditionalLibraries.Add("shell32.lib");
			LinkEnvironment.AdditionalLibraries.Add("ole32.lib");
			LinkEnvironment.AdditionalLibraries.Add("oleaut32.lib");
			LinkEnvironment.AdditionalLibraries.Add("uuid.lib");
			LinkEnvironment.AdditionalLibraries.Add("odbc32.lib");
			LinkEnvironment.AdditionalLibraries.Add("odbccp32.lib");
			LinkEnvironment.AdditionalLibraries.Add("netapi32.lib");
			LinkEnvironment.AdditionalLibraries.Add("iphlpapi.lib");
			LinkEnvironment.AdditionalLibraries.Add("setupapi.lib"); //  Required for access monitor device enumeration

			// Windows Vista/7 Desktop Windows Manager API for Slate Windows Compliance
			LinkEnvironment.AdditionalLibraries.Add("dwmapi.lib");

			// IME
			LinkEnvironment.AdditionalLibraries.Add("imm32.lib");

			// For 64-bit builds, we'll forcibly ignore a linker warning with DirectInput.  This is
			// Microsoft's recommended solution as they don't have a fixed .lib for us.
			if (Target.Platform != BuildTargetPlatform.Win32)
			{
				LinkEnvironment.AdditionalArguments += " /ignore:4078";
			}

			// Set up default stack size
			LinkEnvironment.DefaultStackSize       = Target.WindowsPlatform.DefaultStackSize;
			LinkEnvironment.DefaultStackSizeCommit = Target.WindowsPlatform.DefaultStackSizeCommit;
			LinkEnvironment.ModuleDefinitionFile   = Target.WindowsPlatform.ModuleDefinitionFile;
		}

		// Setup the configuration environment for building
		public override void SetUpConfigurationEnvironment(ReadOnlyTargetRules Target, CppCompileEnvironment GlobalCompileEnvironment, LinkEnvironment GlobalLinkEnvironment)
		{
			base.SetUpConfigurationEnvironment(Target, GlobalCompileEnvironment, GlobalLinkEnvironment);

			// NOTE: Even when debug info is turned off, we currently force the linker to generate debug info anyway on Visual C++ platforms.
			//       This will cause a PDB file to be generated with symbols for most of the classes and function/method names,
			//       so that crashes still yield somewhat useful call stacks, even though compiler-generate debug info may be disabled.
			//       This gives us much of the build-time savings of fully-disabled debug info, without giving up call data completely.
			GlobalLinkEnvironment.bCreateDebugInfo = true;
		}

		// Whether this platform should create debug information or not
		public override bool ShouldCreateDebugInfo(ReadOnlyTargetRules Target)
		{
			switch (Target.Configuration)
			{
				case TargetConfiguration.Development:
				case TargetConfiguration.Shipping:
				case TargetConfiguration.Test:
					return !Target.bOmitPCDebugInfoInDevelopment;
				case TargetConfiguration.DebugGame:
				case TargetConfiguration.Debug:
				default:
					return true;
			};
		}

		// Creates a toolchain instance for the given platform.
		public override ToolChain CreateToolChain(ReadOnlyTargetRules Target)
		{
			if (Target.WindowsPlatform.StaticAnalyzer == WindowsStaticAnalyzer.PVSStudio)
			{
				return new PVSToolChain(Target);
			}
			else
			{
				return new VCToolChain(Target);
			}
		}

		// Allows the platform to return various build metadata that is not tracked by other means.
		// If the returned string changes, the makefile will be invalidated.
		public override void GetExternalBuildMetadata(FileReference ProjectFile, StringBuilder Metadata)
		{
			base.GetExternalBuildMetadata(ProjectFile, Metadata);

			if(ProjectFile != null)
			{
				Metadata.AppendLine("ICON: {0}", GetApplicationIcon(ProjectFile));
			}
		}
	}

	class WindowsPlatformSDK : BuildPlatformSDK
	{
		protected override SDKStatus HasRequiredManualSDKInternal()
		{
			return SDKStatus.Valid;
		}
	}

	class WindowsPlatformFactory : BuildPlatformFactory
	{
		public override BuildTargetPlatform TargetPlatform => BuildTargetPlatform.Win64;

		// Register the platform with the UEBuildPlatform class
		public override void RegisterBuildPlatforms()
		{
			WindowsPlatformSDK SDK = new WindowsPlatformSDK();
			SDK.ManageAndValidateSDK();

			// Register this build platform for both Win64 and Win32
			BuildPlatform.RegisterBuildPlatform(new WindowsPlatform(BuildTargetPlatform.Win64, SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win64, BuildPlatformGroup.Windows);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win64, BuildPlatformGroup.Microsoft);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win64, BuildPlatformGroup.Desktop);

			BuildPlatform.RegisterBuildPlatform(new WindowsPlatform(BuildTargetPlatform.Win32, SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win32, BuildPlatformGroup.Windows);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win32, BuildPlatformGroup.Microsoft);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Win32, BuildPlatformGroup.Desktop);
		}
	}
#pragma warning restore IDE1006 // Naming Styles
}
