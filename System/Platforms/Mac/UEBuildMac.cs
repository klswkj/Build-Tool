using System;
using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// Mac-specific target settings
	public class MacTargetRules
	{
		// Whether to generate dSYM files.
		// Lists Architectures that you want to build.
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bGeneratedSYMFile")]
		public bool bGenerateDsymFile = true;

		// Enables address sanitizer (ASan).
		[CommandLine("-EnableASan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableAddressSanitizer")]
		public bool bEnableAddressSanitizer = false;

		// Enables thread sanitizer (TSan).
		[CommandLine("-EnableTSan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableThreadSanitizer")]
		public bool bEnableThreadSanitizer = false;

		// Enables undefined behavior sanitizer (UBSan).
		[CommandLine("-EnableUBSan")]
		[XMLConfigFile(Category = "BuildConfiguration", Name = "bEnableUndefinedBehaviorSanitizer")]
		public bool bEnableUndefinedBehaviorSanitizer = false;
	}

	// Read-only wrapper for Mac-specific target settings
	public class ReadOnlyMacTargetRules
	{
		private readonly MacTargetRules Inner;

		public ReadOnlyMacTargetRules(MacTargetRules Inner)
		{
			this.Inner = Inner;
		}


		// Accessors for fields on the inner TargetRules instance
		#region Read-only accessor properties 
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS1591

#pragma warning disable IDE1006 // Naming Styles
		public bool bGenerateDsymFile
			=> Inner.bGenerateDsymFile;

		public bool bEnableAddressSanitizer 
			=> Inner.bEnableAddressSanitizer;

		public bool bEnableThreadSanitizer
		=> Inner.bEnableThreadSanitizer;

		public bool bEnableUndefinedBehaviorSanitizer
		=> Inner.bEnableUndefinedBehaviorSanitizer;

#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore CS1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
		#endregion
	}

	class MacPlatform : BuildPlatform
	{
		private readonly MacPlatformSDK SDK;

		public MacPlatform(MacPlatformSDK InSDK) : base(BuildTargetPlatform.Mac)
		{
			SDK = InSDK;
		}

        public override SDKStatus HasRequiredSDKsInstalled()
        {
			return SDK.HasRequiredSDKsInstalled();
        }

        public override string GetDefaultArchitecture(FileReference Architecture)
		{
			return "";
		}

		public override bool CanUseXGE()
		{
			return false;
		}

		public override bool CanUseDistcc()
		{
			return true;
		}

		public override void ValidateTarget(TargetRules Target)
		{
			if (BuildHostPlatform.Current.Platform != BuildTargetPlatform.Mac)
			{
				// @todo: Temporarily disable precompiled header files when building remotely due to errors
				Target.bUsePCHFiles = false;
			}

			// Needs OS X 10.11 for Metal. The remote toolchain has not been initialized yet, so just assume it's a recent SDK.
			if ((BuildHostPlatform.Current.Platform != BuildTargetPlatform.Mac || 10.11f <= MacToolChain.Settings.MacOSSDKVersionFloat)
				&& Target.bCompileAgainstEngine)
			{
				Target.GlobalDefinitions.Add("HAS_METAL=1");
				Target.ExtraModuleNames.Add("MetalRHI");
			}
			else
			{
				Target.GlobalDefinitions.Add("HAS_METAL=0");
			}

			// Force using the ANSI allocator if ASan is enabled
			string AddressSanitizer = Environment.GetEnvironmentVariable("ENABLE_ADDRESS_SANITIZER");
			if(Target.MacPlatform.bEnableAddressSanitizer || 
				(AddressSanitizer != null && AddressSanitizer == "YES"))
			{
				Target.GlobalDefinitions.Add("FORCE_ANSI_ALLOCATOR=1");
			}

			Target.bUsePDBFiles =
				!Target.bDisableDebugInfo                                &&
				 Target.Configuration != TargetConfiguration.Debug &&
				 Target.MacPlatform.bGenerateDsymFile                    &&
			     Platform == BuildTargetPlatform.Mac;

			// we always deploy - the build machines need to be able to copy the files back, which needs the full bundle
			Target.bDeployAfterCompile = true;

			Target.bCheckSystemHeadersForModification = BuildHostPlatform.Current.Platform != BuildTargetPlatform.Mac;

			Target.bCompileISPC = true;
		}

		// Determines if the given name is a build product for a target.
		public override bool IsBuildProduct(string FileName, string[] NamePrefixes, string[] NameSuffixes)
		{
			return IsBuildProductName(FileName, NamePrefixes, NameSuffixes, "")       ||
				   IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dsym")  ||
				   IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dylib") ||
				   IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".a")     ||
				   IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".app");
		}

		// Get the extension to use for the given binary type
		public override string GetBinaryExtension(BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
					return ".dylib";
				case BuildBinaryType.Executable:
					return "";
				case BuildBinaryType.StaticLibrary:
					return ".a";
			}
			return base.GetBinaryExtension(InBinaryType);
		}

		
		// Get the extensions to use for debug info for the given binary type
		
		// <param name="Target">Rules for the target being built</param>
		// <param name="InBinaryType"> The binary type being built</param>
		// <returns>string[]    The debug info extensions (i.e. 'pdb')</returns>
		public override string[] GetDebugInfoExtensions(ReadOnlyTargetRules Target, BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
				case BuildBinaryType.Executable:
					return Target.bUsePDBFiles ? new string[] {".dSYM"} : new string[] {};
				case BuildBinaryType.StaticLibrary:
				default:
					return new string [] {};
			}
		}

		public override DirectoryReference GetBundleDirectory(ReadOnlyTargetRules Rules, List<FileReference> OutputFiles)
		{
			return Rules.bIsBuildingConsoleApplication ? null : OutputFiles[0].Directory.ParentDirectory.ParentDirectory;
		}

		// For platforms that need to output multiple files per binary (ie Android "fat" binaries)
		// this will emit multiple paths. By default, it simply makes an array from the input
		public override List<FileReference> FinalizeBinaryPaths(FileReference BinaryName, FileReference ProjectFile, ReadOnlyTargetRules Target)
		{
			List<FileReference> BinaryPaths = new List<FileReference>();

			if (Target.bIsBuildingConsoleApplication || BinaryName.GetExtension().HasValue())
			{
				BinaryPaths.Add(BinaryName);
			}
			else
			{
				BinaryPaths.Add(new FileReference(BinaryName.FullName + Tag.Directory.AppFolder + BinaryName.GetFileName()));
			}

			return BinaryPaths;
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

			// allow standalone tools to use target platform modules, without needing Engine
			if (ModuleName == "TargetPlatform")
			{
				if (InTargetRules.bForceBuildTargetPlatforms)
				{
					InModuleRules.DynamicallyLoadedModuleNames.Add("MacTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("MacNoEditorTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("MacClientTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("MacServerTargetPlatform");
					InModuleRules.DynamicallyLoadedModuleNames.Add("AllDesktopTargetPlatform");
				}

				if (bBuildShaderFormats)
				{
					// Rules.DynamicallyLoadedModuleNames.Add("ShaderFormatD3D");
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatOpenGL");
					InModuleRules.DynamicallyLoadedModuleNames.Add("MetalShaderFormat");
					InModuleRules.DynamicallyLoadedModuleNames.Add("ShaderFormatVectorVM");

					InModuleRules.DynamicallyLoadedModuleNames.Remove("VulkanRHI");
					InModuleRules.DynamicallyLoadedModuleNames.Add("VulkanShaderFormat");
				}
			}
		}

		// Setup the target environment for building
		public override void SetUpEnvironment(ReadOnlyTargetRules TargetBeingCompiled, CppCompileEnvironment CompileEnvironment, LinkEnvironment LinkEnvironment)
		{
			CompileEnvironment.Definitions.Add("PLATFORM_MAC=1");
			CompileEnvironment.Definitions.Add("PLATFORM_APPLE=1");

			CompileEnvironment.Definitions.Add("WITH_TTS=0");
			CompileEnvironment.Definitions.Add("WITH_SPEECH_RECOGNITION=0");
		}

		// Whether this platform should create debug information or not
		public override bool ShouldCreateDebugInfo(ReadOnlyTargetRules Target)
		{
			return true;
		}

		// Creates a toolchain instance for the given platform.
		public override ToolChain CreateToolChain(ReadOnlyTargetRules Target)
		{
			MacToolChainOptions Options = MacToolChainOptions.None;

			string AddressSanitizer   = Environment.GetEnvironmentVariable("ENABLE_ADDRESS_SANITIZER");
			string ThreadSanitizer    = Environment.GetEnvironmentVariable("ENABLE_THREAD_SANITIZER");
			string UndefSanitizerMode = Environment.GetEnvironmentVariable("ENABLE_UNDEFINED_BEHAVIOR_SANITIZER");

			if(Target.MacPlatform.bEnableAddressSanitizer || 
				(AddressSanitizer != null && AddressSanitizer == "YES"))
			{
				Options |= MacToolChainOptions.EnableAddressSanitizer;
			}
			if(Target.MacPlatform.bEnableThreadSanitizer || 
				(ThreadSanitizer != null && ThreadSanitizer == "YES"))
			{
				Options |= MacToolChainOptions.EnableThreadSanitizer;
			}
			if(Target.MacPlatform.bEnableUndefinedBehaviorSanitizer || 
				(UndefSanitizerMode != null && UndefSanitizerMode == "YES"))
			{
				Options |= MacToolChainOptions.EnableUndefinedBehaviorSanitizer;
			}
			if(Target.bShouldCompileAsDLL)
			{
				Options |= MacToolChainOptions.OutputDylib;
			}
			return new MacToolChain(Target.ProjectFile, Options);
		}

		// Deploys the given target
		public override void Deploy(TargetReceipt Receipt)
		{
			new UEDeployMac().PrepTargetForDeployment(Receipt);
		}
	}

	class MacPlatformSDK : BuildPlatformSDK
	{
		protected override SDKStatus HasRequiredManualSDKInternal()
		{
			return SDKStatus.Valid;
		}
	}

	class MacPlatformFactory : BuildPlatformFactory
	{
		public override BuildTargetPlatform TargetPlatform => BuildTargetPlatform.Mac;

		// Register the platform with the BuildPlatform class
		public override void RegisterBuildPlatforms()
		{
			MacPlatformSDK SDK = new MacPlatformSDK();
			SDK.ManageAndValidateSDK();

			// Register this build platform for Mac
			BuildPlatform.RegisterBuildPlatform(new MacPlatform(SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Mac, BuildPlatformGroup.Apple);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.Mac, BuildPlatformGroup.Desktop);
		}
	}
}
