using System.Collections.Generic;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// Base class for platform-specific project generators
	internal sealed class HoloLensProjectGenerator : PlatformProjectGenerator
	{
		public HoloLensProjectGenerator(CommandLineArguments Arguments)
			: base(Arguments)
		{
		}

		// Enumerate all the platforms that this generator supports
		public override IEnumerable<BuildTargetPlatform> GetPlatforms()
		{
			yield return BuildTargetPlatform.HoloLens;
		}

		// VisualStudio project generation functions
		// Whether this build platform has native support for VisualStudio
		public override bool HasVisualStudioSupport(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, VCProjectFileFormat ProjectFileFormat) 
			=> true;

		// Get whether this platform deploys
		public override bool GetVisualStudioDeploymentEnabled(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration) 
			=> true;

		public override void GenerateGameProperties(TargetConfiguration Configuration, StringBuilder VCProjectFileContent, TargetType TargetType, DirectoryReference RootDirectory, FileReference TargetFilePath)
		{
			string MinVersion = string.Empty;
			string MaxTestedVersion = string.Empty;
			ConfigHierarchy EngineIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, RootDirectory, BuildTargetPlatform.HoloLens);
			if (EngineIni != null)
			{
				EngineIni.GetString("/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "MinimumPlatformVersion", out MinVersion);
				EngineIni.GetString("/Script/HoloLensPlatformEditor.HoloLensTargetSettings", "MaximumPlatformVersionTested", out MaxTestedVersion);
			}
			if (MinVersion.HasValue())
			{
				VCProjectFileContent.Append("		<WindowsTargetPlatformMinVersion>" + MinVersion + "</WindowsTargetPlatformMinVersion>" + ProjectFileGenerator.NewLine);
			}
			if (MaxTestedVersion.HasValue())
			{
				VCProjectFileContent.Append("		<WindowsTargetPlatformVersion>" + MaxTestedVersion + "</WindowsTargetPlatformVersion>" + ProjectFileGenerator.NewLine);
			}

			WindowsCompiler Compiler = WindowsPlatform.GetDefaultCompiler(TargetFilePath);
			DirectoryReference PlatformWinMDLocation = HoloLens.GetCppCXMetadataLocation(Compiler, "Latest");
			if (PlatformWinMDLocation == null || !FileReference.Exists(FileReference.Combine(PlatformWinMDLocation, "platform.winmd")))
			{
				throw new BuildException("Unable to find platform.winmd for {0} toolchain; '{1}' is an invalid version", WindowsPlatform.GetCompilerName(Compiler), "Latest");
			}
			string FoundationWinMDPath = HoloLens.GetLatestMetadataPathForApiContract("Windows.Foundation.FoundationContract");
			string UniversalWinMDPath = HoloLens.GetLatestMetadataPathForApiContract("Windows.Foundation.UniversalApiContract");
			VCProjectFileContent.Append("		<AdditionalOptions>/ZW /ZW:nostdlib</AdditionalOptions>" + ProjectFileGenerator.NewLine);
			VCProjectFileContent.Append("		<NMakePreprocessorDefinitions>$(NMakePreprocessorDefinitions);PLATFORM_HOLOLENS=1;HOLOLENS=1;</NMakePreprocessorDefinitions>" + ProjectFileGenerator.NewLine);
			if (PlatformWinMDLocation != null)
			{
				VCProjectFileContent.Append("		<NMakeAssemblySearchPath>$(NMakeAssemblySearchPath);" + PlatformWinMDLocation + "</NMakeAssemblySearchPath>" + ProjectFileGenerator.NewLine);
			}
			VCProjectFileContent.Append("		<NMakeForcedUsingAssemblies>$(NMakeForcedUsingAssemblies);" + FoundationWinMDPath + ";" + UniversalWinMDPath + ";platform.winmd</NMakeForcedUsingAssemblies>" + ProjectFileGenerator.NewLine);
		}

		public override void GetVisualStudioPreDefaultString(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, StringBuilder VCProjectFileContent)
		{
			// VS2017 expects WindowsTargetPlatformVersion to be set in conjunction with these other properties, otherwise the projects
			// will fail to load when the solution is in a HoloLens configuration.
			// Default to latest supported version.  Game projects can override this later.
			// Because this property is only required for VS2017 we can safely say that's the compiler version (whether that's actually true or not)
			// string SDKFolder = "";
			string SDKVersion = "";

#pragma warning disable IDE0059 // Unnecessary assignment of a value
			if (WindowsPlatform.TryGetWindowsSdkDir("Latest", out VersionNumber SDKVersionNumber, out DirectoryReference OutSDKDir))
#pragma warning restore IDE0059 // Unnecessary assignment of a value
			{
				// SDKFolder = folder.FullName;
				SDKVersion = SDKVersionNumber.ToString();
			}

			VCProjectFileContent.AppendLine("		<AppContainerApplication>true</AppContainerApplication>");
			VCProjectFileContent.AppendLine("		<ApplicationType>Windows Store</ApplicationType>");
			VCProjectFileContent.AppendLine("		<ApplicationTypeRevision>10.0</ApplicationTypeRevision>");
			VCProjectFileContent.AppendLine("		<WindowsAppContainer>true</WindowsAppContainer>");
			VCProjectFileContent.AppendLine("		<AppxPackage>true</AppxPackage>");
			VCProjectFileContent.AppendLine("		<WindowsTargetPlatformVersion>{0}</WindowsTargetPlatformVersion>", SDKVersion.ToString()); 
		}

		public override string GetVisualStudioLayoutDirSection
		(
			BuildTargetPlatform InPlatform,
			TargetConfiguration InConfiguration,
			string InConditionString,
			TargetType TargetType,
			FileReference TargetRulesPath,
			FileReference ProjectFilePath,
			FileReference NMakeOutputPath,
			VCProjectFileFormat InProjectFileFormat
		)
		{
			string LayoutDirString = "";

			if (IsValidHoloLensTarget(InPlatform, TargetType, TargetRulesPath))
			{
				LayoutDirString += "	<PropertyGroup " + InConditionString + ">" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<RemoveExtraDeployFiles>false</RemoveExtraDeployFiles>" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<LayoutDir>" + DirectoryReference.Combine(NMakeOutputPath.Directory, "AppX").FullName + "</LayoutDir>" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<AppxPackageRecipe>" + FileReference.Combine(NMakeOutputPath.Directory, ProjectFilePath.GetFileNameWithoutExtension() + ".build.appxrecipe").FullName + "</AppxPackageRecipe>" + ProjectFileGenerator.NewLine;
				LayoutDirString += "	</PropertyGroup>" + ProjectFileGenerator.NewLine;

				// another hijack - this is a convenient point to make sure that HoloLens-appropriate debuggers are available
				// in the project property pages.
				LayoutDirString += "    <ItemGroup " + InConditionString + ">" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<PropertyPageSchema Include=\"$(VCTargetsPath)$(LangID)\\AppHostDebugger_Local.xml\" />" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<PropertyPageSchema Include=\"$(VCTargetsPath)$(LangID)\\AppHostDebugger_Simulator.xml\" />" + ProjectFileGenerator.NewLine;
				LayoutDirString += "		<PropertyPageSchema Include=\"$(VCTargetsPath)$(LangID)\\AppHostDebugger_Remote.xml\" />" + ProjectFileGenerator.NewLine;
				LayoutDirString += "    </ItemGroup>" + ProjectFileGenerator.NewLine;
			}

			return LayoutDirString;
		}

		// VisualStudio project generation functions
		// Return the VisualStudio platform name for this build platform
		public override string GetVisualStudioPlatformName(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration)
		{
			if (InPlatform == BuildTargetPlatform.HoloLens)
			{
				return "arm64";
			}

			return InPlatform.ToString();
		}

		private static bool IsValidHoloLensTarget(BuildTargetPlatform InPlatform, TargetType InTargetType, FileReference InTargetFilePath)
		{
			if ((InPlatform == BuildTargetPlatform.HoloLens) &&
				InTargetType != TargetRules.TargetType.Editor &&
				InTargetType != TargetRules.TargetType.Server &&
				(InTargetType == TargetRules.TargetType.Client || InTargetType == TargetRules.TargetType.Game))
			{
				// We do not want to include any Templates targets
				// Not a huge fan of doing it via path name comparisons... but it works
				string TempTargetFilePath = InTargetFilePath.FullName.Replace("\\", "/");
				if (TempTargetFilePath.Contains("/Templates/"))
				{
					string AbsoluteEnginePath = BuildTool.EngineDirectory.FullName;
					AbsoluteEnginePath = AbsoluteEnginePath.Replace("\\", "/");
					if (AbsoluteEnginePath.EndsWith("/") == false)
					{
						AbsoluteEnginePath += "/";
					}
					string CheckPath = AbsoluteEnginePath.Replace("/Engine/", "/Templates/");
					if (TempTargetFilePath.StartsWith(CheckPath))
					{
						return false;
					}
				}

				return true;
			}

			return false;
		}

		public override bool RequiresVSUserFileGeneration() => true;

		public override string GetVisualStudioUserFileStrings
		(
			BuildTargetPlatform InPlatform,
			TargetConfiguration InConfiguration,
			string InConditionString,
			TargetRules InTargetRules,
			FileReference TargetRulesPath,
			FileReference ProjectFilePath
		)
		{
			string UserFileEntry = "";
			if (IsValidHoloLensTarget(InPlatform, InTargetRules.Type, TargetRulesPath))
			{
				UserFileEntry += "<PropertyGroup " + InConditionString + ">\n";
				UserFileEntry += "	<DebuggerFlavor>AppHostLocalDebugger</DebuggerFlavor>\n";
				UserFileEntry += "</PropertyGroup>\n";
			}
			return UserFileEntry;
		}
	}
}

