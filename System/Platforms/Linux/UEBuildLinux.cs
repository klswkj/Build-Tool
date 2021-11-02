// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;
using BuildToolUtilities;
using System.Text.RegularExpressions;

namespace BuildTool
{
	// Architecture as stored in the ini.
	public enum LinuxArchitecture
	{
		X86_64UnknownLinuxGnu,      // x86_64, most commonly used architecture.
		ArmUnknownLinuxGnueabihf,   // A.k.a. AArch32, ARM 32-bit with hardware floats
		AArch64UnknownLinuxGnueabi, // AArch64, ARM 64-bit
		I686UnknownLinuxGnu         // i686, Intel 32-bit
	}

	// Linux-specific target settings
	public sealed class LinuxTargetRules
	{
		public LinuxTargetRules()
		{
			XMLConfig.ApplyTo(this);
		}

		// Enables address sanitizer (ASan)
		[CommandLine("-EnableASan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableAddressSanitizer")]
		public bool bEnableAddressSanitizer = false;

		// Enables thread sanitizer (TSan)
		[CommandLine("-EnableTSan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableThreadSanitizer")]
		public bool bEnableThreadSanitizer = false;

		// Enables undefined behavior sanitizer (UBSan)
		[CommandLine("-EnableUBSan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableUndefinedBehaviorSanitizer")]
		public bool bEnableUndefinedBehaviorSanitizer = false;

		// Enables memory sanitizer (MSan)
		[CommandLine("-EnableMSan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableMemorySanitizer")]
		public bool bEnableMemorySanitizer = false;

		// Enables "thin" LTO
		[CommandLine("-ThinLTO")]
		public bool bEnableThinLTO = false;

		// Whether or not to preserve the portable symbol file produced by dump_syms
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/LinuxPlatform.LinuxTargetSettings")]
		public bool bPreservePSYM = false;
	}

	// Read-only wrapper for Linux-specific target settings
	public class ReadOnlyLinuxTargetRules
	{
		private readonly LinuxTargetRules Inner;

		public ReadOnlyLinuxTargetRules(LinuxTargetRules Inner)
		{
			this.Inner = Inner;
		}

		// Accessors for fields on the inner TargetRules instance
		#region Read-only accessor properties 
#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS1591
#endif

#pragma warning disable IDE1006 // Naming Styles
		public bool bPreservePSYM => Inner.bPreservePSYM;

		public bool bEnableAddressSanitizer => Inner.bEnableAddressSanitizer;

		public bool bEnableThreadSanitizer => Inner.bEnableThreadSanitizer;

		public bool bEnableUndefinedBehaviorSanitizer => Inner.bEnableUndefinedBehaviorSanitizer;

		public bool bEnableMemorySanitizer => Inner.bEnableMemorySanitizer;

		public bool bEnableThinLTO => Inner.bEnableThinLTO;
#pragma warning restore IDE1006 // Naming Styles

#if !__MonoCS__
#pragma warning restore CS1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif
		#endregion
	}
#pragma warning restore IDE0079 // Remove unnecessary suppression

	class LinuxPlatform : BuildPlatform
	{
		// Linux host architecture (compiler target triplet)
		public const string DefaultHostArchitecture = "x86_64-unknown-linux-gnu";

		// SDK in use by the platform
		protected LinuxPlatformSDK SDK;

		public LinuxPlatform(LinuxPlatformSDK InSDK)
			: this(BuildTargetPlatform.Linux, InSDK)
		{
			SDK = InSDK;
		}

		public LinuxPlatform(BuildTargetPlatform InBuildTargetPlatform, LinuxPlatformSDK InSDK)
			: base(InBuildTargetPlatform)
		{
			SDK = InSDK;
		}

		// Whether the required external SDKs are installed for this platform.
		// Could be either a manual install or an AutoSDK.
		public override SDKStatus HasRequiredSDKsInstalled()
		{
			return SDK.HasRequiredSDKsInstalled();
		}

