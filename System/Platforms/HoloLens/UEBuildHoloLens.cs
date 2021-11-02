using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// HoloLens-specific target settings
	public sealed class HoloLensTargetRules
	{
		// Version of the compiler toolchain to use on HoloLens. A value of "default" will be changed to a specific version at UBT startup.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "CompilerVersion")]
		[XMLConfigFile(Category = "HoloLensPlatform")]
		[CommandLine("-2015", Value = "VisualStudio2015")]
		[CommandLine("-2017", Value = "VisualStudio2017")]
		[CommandLine("-2019", Value = "VisualStudio2019")]
		public WindowsCompiler Compiler = WindowsCompiler.Default;

		public WindowsArchitecture Architecture;

		// Enable PIX debugging (automatically disabled in Shipping and Test configs)
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "bEnablePIXProfiling")]
		public bool bPixProfilingEnabled = true;

		// Version of the compiler toolchain to use on HoloLens. A value of "default" will be changed to a specific version at UBT startup.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "bBuildForRetailWindowsStore")]
		public bool bBuildForRetailWindowsStore = false;

		// Contains the specific version of the Windows 10 SDK that we will build against. If empty, it will be "Latest"
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "Windows10SDKVersion")]
		public string Win10SDKVersionString = null;

		internal Version Win10SDKVersion = null;

		// Automatically increment the project version after each build.
		[ConfigFile(ConfigHierarchyType.Engine, "/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "bAutoIncrementVersion")]
		public bool bAutoIncrementVersion = false;

		public HoloLensTargetRules(TargetInfo Info)
		{
			if (Info.Platform == BuildTargetPlatform.HoloLens && !String.IsNullOrEmpty(Info.Architecture))
			{
				Architecture = (WindowsArchitecture)Enum.Parse(typeof(WindowsArchitecture), Info.Architecture, true);
			}
		}
	}

	// Read-only wrapper for HoloLens-specific target settings
	public class ReadOnlyHoloLensTargetRules
	{
		// The private mutable settings object
		private readonly HoloLensTargetRules Inner;

		public ReadOnlyHoloLensTargetRules(HoloLensTargetRules Inner)
		{
			this.Inner = Inner;
		}

		// Accessors for fields on the inner TargetRules instance
		#region Read-only accessor properties 
#pragma warning disable IDE1006 // Naming Styles
#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS1591
#endif
		public WindowsCompiler Compiler => Inner.Compiler;

		public WindowsArchitecture Architecture => Inner.Architecture;

		public bool bPixProfilingEnabled => Inner.bPixProfilingEnabled;

		public bool bBuildForRetailWindowsStore => Inner.bBuildForRetailWindowsStore;

		public Version Win10SDKVersion => Inner.Win10SDKVersion;

		public string Win10SDKVersionString => Inner.Win10SDKVersionString;
#if !__MonoCS__
#pragma warning restore CS1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif
#pragma warning restore IDE1006 // Naming Styles
		#endregion
	}

	class HoloLens : BuildPlatform
	{
		public static readonly Version MinimumSDKVersionRecommended = new Version(10, 0, 17763, 0);
		public static readonly Version MaximumSDKVersionTested = new Version(10, 0, 18362, int.MaxValue);
		public static readonly Version MaximumSDKVersionForVS2015 = new Version(10, 0, 14393, int.MaxValue);
		public static readonly Version MinimumSDKVersionForD3D12RHI = new Version(10, 0, 15063, 0);

		private readonly HoloLensPlatformSDK SDK;

		public HoloLens(BuildTargetPlatform InPlatform, HoloLensPlatformSDK InSDK) : base(InPlatform)
		{
			SDK = InSDK;
		}

		public override SDKStatus HasRequiredSDKsInstalled()
		{ 
			return SDK.HasRequiredSDKsInstalled();
		}

		public override void ValidateTarget(TargetRules Target)
		{
			DirectoryReference IniDirRef = DirectoryReference.FromFile(Target.ProjectFile);

			if (IniDirRef == null && BuildTool.GetRemoteIniPath().HasValue())
			{
				IniDirRef = new DirectoryReference(BuildTool.GetRemoteIniPath());
			}

			// Stash the current compiler choice (accounts for command line) in case ReadSettings reverts it to default
			WindowsCompiler CompilerBeforeReadSettings = Target.HoloLensPlatform.Compiler;

			ConfigCache.ReadSettings(IniDirRef, Platform, Target.HoloLensPlatform);

			if (Target.HoloLensPlatform.Compiler == WindowsCompiler.Default)
			{
				Target.HoloLensPlatform.Compiler = (CompilerBeforeReadSettings != WindowsCompiler.Default) ? 
					CompilerBeforeReadSettings : WindowsPlatform.GetDefaultCompiler(Target.ProjectFile);
			}

			if(!Target.bGenerateProjectFiles)
			{
				Log.TraceInformationOnce("Using {0} architecture for deploying to HoloLens device", Target.HoloLensPlatform.Architecture);
			}

			Target.WindowsPlatform.Compiler             = Target.HoloLensPlatform.Compiler;
			Target.WindowsPlatform.Architecture         = Target.HoloLensPlatform.Architecture;
			Target.WindowsPlatform.bPixProfilingEnabled = Target.HoloLensPlatform.bPixProfilingEnabled;
			Target.WindowsPlatform.bUseWindowsSDK10     = true;
			Target.bDeployAfterCompile                  = true;
			Target.bCompileNvCloth                      = false; // requires CUDA

			// Disable Simplygon support if compiling against the NULL RHI.
			if (Target.GlobalDefinitions.Contains("USE_NULL_RHI=1"))
			{
				Target.bCompileSpeedTree = false;
			}

			// Use shipping binaries to avoid dependency on nvToolsExt which fails WACK.
			if (Target.Configuration == TargetConfiguration.Shipping)
			{
				Target.bUseShippingPhysXLibraries = true;
			}

			// Be resilient to SDKs being uninstalled but still referenced in the INI file
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			if (!WindowsPlatform.TryGetWindowsSdkDir(Target.HoloLensPlatform.Win10SDKVersionString, out VersionNumber SelectedWindowsSdkVersion, out DirectoryReference SelectedWindowsSdkDir))
#pragma warning restore IDE0059 // Unnecessary assignment of a value
			{
				Target.HoloLensPlatform.Win10SDKVersionString = "Latest";
			}

			// Initialize the VC environment for the target, and set all the version numbers to the concrete values we chose.
			VCEnvironment Environment = VCEnvironment.Create(Target.WindowsPlatform.Compiler, Platform, Target.WindowsPlatform.Architecture, Target.WindowsPlatform.CompilerVersion, Target.HoloLensPlatform.Win10SDKVersionString, null);
			
			Target.WindowsPlatform.Environment       = Environment;
			Target.WindowsPlatform.Compiler          = Environment.Compiler;
			Target.WindowsPlatform.CompilerVersion   = Environment.CompilerVersion.ToString();
			Target.WindowsPlatform.WindowsSdkVersion = Environment.WindowsSdkVersion.ToString();

			// Windows 10 SDK version
			// Auto-detect latest compatible by default (recommended), allow for explicit override if necessary
			// Validate that the SDK isn't too old, and that the combination of VS and SDK is supported.
			Target.HoloLensPlatform.Win10SDKVersion = new Version(Environment.WindowsSdkVersion.ToString());

			if(!Target.bGenerateProjectFiles)
			{
				Log.TraceInformationOnce("Building using Windows SDK version {0} for HoloLens", Target.HoloLensPlatform.Win10SDKVersion);

				if (Target.HoloLensPlatform.Win10SDKVersion < MinimumSDKVersionRecommended)
				{
					Log.TraceWarning("Your Windows SDK version {0} is older than the minimum recommended version ({1}) for HoloLens.  Consider upgrading.", Target.HoloLensPlatform.Win10SDKVersion, MinimumSDKVersionRecommended);
				}
				else if (Target.HoloLensPlatform.Win10SDKVersion > MaximumSDKVersionTested)
				{
					Log.TraceInformationOnce("Your Windows SDK version ({0}) for HoloLens is newer than the highest tested with this version of UBT ({1}).  This is probably fine, but if you encounter issues consider using an earlier SDK.", Target.HoloLensPlatform.Win10SDKVersion, MaximumSDKVersionTested);
				}
			}

			HoloLensExports.InitWindowsSdkToolPath(Target.HoloLensPlatform.Win10SDKVersion.ToString());
		}

		public static string ArchitectureName = WindowsArchitecture.x64.ToString();

		// Gets the default HoloLens architecture
		public override string GetDefaultArchitecture(FileReference ProjectFile)
		{
			return HoloLens.ArchitectureName;
		}

		
		// Determines if the given name is a build product for a target.
		
		// <param name="FileName">The name to check</param>
		// <param name="NamePrefixes">Target or application names that may appear at the start of the build product name (eg. "Editor", "ShooterGameEditor")</param>
		// <param name="NameSuffixes">Suffixes which may appear at the end of the build product name</param>
		// <returns>True if the string matches the name of a build product, false otherwise</returns>
		public override bool IsBuildProduct(string FileName, string[] NamePrefixes, string[] NameSuffixes)
		{
			return IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".exe")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dll")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".dll.response")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".lib")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".pdb")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".exp")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".obj")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".map")
				|| IsBuildProductName(FileName, NamePrefixes, NameSuffixes, ".objpaths");
		}

		
		// Get the extension to use for the given binary type
		
		// <param name="InBinaryType"> The binrary type being built</param>
		// <returns>string	The binary extenstion (ie 'exe' or 'dll')</returns>
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
				case BuildBinaryType.Object:
					return ".obj";
				case BuildBinaryType.PrecompiledHeader:
					return ".pch";
			}
			return base.GetBinaryExtension(InBinaryType);
		}

		
		// Get the extension to use for debug info for the given binary type
		
		// <param name="Target">The target being built</param>
		// <param name="InBinaryType"> The binary type being built</param>
		// <returns>string	The debug info extension (i.e. 'pdb')</returns>
		public override string[] GetDebugInfoExtensions(ReadOnlyTargetRules Target, BuildBinaryType InBinaryType)
		{
			switch (InBinaryType)
			{
				case BuildBinaryType.DynamicLinkLibrary:
				case BuildBinaryType.Executable:
					return new string[] { ".pdb" };
			}
			return new string[] { "" };
		}

		internal static DirectoryReference GetCppCXMetadataLocation(WindowsCompiler Compiler, string CompilerVersion)
		{
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			if (!WindowsPlatform.TryGetToolChainDir(Compiler, CompilerVersion, out VersionNumber SelectedToolChainVersion, out DirectoryReference SelectedToolChainDir))

			{
				return null;
			}

			return GetCppCXMetadataLocation(Compiler, SelectedToolChainDir);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
		}

		public static DirectoryReference GetCppCXMetadataLocation(WindowsCompiler Compiler, DirectoryReference SelectedToolChainDir)
		{
			if (Compiler == WindowsCompiler.VisualStudio2015_DEPRECATED)
			{
				return DirectoryReference.Combine(SelectedToolChainDir, "lib", "store", "references");
			}
			else if (Compiler >= WindowsCompiler.VisualStudio2017)
			{
				return DirectoryReference.Combine(SelectedToolChainDir, "lib", "x86", "Store", "references");
			}
			else if (Compiler >= WindowsCompiler.VisualStudio2019)
			{
				return DirectoryReference.Combine(SelectedToolChainDir, "lib", "x86", "Store", "references");
			}
			else
			{
				return null;
			}
		}


		private static Version FindLatestVersionDirectory(string InDirectory, Version NoLaterThan)
		{
			Version LatestVersion = new Version(0, 0, 0, 0);
			if (Directory.Exists(InDirectory))
			{
				string[] VersionDirectories = Directory.GetDirectories(InDirectory);
				foreach (string Dir in VersionDirectories)
				{
					string VersionString = Path.GetFileName(Dir);
					if (Version.TryParse(VersionString, out Version FoundVersion) && 
						LatestVersion < FoundVersion)
					{
						if (NoLaterThan == null || 
							FoundVersion <= NoLaterThan)
						{
							LatestVersion = FoundVersion;
						}
					}
				}
			}
			return LatestVersion;
		}

		internal static string GetLatestMetadataPathForApiContract(string ApiContract)
		{
			if (!WindowsPlatform.TryGetWindowsSdkDir("Latest", out VersionNumber SDKVersion, out DirectoryReference SDKFolder))
			{
				return string.Empty;
			}

			DirectoryReference ReferenceDir = DirectoryReference.Combine(SDKFolder, "References");
			if (DirectoryReference.Exists(ReferenceDir))
			{
				// Prefer a contract from a suitable SDK-versioned subdir of the references folder when available (starts with 15063 SDK)
				// Version WindowsSDKVersionMaxForToolchain = Compiler < WindowsCompiler.VisualStudio2017 ? HoloLens.MaximumSDKVersionForVS2015 : null;
				DirectoryReference SDKVersionedReferenceDir = DirectoryReference.Combine(ReferenceDir, SDKVersion.ToString());
				DirectoryReference ContractDir              = DirectoryReference.Combine(SDKVersionedReferenceDir, ApiContract);
				FileReference MetadataFileRef = null;
				Version ContractLatestVersion;
				if (DirectoryReference.Exists(ContractDir))
				{
					// Note: contract versions don't line up with Windows SDK versions (they're numbered independently as 1.0.0.0, 2.0.0.0, etc.)
					ContractLatestVersion = FindLatestVersionDirectory(ContractDir.FullName, null);
					MetadataFileRef = FileReference.Combine(ContractDir, ContractLatestVersion.ToString(), ApiContract + ".winmd");
				}

				// Retry in unversioned references dir if we failed above.
				if (MetadataFileRef == null || !FileReference.Exists(MetadataFileRef))
				{
					ContractDir = DirectoryReference.Combine(ReferenceDir, ApiContract);
					if (DirectoryReference.Exists(ContractDir))
					{
						ContractLatestVersion = FindLatestVersionDirectory(ContractDir.FullName, null);
						MetadataFileRef = FileReference.Combine(ContractDir, ContractLatestVersion.ToString(), ApiContract + ".winmd");
					}
				}
				if (MetadataFileRef != null && FileReference.Exists(MetadataFileRef))
				{
					return MetadataFileRef.FullName;
				}
			}

			return string.Empty;
		}

		// Modify the rules for a newly created module, where the target is a different host platform.
		// This is not required - but allows for hiding details of a particular platform.
		public override void ModifyModuleRulesForOtherPlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules TargetBeingBuild)
		{
			// This code has been removed because it causes a full rebuild after generating project files (since response files are overwritten with different defines).
#if false
			if (Target.Platform == BuildTargetPlatform.Win64)
			{
				if (ProjectFileGenerator.bGenerateProjectFiles)
				{
					// Use latest SDK for Intellisense purposes
					WindowsCompiler CompilerForSdkRestriction = Target.HoloLensPlatform.Compiler != WindowsCompiler.Default ? Target.HoloLensPlatform.Compiler : Target.WindowsPlatform.Compiler;
					if (CompilerForSdkRestriction != WindowsCompiler.Default)
					{
						Version OutWin10SDKVersion;
						DirectoryReference OutSdkDir;
						if(WindowsExports.TryGetWindowsSdkDir(Target.HoloLensPlatform.Win10SDKVersionString, out OutWin10SDKVersion, out OutSdkDir))
						{
							Rules.PublicDefinitions.Add(string.Format("WIN10_SDK_VERSION={0}", OutWin10SDKVersion.Build));
						}
					}
				}
			}
#endif
		}

		// Deploys the given target
		public override void Deploy(TargetReceipt ReceiptBeingDeployed)
		{
			new HoloLensDeploy().PrepTargetForDeployment(ReceiptBeingDeployed);
		}

		
		// Modify the rules for a newly created module, in a target that's being built for this platform.
		// This is not required - but allows for hiding details of a particular platform.
		public override void ModifyModuleRulesForActivePlatform(string ModuleName, ModuleRules InModuleRules, ReadOnlyTargetRules TargetBeingBuild)
		{
			if (ModuleName == "Core")
			{
				//Rules.PrivateDependencyModuleNames.Add("HoloLensSDK");
			}
			else if (ModuleName == "Engine")
			{
				InModuleRules.PrivateDependencyModuleNames.Add("zlib");
				InModuleRules.PrivateDependencyModuleNames.Add("UElibPNG");
				InModuleRules.PublicDependencyModuleNames.Add("UEOgg");
				InModuleRules.PublicDependencyModuleNames.Add("Vorbis");
			}
			else if (ModuleName == "D3D11RHI")
			{
				InModuleRules.PublicDefinitions.Add("D3D11_WITH_DWMAPI=0");
				InModuleRules.PublicDefinitions.Add("WITH_DX_PERF=0");
			}
			else if (ModuleName == "D3D12RHI")
			{
				// To enable platform specific D3D12 RHI Types
				InModuleRules.PrivateIncludePaths.Add("Runtime/D3D12RHI/Private/HoloLens");
			}
			else if (ModuleName == "DX11")
			{
				// Clear out all the Windows include paths and libraries...
				// The HoloLensSDK module handles proper paths and libs for HoloLens.
				// However, the D3D11RHI module will include the DX11 module.
				InModuleRules.PublicIncludePaths.Clear();
				InModuleRules.PublicSystemLibraryPaths.Clear();
				InModuleRules.PublicSystemLibraries.Clear();
				InModuleRules.PublicAdditionalLibraries.Clear();
				InModuleRules.PublicDefinitions.Remove("WITH_D3DX_LIBS=1");
				InModuleRules.PublicDefinitions.Add("WITH_D3DX_LIBS=0");
				InModuleRules.PublicAdditionalLibraries.Remove("X3DAudio.lib");
				InModuleRules.PublicAdditionalLibraries.Remove("XAPOFX.lib");
			}
			else if (ModuleName == "XAudio2")
			{
				InModuleRules.PublicDefinitions.Add("XAUDIO_SUPPORTS_XMA2WAVEFORMATEX=0");
				InModuleRules.PublicDefinitions.Add("XAUDIO_SUPPORTS_DEVICE_DETAILS=0");
				InModuleRules.PublicDefinitions.Add("XAUDIO2_SUPPORTS_MUSIC=0");
				InModuleRules.PublicDefinitions.Add("XAUDIO2_SUPPORTS_SENDLIST=1");
				InModuleRules.PublicSystemLibraries.Add("XAudio2.lib");
			}
			else if (ModuleName == "DX11Audio")
			{
				InModuleRules.PublicAdditionalLibraries.Remove("X3DAudio.lib");
				InModuleRules.PublicAdditionalLibraries.Remove("XAPOFX.lib");
			}
		}

		private void ExpandWinMDReferences(ReadOnlyTargetRules Target, string SDKFolder, string SDKVersion, ref List<string> WinMDReferences)
		{
			// Code below will fail when not using the Win10 SDK.  Early out to avoid warning spam.
			if (!Target.WindowsPlatform.bUseWindowsSDK10)
			{
				return;
			}

			if (0 < WinMDReferences.Count)
			{
				// Allow bringing in Windows SDK contracts just by naming the contract
				// These are files that look like References/10.0.98765.0/AMadeUpWindowsApiContract/5.0.0.0/AMadeUpWindowsApiContract.winmd
				List<string> ExpandedWinMDReferences = new List<string>();

				// The first few releases of the Windows 10 SDK didn't put the SDK version in the reference path
				string ReferenceRoot = Path.Combine(SDKFolder, "References");
				string VersionedReferenceRoot = Path.Combine(ReferenceRoot, SDKVersion);
				if (Directory.Exists(VersionedReferenceRoot))
				{
					ReferenceRoot = VersionedReferenceRoot;
				}

				foreach (string WinMDRef in WinMDReferences)
				{
					if (File.Exists(WinMDRef))
					{
						// Already a valid path
						ExpandedWinMDReferences.Add(WinMDRef);
					}
					else
					{
						string ContractFolder = Path.Combine(ReferenceRoot, WinMDRef);

						Version ContractVersion = FindLatestVersionDirectory(ContractFolder, null);
						string ExpandedWinMDRef = Path.Combine(ContractFolder, ContractVersion.ToString(), WinMDRef + ".winmd");
						if (File.Exists(ExpandedWinMDRef))
						{
							ExpandedWinMDReferences.Add(ExpandedWinMDRef);
						}
						else
						{
							Log.TraceWarning("Unable to resolve location for HoloLens WinMD api contract {0}, file {1}", WinMDRef, ExpandedWinMDRef);
						}
					}
				}

				WinMDReferences = ExpandedWinMDReferences;
			}
		}

		// Setup the target environment for building
		public override void SetUpEnvironment(ReadOnlyTargetRules TargetBeingCompiled, CppCompileEnvironment InCPPCompileEnvironment, LinkEnvironment InLinkEnvironment)
		{
			// Add Win10 SDK pieces - moved here since it allows better control over SDK version
			string Win10SDKRoot = TargetBeingCompiled.WindowsPlatform.WindowsSdkDir;

			// Include paths
			InCPPCompileEnvironment.SystemIncludePaths.Add(new DirectoryReference(string.Format(@"{0}\Include\{1}\ucrt",   Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion)));
			InCPPCompileEnvironment.SystemIncludePaths.Add(new DirectoryReference(string.Format(@"{0}\Include\{1}\um",     Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion)));
			InCPPCompileEnvironment.SystemIncludePaths.Add(new DirectoryReference(string.Format(@"{0}\Include\{1}\shared", Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion)));
			InCPPCompileEnvironment.SystemIncludePaths.Add(new DirectoryReference(string.Format(@"{0}\Include\{1}\winrt",  Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion)));

			// Library paths
			// @MIXEDREALITY_CHANGE : BEGIN TODO: change to arm.
			string LibArchitecture = WindowsExports.GetArchitectureSubpath(TargetBeingCompiled.HoloLensPlatform.Architecture);
			InLinkEnvironment.LibraryPaths.Add(new DirectoryReference(string.Format(@"{0}\Lib\{1}\ucrt\{2}", Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion, LibArchitecture)));
			InLinkEnvironment.LibraryPaths.Add(new DirectoryReference(string.Format(@"{0}\Lib\{1}\um\{2}",   Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion, LibArchitecture)));

			// Reference (WinMD) paths
			// Only Foundation and Universal are referenced by default.  
			List<string> AlwaysReferenceContracts = new List<string>
			{
				"Windows.Foundation.FoundationContract",
				"Windows.Foundation.UniversalApiContract"
			};

			ExpandWinMDReferences(TargetBeingCompiled, Win10SDKRoot, TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion.ToString(), ref AlwaysReferenceContracts);

			StringBuilder WinMDReferenceArguments = new StringBuilder();
			foreach (string WinMDReference in AlwaysReferenceContracts)
			{
				WinMDReferenceArguments.AppendFormat(@" /FU""{0}""", WinMDReference);
			}
			InCPPCompileEnvironment.AdditionalArguments += WinMDReferenceArguments;

			InCPPCompileEnvironment.Definitions.Add("EXCEPTIONS_DISABLED=0");

			InCPPCompileEnvironment.Definitions.Add("_WIN32_WINNT=0x0A00");
			InCPPCompileEnvironment.Definitions.Add("WINVER=0x0A00");

			InCPPCompileEnvironment.Definitions.Add("PLATFORM_HOLOLENS=1");
			InCPPCompileEnvironment.Definitions.Add("HOLOLENS=1");

			InCPPCompileEnvironment.Definitions.Add("WINAPI_FAMILY=WINAPI_FAMILY_APP");
			InCPPCompileEnvironment.Definitions.Add("PLATFORM_MICROSOFT=1");

			// No D3DX on HoloLens!
			InCPPCompileEnvironment.Definitions.Add("NO_D3DX_LIBS=1");

			if (TargetBeingCompiled.HoloLensPlatform.bBuildForRetailWindowsStore)
			{
				InCPPCompileEnvironment.Definitions.Add("USING_RETAIL_WINDOWS_STORE=1");
			}
			else
			{
				InCPPCompileEnvironment.Definitions.Add("USING_RETAIL_WINDOWS_STORE=0");
			}

			InCPPCompileEnvironment.Definitions.Add("WITH_D3D12_RHI=0");

			InLinkEnvironment.AdditionalArguments += "/NODEFAULTLIB";
			//CompileEnvironment.AdditionalArguments += " /showIncludes";

			InLinkEnvironment.AdditionalLibraries.Add("windowsapp.lib");

			InCPPCompileEnvironment.Definitions.Add(string.Format("WIN10_SDK_VERSION={0}", TargetBeingCompiled.HoloLensPlatform.Win10SDKVersion.Build));

			InLinkEnvironment.AdditionalLibraries.Add("dloadhelper.lib");
			InLinkEnvironment.AdditionalLibraries.Add("ws2_32.lib");

			if (InCPPCompileEnvironment.bUseDebugCRT)
			{
				InLinkEnvironment.AdditionalLibraries.Add("vccorlibd.lib");
				InLinkEnvironment.AdditionalLibraries.Add("ucrtd.lib");
				InLinkEnvironment.AdditionalLibraries.Add("vcruntimed.lib");
				InLinkEnvironment.AdditionalLibraries.Add("msvcrtd.lib");
				InLinkEnvironment.AdditionalLibraries.Add("msvcprtd.lib");
			}
			else
			{
				InLinkEnvironment.AdditionalLibraries.Add("vccorlib.lib");
				InLinkEnvironment.AdditionalLibraries.Add("ucrt.lib");
				InLinkEnvironment.AdditionalLibraries.Add("vcruntime.lib");
				InLinkEnvironment.AdditionalLibraries.Add("msvcrt.lib");
				InLinkEnvironment.AdditionalLibraries.Add("msvcprt.lib");
			}
			InLinkEnvironment.AdditionalLibraries.Add("legacy_stdio_wide_specifiers.lib");
			InLinkEnvironment.AdditionalLibraries.Add("uuid.lib"); 
		}

		// Setup the configuration environment for building
		public override void SetUpConfigurationEnvironment(ReadOnlyTargetRules TargetBeingBuilt, CppCompileEnvironment GlobalCompileEnvironment, LinkEnvironment GlobalLinkEnvironment)
		{
			// Determine the C++ compile/link configuration based on the Build configuration.

			if (GlobalCompileEnvironment.bUseDebugCRT)
			{
				GlobalCompileEnvironment.Definitions.Add("_DEBUG=1"); // the engine doesn't use this, but lots of 3rd party stuff does
			}
			else
			{
				GlobalCompileEnvironment.Definitions.Add("NDEBUG=1"); // the engine doesn't use this, but lots of 3rd party stuff does
			}

			//CppConfiguration CompileConfiguration;
			TargetConfiguration CheckConfig = TargetBeingBuilt.Configuration;
			switch (CheckConfig)
			{
				default:
				case TargetConfiguration.Debug:
					GlobalCompileEnvironment.Definitions.Add("UE_BUILD_DEBUG=1");
					break;
				case TargetConfiguration.DebugGame:
				// Default to Development; can be overriden by individual modules.
				case TargetConfiguration.Development:
					GlobalCompileEnvironment.Definitions.Add("UE_BUILD_DEVELOPMENT=1");
					break;
				case TargetConfiguration.Shipping:
					GlobalCompileEnvironment.Definitions.Add("UE_BUILD_SHIPPING=1");
					break;
				case TargetConfiguration.Test:
					GlobalCompileEnvironment.Definitions.Add("UE_BUILD_TEST=1");
					break;
			}

			// Create debug info based on the heuristics specified by the user.
			GlobalCompileEnvironment.bCreateDebugInfo = !TargetBeingBuilt.bDisableDebugInfo && ShouldCreateDebugInfo(TargetBeingBuilt);

			// NOTE: Even when debug info is turned off, we currently force the linker to generate debug info
			//	   anyway on Visual C++ platforms.  This will cause a PDB file to be generated with symbols
			//	   for most of the classes and function/method names, so that crashes still yield somewhat
			//	   useful call stacks, even though compiler-generate debug info may be disabled.  This gives
			//	   us much of the build-time savings of fully-disabled debug info, without giving up call
			//	   data completely.
			GlobalLinkEnvironment.bCreateDebugInfo = true;
		}

		// Whether this platform should create debug information or not
		public override bool ShouldCreateDebugInfo(ReadOnlyTargetRules TargetBeingBuilt)
		{
			switch (TargetBeingBuilt.Configuration)
			{
				case TargetConfiguration.Development:
				case TargetConfiguration.Shipping:
				case TargetConfiguration.Test:
					return !TargetBeingBuilt.bOmitPCDebugInfoInDevelopment;
				case TargetConfiguration.DebugGame:
				case TargetConfiguration.Debug:
				default:
					return true;
			};
		}

		// Creates a toolchain instance for the given platform.
		public override ToolChain CreateToolChain(ReadOnlyTargetRules TargetBeingBuilt)
		{
			return new HoloLensToolChain(TargetBeingBuilt);
		}
	} // End HoloLens

	class HoloLensPlatformSDK : BuildPlatformSDK
	{
		private static readonly bool bIsInstalled = false;
		//static string LatestVersionString = string.Empty;
		//static string InstallLocation = string.Empty;

		static HoloLensPlatformSDK()
		{
#if !__MonoCS__
			if (Utils.IsRunningOnMono)
			{
				return;
			}

			string Version = "v10.0";
			string[] possibleRegLocations =
			{
				@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\",
				@"HKEY_CURRENT_USER\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\"
			};
			foreach (string regLocation in possibleRegLocations)
			{
				object Result = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\" + Version, "InstallationFolder", null);

				if (Result != null)
				{
					bIsInstalled = true;
					//InstallLocation = (string)Result;
					//LatestVersionString = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\" + Version, "ProductVersion", null) as string;
					break;
				}
			}
#endif
		}

		protected override SDKStatus HasRequiredManualSDKInternal()
		{
			return (!Utils.IsRunningOnMono && bIsInstalled) ? SDKStatus.Valid : SDKStatus.Invalid;
		}
	} // End HoloLensPlatformSDK

	class HoloLensPlatformFactory : BuildPlatformFactory
	{
		public override BuildTargetPlatform TargetPlatform => BuildTargetPlatform.HoloLens;

		// Register the platform with the UEBuildPlatform class
		public override void RegisterBuildPlatforms()
		{
			HoloLensPlatformSDK SDK = new HoloLensPlatformSDK();
			SDK.ManageAndValidateSDK();

			BuildPlatform.RegisterBuildPlatform(new HoloLens(BuildTargetPlatform.HoloLens, SDK));
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.HoloLens, BuildPlatformGroup.Microsoft);
			BuildPlatform.RegisterPlatformWithGroup(BuildTargetPlatform.HoloLens, BuildPlatformGroup.HoloLens);
		}
	}
}