		// Find the default architecture for the given project
		public override string GetDefaultArchitecture(FileReference ProjectFile)
		{
			if (Platform == BuildTargetPlatform.LinuxAArch64)
			{
				return "aarch64-unknown-linux-gnueabi";
			}
			else
			{
				return "x86_64-unknown-linux-gnu";
			}
		}

		// Get name for architecture-specific directories (can be shorter than architecture name itself)
		public override string GetFolderNameForArchitecture(string Architecture)
		{
			// shorten the string (heuristically)
			uint Sum = 0;
			int Len = Architecture.Length;
			for (int Index = 0; Index < Len; ++Index)
			{
				Sum += (uint)(Architecture[Index]);
				Sum <<= 1;	// allowed to overflow
			}
			return Sum.ToString("X");
		}

		public override void ResetTarget(TargetRules Target)
		{
			ValidateTarget(Target);
		}

		public override void ValidateTarget(TargetRules Target)
		{
			if(Target.LinuxPlatform.bEnableThinLTO)
			{
				Target.bAllowLTCG = true;
			}

			if (Target.bAllowLTCG && Target.LinkType != TargetLinkType.Monolithic)
			{
				throw new BuildException("LTO (LTCG) for modular builds is not supported (lld is not currently used for dynamic libraries).");
			}

			// depends on arch, APEX cannot be as of November'16 compiled for AArch32/64
			Target.bCompileAPEX = Target.Architecture.StartsWith("x86_64");
			Target.bCompileNvCloth = Target.Architecture.StartsWith("x86_64");

			if (Target.GlobalDefinitions.Contains("USE_NULL_RHI=1"))
			{				
				Target.bCompileCEF3 = false;
			}

			// check if OS update invalidated our build
			Target.bCheckSystemHeadersForModification = (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux);

			Target.bCompileISPC = Target.Architecture.StartsWith("x86_64");
		}

		// Allows the platform to override whether the architecture name should be appended to the name of binaries.
		public override bool RequiresArchitectureSuffix()
		{
			return false; // Linux ignores architecture-specific names, although it might be worth it to prepend architecture
		}

		public override bool CanUseXGE()
		{
			// [RCL] 2018-05-02: disabling XGE even during a native build because the support is not ready and you can have mysterious build failures when ib_console is installed.
			// [RCL] 2018-07-10: enabling XGE for Windows to see if the crash from 2016 still persists. Please disable if you see spurious build errors that don't repro without XGE
			// [bschaefer] 2018-08-24: disabling XGE due to a bug where XGE seems to be lower casing folders names that are headers ie. misc/Header.h vs Misc/Header.h
			// [bschaefer] 2018-10-04: enabling XGE as an update in xgConsole seems to have fixed it for me
			// [bschaefer] 2018-12-17: disable XGE again, as the same issue before seems to still be happening but intermittently
			// [bschaefer] 2019-6-13: enable XGE, as the bug from before is now fixed
			return BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64;
		}

		public override bool CanUseParallelExecutor()
		{
			// No known problems with parallel executor, always use for build machines
			return true;
		}
		
		// Determines if the given name is a build product for a target.
		public override bool IsBuildProduct(string FileNameToCheck, string[] NamePrefixes, string[] NameSuffixes)
		{
			// NamePrefixes -> Editor, Developer, Debug, ....
			if (FileNameToCheck.StartsWith("lib"))
			{
				return IsBuildProductName(FileNameToCheck, 3, FileNameToCheck.Length - 3, NamePrefixes, NameSuffixes, ".a")   ||
					   IsBuildProductName(FileNameToCheck, 3, FileNameToCheck.Length - 3, NamePrefixes, NameSuffixes, ".so")  ||
					   IsBuildProductName(FileNameToCheck, 3, FileNameToCheck.Length - 3, NamePrefixes, NameSuffixes, ".sym") ||
					   IsBuildProductName(FileNameToCheck, 3, FileNameToCheck.Length - 3, NamePrefixes, NameSuffixes, ".debug");
			}
			else
			{
				return IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, "")     ||
					   IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, ".so")  ||
					   IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, ".a")   ||
					   IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, ".sym") ||
					   IsBuildProductName(FileNameToCheck, NamePrefixes, NameSuffixes, ".debug");
			}
		}

		// Get the extension to use for the given binary type
		public override string GetBinaryExtension(BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
					return ".so";
				case BuildBinaryType.Executable:
					return "";
				case BuildBinaryType.StaticLibrary:
					return ".a";
			}
			return base.GetBinaryExtension(InBinaryType);
		}

		// Get the extensions to use for debug info for the given binary type
		public override string[] GetDebugInfoExtensions(ReadOnlyTargetRules InTarget, BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
				case BuildBinaryType.Executable:
					if (InTarget.LinuxPlatform.bPreservePSYM)
					{
						return new string[] {".psym", ".sym", ".debug"};
					}
					else
					{
						return new string[] {".sym", ".debug"};
					}
			}
			return new string [] {};
		}
		
		// Modify the rules for a newly created module, where the target is a different host platform.
		// This is not required - but allows for hiding details of a particular platform.
		public override void ModifyModuleRulesForOtherPlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules Target)
		{
			// don't do any target platform stuff if SDK is not available
			if (!BuildPlatform.IsPlatformAvailable(Platform))
			{
				return;
			}

			if ((Target.Platform == BuildTargetPlatform.Win32) || 
				(Target.Platform == BuildTargetPlatform.Win64))
			{
				if (!Target.bBuildRequiresCookedData)
				{
					if (ModuleName == "Engine")
					{
						if (Target.bBuildDeveloperTools)
						{
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxNoEditorTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64NoEditorTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxClientTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ClientTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxServerTargetPlatform");
							InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ServerTargetPlatform");
						}
					}
				}

				// allow standalone tools to use targetplatform modules, without needing Engine
				if (Target.bForceBuildTargetPlatforms && ModuleName == "TargetPlatform")
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxNoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64NoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxServerTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ServerTargetPlatform");
				}
			}
		}
		
		// Modify the rules for a newly created module, in a target that's being built for this platform.
		// This is not required - but allows for hiding details of a particular platform.
		public override void ModifyModuleRulesForActivePlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules Target)
		{
			bool bBuildShaderFormats = Target.bForceBuildShaderFormats;

			if (!Target.bBuildRequiresCookedData)
			{
				if (ModuleName == "TargetPlatform")
				{
					bBuildShaderFormats = true;
				}
			}

			// allow standalone tools to use target platform modules, without needing Engine
			if (ModuleName == "TargetPlatform")
			{
				if (Target.bForceBuildTargetPlatforms)
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxNoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64NoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxServerTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("LinuxAArch64ServerTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("AllDesktopTargetPlatform");
				}

				if (bBuildShaderFormats)
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatOpenGL");
					InModuleRules.DynamicallyLoadedModuleNames.Add("VulkanShaderFormat");
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatVectorVM");
				}
			}
		}

		public virtual void SetUpSpecificEnvironment(ReadOnlyTargetRules InTargetRules, CppCompileEnvironment InCPPCompileEnvironment, LinkEnvironment InLinkEnvironment)
		{
			InCPPCompileEnvironment.Definitions.Add("PLATFORM_LINUX=1");
			InCPPCompileEnvironment.Definitions.Add("PLATFORM_UNIX=1");

			InCPPCompileEnvironment.Definitions.Add("LINUX=1"); // For libOGG

			// this define does not set jemalloc as default, just indicates its support
			InCPPCompileEnvironment.Definitions.Add("PLATFORM_SUPPORTS_JEMALLOC=1");

			// LinuxAArch64 uses only Linux header files
			InCPPCompileEnvironment.Definitions.Add("OVERRIDE_PLATFORM_HEADER_NAME=Linux");

			InCPPCompileEnvironment.Definitions.Add("PLATFORM_LINUXAARCH64=" +
				(InTargetRules.Platform == BuildTargetPlatform.LinuxAArch64 ? "1" : "0"));
		}
		
		// Setup the target environment for building
		public override void SetUpEnvironment(ReadOnlyTargetRules InTargetRules, CppCompileEnvironment InCPPCompileEnvironment, LinkEnvironment InLinkEnvironment)
		{
			// During the native builds, check the system includes as well (check toolchain when cross-compiling?)
			string BaseLinuxPath = SDK.GetBaseLinuxPathForArchitecture(InTargetRules.Architecture);
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux && String.IsNullOrEmpty(BaseLinuxPath))
			{
				InCPPCompileEnvironment.SystemIncludePaths.Add(new DirectoryReference("/usr/include"));
			}

			if (InCPPCompileEnvironment.bPGOOptimize != InLinkEnvironment.bPGOOptimize)
			{
				throw new BuildException
				(
					"Inconsistency between PGOOptimize settings in Compile ({0}) and Link ({1}) environments",
					InCPPCompileEnvironment.bPGOOptimize,
					InLinkEnvironment.bPGOOptimize
				);
			}

			if (InCPPCompileEnvironment.bPGOProfile != InLinkEnvironment.bPGOProfile)
			{
                throw new BuildException
                (
                    "Inconsistency between PGOProfile settings in Compile ({0}) and Link ({1}) environments",
                    InCPPCompileEnvironment.bPGOProfile,
                    InLinkEnvironment.bPGOProfile
                );
            }

			if (InCPPCompileEnvironment.bPGOOptimize)
			{
				DirectoryReference BaseDir = BuildTool.EngineDirectory;
				if (InTargetRules.ProjectFile != null)
				{
					BaseDir = DirectoryReference.FromFile(InTargetRules.ProjectFile);
				}

				InCPPCompileEnvironment.PGODirectory      = Path.Combine(BaseDir.FullName, "Build", InTargetRules.Platform.ToString(), "PGO").Replace('\\', '/') + "/";
				InCPPCompileEnvironment.PGOFilenamePrefix = "profile.profdata";

				InLinkEnvironment.PGODirectory      = InCPPCompileEnvironment.PGODirectory;
				InLinkEnvironment.PGOFilenamePrefix = InCPPCompileEnvironment.PGOFilenamePrefix;
			}

			// For consistency with other platforms, also enable LTO whenever doing profile-guided optimizations.
			// Obviously both PGI (instrumented) and PGO (optimized) binaries need to have that
			if (InCPPCompileEnvironment.bPGOProfile || 
				InCPPCompileEnvironment.bPGOOptimize)
			{
				InCPPCompileEnvironment.bAllowLTCG = true;
				InLinkEnvironment.bAllowLTCG = true;
			}

			if (InCPPCompileEnvironment.bAllowLTCG != InLinkEnvironment.bAllowLTCG)
			{
				throw new BuildException
				(
					"Inconsistency between LTCG settings in Compile ({0}) and Link ({1}) environments",
					InCPPCompileEnvironment.bAllowLTCG,
					InLinkEnvironment.bAllowLTCG
				);
			}

			// link with Linux libraries.
			InLinkEnvironment.AdditionalLibraries.Add("pthread");

			// let this class or a sub class do settings specific to that class
			SetUpSpecificEnvironment(InTargetRules, InCPPCompileEnvironment, InLinkEnvironment);
		}

		
		// Whether this platform should create debug information or not
		public override bool ShouldCreateDebugInfo(ReadOnlyTargetRules InTargetRules)
		{
			switch (InTargetRules.Configuration)
			{
				case TargetConfiguration.Development:
				case TargetConfiguration.Shipping:
				case TargetConfiguration.Test:
				case TargetConfiguration.Debug:
				default:
					return true;
			};
		}

		public override List<FileReference> FinalizeBinaryPaths(FileReference BinaryName, FileReference ProjectFile, ReadOnlyTargetRules Target)
		{
			List<FileReference> FinalBinaryPath = new List<FileReference>();

			string SanitizerSuffix = null;

			// Only append these for monolithic builds. non-monolithic runs into issues dealing with target/modules files
			if (Target.LinkType == TargetLinkType.Monolithic)
			{
				if(Target.LinuxPlatform.bEnableAddressSanitizer)
				{
					SanitizerSuffix = "ASan";
				}
				else if(Target.LinuxPlatform.bEnableThreadSanitizer)
				{
					SanitizerSuffix = "TSan";
				}
				else if(Target.LinuxPlatform.bEnableUndefinedBehaviorSanitizer)
				{
					SanitizerSuffix = "UBSan";
				}
				else if(Target.LinuxPlatform.bEnableMemorySanitizer)
				{
					SanitizerSuffix = "MSan";
				}
			}

			if (String.IsNullOrEmpty(SanitizerSuffix))
			{
				FinalBinaryPath.Add(BinaryName);
			}
			else
			{
				// Append the sanitizer suffix to the binary name but before the extension type
				FinalBinaryPath.Add(new FileReference(Path.Combine(BinaryName.Directory.FullName, BinaryName.GetFileNameWithoutExtension() + "-" + SanitizerSuffix + BinaryName.GetExtension())));
			}

			return FinalBinaryPath;
		}

		// Creates a toolchain instance for the given platform.
		public override ToolChain CreateToolChain(ReadOnlyTargetRules Target)
		{
			LinuxToolChainOptions Options = LinuxToolChainOptions.None;

			if(Target.LinuxPlatform.bEnableAddressSanitizer)
			{
				Options |= LinuxToolChainOptions.EnableAddressSanitizer;

				if (Target.LinkType != TargetLinkType.Monolithic)
				{
					Options |= LinuxToolChainOptions.EnableSharedSanitizer;
				}
			}

			if(Target.LinuxPlatform.bEnableThreadSanitizer)
			{
				Options |= LinuxToolChainOptions.EnableThreadSanitizer;

				if (Target.LinkType != TargetLinkType.Monolithic)
				{
					throw new BuildException("Thread Sanitizer (TSan) unsupported for non-monolithic builds");
				}
			}

			if(Target.LinuxPlatform.bEnableUndefinedBehaviorSanitizer)
			{
				Options |= LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer;

				if (Target.LinkType != TargetLinkType.Monolithic)
				{
					Options |= LinuxToolChainOptions.EnableSharedSanitizer;
				}
			}

			if(Target.LinuxPlatform.bEnableMemorySanitizer)
			{
				Options |= LinuxToolChainOptions.EnableMemorySanitizer;

				if (Target.LinkType != TargetLinkType.Monolithic)
				{
					throw new BuildException("Memory Sanitizer (MSan) unsupported for non-monolithic builds");
				}
			}

			if(Target.LinuxPlatform.bEnableThinLTO)
			{
				Options |= LinuxToolChainOptions.EnableThinLTO;
			}

			return new LinuxToolChain(Target.Architecture, SDK, Target.LinuxPlatform.bPreservePSYM, Options);
		}
	}

	internal sealed class LinuxPlatformSDK : BuildPlatformSDK
	{
		// This is the SDK version we support
		private static readonly string ExpectedSDKVersion = "v19_clang-11.0.1-centos7";	// now unified for all the architectures
		private static readonly string TargetPlatformName = "Linux_x64"; // Platform name (embeds architecture for now)

		private int bForceUseSystemCompiler = -1; // Force using system compiler and error out if not possible

		public bool bVerboseCompiler = false; // Whether to compile with the verbose flag

		public bool bVerboseLinker = false; // Whether to link with the verbose flag

		// Whether platform supports switching SDKs during runtime
		protected override bool PlatformSupportsAutoSDKs()
		{
			return true;
		}

		// Returns platform-specific name used in SDK repository
		public override string GetSDKTargetPlatformName()
		{
			return TargetPlatformName;
		}
		
		// Returns SDK string as required by the platform
		protected override string GetRequiredSDKString()
		{
			return ExpectedSDKVersion;
		}

		protected override String GetRequiredScriptVersionString()
		{
			return "3.0";
		}

		protected override bool PreferAutoSDK()
		{
			// having LINUX_ROOT set (for legacy reasons or for convenience of cross-compiling certain third party libs) should not make UBT skip AutoSDKs
			return true;
		}

		public string HaveLinuxDependenciesFile()
		{
			// This file must have no extension so that GitDeps considers it a binary dependency - it will only be pulled by the Setup script if Linux is enabled.
			return "HaveLinuxDependencies";
		}

		public string SDKVersionFileName()
		{
			return "ToolchainVersion.txt";
		}

		private static int GetLinuxToolchainVersionFromString(string SDKVersion)
		{
			// Example: v11_clang-5.0.0-centos7
			string FullVersionPattern = @"^v[0-9]+_.*$";
			Regex Regex = new Regex(FullVersionPattern);
			if (Regex.IsMatch(SDKVersion))
			{
				string VersionPattern = @"[0-9]+";
				Regex = new Regex(VersionPattern);
				Match Match = Regex.Match(SDKVersion);
				if (Match.Success)
				{
					bool bParsed = Int32.TryParse(Match.Value, out int Version);
					if (bParsed)
					{
						return Version;
					}
				}
			}

			return -1;
		}

		public bool CheckSDKCompatible(string VersionString, out string ErrorMessage)
		{
			int Version = GetLinuxToolchainVersionFromString(VersionString);
			int ExpectedVersion = GetLinuxToolchainVersionFromString(ExpectedSDKVersion);
			if (Version >= 0 && ExpectedVersion >= 0 && Version != ExpectedVersion)
			{
				if (Version < ExpectedVersion)
				{
					ErrorMessage = "Toolchain found \"" + VersionString + "\" is older then the required version \"" + ExpectedSDKVersion + "\"";
					return false;
				}
				else
				{
					Log.TraceWarning("Toolchain \"{0}\" is newer than the expected version \"{1}\", you may run into compilation errors", VersionString, ExpectedSDKVersion);
				}
			}
			else if (VersionString != ExpectedSDKVersion)
			{
				ErrorMessage = "Failed to find a supported toolchain, found \"" + VersionString + "\", expected \"" + ExpectedSDKVersion + "\"";
				return false;
			}

			ErrorMessage = "";
			return true;
		}

		// Returns the in-tree root for the Linux Toolchain for this host platform.
		private static DirectoryReference GetInTreeSDKRoot()
		{
			return DirectoryReference.Combine(BuildTool.RootDirectory, "Engine/Extras/ThirdPartyNotUE/SDKs", "Host" + BuildHostPlatform.Current.Platform, TargetPlatformName);
		}

		// Whether a host can use its system sdk for this platform
		public bool ForceUseSystemCompiler()
		{
			// by default tools chains don't parse arguments, but we want to be able to check the -bForceUseSystemCompiler flag.
			if (bForceUseSystemCompiler == -1)
			{
				bForceUseSystemCompiler = 0;
				string[] CmdLine = Environment.GetCommandLineArgs();

				foreach (string CmdLineArg in CmdLine)
				{
					if (CmdLineArg.Equals("-ForceUseSystemCompiler", StringComparison.OrdinalIgnoreCase))
					{
						bForceUseSystemCompiler = 1;
						break;
					}
				}
			}

			return bForceUseSystemCompiler == 1;
		}

		// Returns the root SDK path for all architectures
		// WARNING: Do not cache this value - it may be changed after sourcing OutputEnvVars.txt
		public string GetSDKLocation()
		{
			// if new multi-arch toolchain is used, prefer it
			string MultiArchRoot = Environment.GetEnvironmentVariable("LINUX_MULTIARCH_ROOT");

			if (String.IsNullOrEmpty(MultiArchRoot))
			{
				// check if in-tree SDK is available
				DirectoryReference InTreeSDKVersionRoot = GetInTreeSDKRoot();

				if (InTreeSDKVersionRoot != null)
				{
					DirectoryReference InTreeSDKVersionPath = DirectoryReference.Combine(InTreeSDKVersionRoot, ExpectedSDKVersion);
					if (DirectoryReference.Exists(InTreeSDKVersionPath))
					{
						MultiArchRoot = InTreeSDKVersionPath.FullName;
					}
				}
			}

			return MultiArchRoot;
		}

		// Returns the SDK path for a specific architecture
		// WARNING: Do not cache this value - it may be changed after sourcing OutputEnvVars.txt
		public string GetBaseLinuxPathForArchitecture(string Architecture)
		{
			// if new multi-arch toolchain is used, prefer it
			string MultiArchRoot = GetSDKLocation();
			string BaseLinuxPath;

			if (!String.IsNullOrEmpty(MultiArchRoot))
			{
				BaseLinuxPath = Path.Combine(MultiArchRoot, Architecture);
			}
			else
			{
				// use cross linux toolchain if LINUX_ROOT is specified
				BaseLinuxPath = Environment.GetEnvironmentVariable("LINUX_ROOT");
			} 
			return BaseLinuxPath;
		}

		// Whether the path contains a valid clang version
		private static bool IsValidClangPath(DirectoryReference BaseLinuxPath)
		{
			FileReference ClangPath = FileReference.Combine(BaseLinuxPath, @"bin", (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64) ? "clang++.exe" : "clang++");
			return FileReference.Exists(ClangPath);
		}

		// Whether the required external SDKs are installed for this platform
		protected override SDKStatus HasRequiredManualSDKInternal()
		{
			// FIXME: UBT should loop across all the architectures and compile for all the selected ones.

			// do not cache this value - it may be changed after sourcing OutputEnvVars.txt
			string BaseLinuxPath = GetBaseLinuxPathForArchitecture(LinuxPlatform.DefaultHostArchitecture);

			if (ForceUseSystemCompiler())
			{
				if (LinuxCommon.WhichClang().HasValue() || 
					LinuxCommon.WhichGcc().HasValue())
				{
					return SDKStatus.Valid;
				}
			}
			else if (BaseLinuxPath.HasValue())
			{
				// paths to our toolchains if BaseLinuxPath is specified
				BaseLinuxPath = BaseLinuxPath.Replace("\"", "");

				if (IsValidClangPath(new DirectoryReference(BaseLinuxPath)))
				{
					return SDKStatus.Valid;
				}
			}

			return SDKStatus.Invalid;
		}
	}

	internal sealed class LinuxPlatformFactory : BuildPlatformFactory
	{
        public override BuildTargetPlatform TargetPlatform => BuildTargetPlatform.Linux;

        // Register the platform with the UEBuildPlatform class
        public override void RegisterBuildPlatforms()
		{
			LinuxPlatformSDK SDK = new LinuxPlatformSDK();
			SDK.ManageAndValidateSDK();

			// Register this build platform for Linux x86-64 and AArch64
			BuildPlatform.RegisterBuildPlatform(new LinuxPlatform(BuildTargetPlatform.Linux, SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Linux, BuildPlatformGroup.Linux);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Linux, BuildPlatformGroup.Unix);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Linux, BuildPlatformGroup.Desktop);

			BuildPlatform.RegisterBuildPlatform(new LinuxPlatform(BuildTargetPlatform.LinuxAArch64, SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.LinuxAArch64, BuildPlatformGroup.Linux);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.LinuxAArch64, BuildPlatformGroup.Unix);
		}
	}
}
