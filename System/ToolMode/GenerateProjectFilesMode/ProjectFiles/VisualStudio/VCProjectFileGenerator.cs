using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Represents a folder within the master project (e.g. Visual Studio solution)
	class VisualStudioSolutionFolder : MasterProjectFolder
	{
		public VisualStudioSolutionFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
			: base(InitOwnerProjectFileGenerator, InitFolderName)
		{
		}
	}

	enum VCProjectFileFormat
	{
		Default,          // Default to the best installed version, but allow SDKs to override
		VisualStudio2012, // Unsupported ([Obsolete])
		VisualStudio2013, // Unsupported ([Obsolete])
		VisualStudio2015,
		VisualStudio2017,
		VisualStudio2019,
	}

	// Up-to-date Class (0 Reference)
	class VCProjectFileSettings
	{
		// The version of Visual Studio to generate project files for.
		[XMLConfigFile(Category = "VCProjectFileGenerator", Name = "Version")]
		public VCProjectFileFormat ProjectFileFormat = VCProjectFileFormat.Default;

		// Puts the most common include paths in the IncludePath property in the MSBuild project. 
		// This significantly reduces Visual Studio memory usage (measured 1.1GB -> 500mb), 
		// but seems to be causing issues with Visual Assist. Value here specifies maximum length of the include path list in KB.
		[XMLConfigFile(Category = "VCProjectFileGenerator")]
		public int MaxSharedIncludePaths = 24 * 1024;

		// Semi-colon separated list of paths that should not be added to the projects include paths. 
		// Useful for omitting third-party headers (e.g ThirdParty/WebRTC) from intellisense suggestions and reducing memory footprints.
		[XMLConfigFile(Category = "VCProjectFileGenerator")]
		public string ExcludedIncludePaths = "";

		// Whether to write a solution option (suo) file for the sln.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bWriteSolutionOptionFile = true;

		// Forces UBT to be built in debug configuration, regardless of the solution configuration
		[XMLConfigFile(Category = "VCProjectFileGenerator")]
		public bool bBuildUBTInDebug = false;

		// Whether to add the -FastPDB option to build command lines by default.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public bool bAddFastPDBToProjects = false;

		// Whether to generate per-file intellisense data.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public readonly bool bUsePerFileIntellisense = true;

		// Whether to include a dependency on ShaderCompileWorker when generating project files for the editor.
		[XMLConfigFile(Category = "BuildConfiguration")]
		public readonly bool bEditorDependsOnShaderCompileWorker = true;

        // Whether to include a dependency on LiveCodingConsole when building targets that support live coding.
		[XMLConfigFile(Category = "VCProjectFileGenerator")]
		public bool bBuildLiveCodingConsole = false;
	}

    // Visual C++ project file generator implementation
    internal class VCProjectFileGenerator : ProjectFileGenerator
	{
		// The version of Visual Studio to generate project files for.
		[XMLConfigFile(Name = "Version")]
		protected VCProjectFileFormat ProjectFileFormat = VCProjectFileFormat.Default;

		// Semi-colon separated list of paths that should not be added to the projects include paths. Useful for omitting third-party headers
		// (e.g ThirdParty/WebRTC) from intellisense suggestions and reducing memory footprints.
		[XMLConfigFile(Category = "VCProjectFileGenerator")]
		public string ExcludedIncludePaths = "";

		// Whether to write a solution option (suo) file for the sln.
		[XMLConfigFile(Category = "BuildConfiguration")]
		protected bool bWriteSolutionOptionFile = true;

		// Forces UBT to be built in debug configuration, regardless of the solution configuration
		[XMLConfigFile]
		protected bool bBuildUBTInDebug = false;

		// Whether to add the -FastPDB option to build command lines by default.
		[XMLConfigFile(Category = "BuildConfiguration")]
		protected readonly bool bAddFastPDBToProjects = false;
		
		// Whether to generate per-file intellisense data.
		[XMLConfigFile(Category = "BuildConfiguration")]
		protected readonly bool bUsePerFileIntellisense = true;

		// Whether to include a dependency on ShaderCompileWorker when generating project files for the editor.
		[XMLConfigFile(Category = "BuildConfiguration")]
		protected readonly bool bEditorDependsOnShaderCompileWorker = true;

		// Whether to include a dependency on LiveCodingConsole when building targets that support live coding.
		[XMLConfigFile]
		protected readonly bool bBuildLiveCodingConsole = false;

		// Override for the build tool to use in generated projects. If the compiler version is specified on the command line, we use the same argument on the 
		// command line for generated projects.
		protected readonly string BuildToolOverride;

		// <param name="InOnlyGameProject">The single project to generate project files for, or null</param>
		// <param name="InProjectFileFormat">Override the project file format to use</param>
		// <param name="InArguments">Additional command line arguments</param>
		public VCProjectFileGenerator
		(
            FileReference InOnlyGameProject, // be null
            VCProjectFileFormat  ProjectFileFormatToUse,
            CommandLineArguments AdditionalCommandLineArguments
		)
			: base(InOnlyGameProject)
		{
			XMLConfig.ApplyTo(this);

			if(ProjectFileFormatToUse != VCProjectFileFormat.Default)
			{
				ProjectFileFormat = ProjectFileFormatToUse;
			}

			if(AdditionalCommandLineArguments.HasOption(Tag.GlobalArgument.VS2015))
			{
				BuildToolOverride = Tag.GlobalArgument.VS2015;
			}
			else if(AdditionalCommandLineArguments.HasOption(Tag.GlobalArgument.VS2017))
			{
				BuildToolOverride = Tag.GlobalArgument.VS2017;
			}
			else if(AdditionalCommandLineArguments.HasOption(Tag.GlobalArgument.VS2019))
			{
				BuildToolOverride = Tag.GlobalArgument.VS2019;
			}
		}

        public override string[] GetTargetArguments(string[] Arguments) 
			=> Arguments.Where(s => string.Equals(s, BuildToolOverride, StringComparison.InvariantCultureIgnoreCase)).ToArray();

        // File extension for project files we'll be generating (e.g. ".vcxproj")
        public override string ProjectFileExtension => ".vcxproj";

        public override void CleanProjectFiles(DirectoryReference InMasterProjectDirectory, string InMasterProjectName, DirectoryReference InIntermediateProjectFilesDirectory)
		{
			FileReference MasterProjectFile = FileReference.Combine(InMasterProjectDirectory, InMasterProjectName);
			FileReference MasterProjDeleteFilename = MasterProjectFile + Tag.Ext.Solution;
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				FileReference.Delete(MasterProjDeleteFilename);
			}
			MasterProjDeleteFilename = MasterProjectFile + Tag.Ext.SQLServerCompactDatabaseFile;
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				FileReference.Delete(MasterProjDeleteFilename);
			}
			MasterProjDeleteFilename = MasterProjectFile + Tag.Ext.SolutionUserOption;
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				FileReference.Delete(MasterProjDeleteFilename);
			}
			MasterProjDeleteFilename = MasterProjectFile + Tag.Ext.VS11SolutionUserOption;
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				FileReference.Delete(MasterProjDeleteFilename);
			}
			MasterProjDeleteFilename = MasterProjectFile + Tag.Ext.VS12SolutionUserOption;
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				FileReference.Delete(MasterProjDeleteFilename);
			}

			// Delete the project files folder
			if (DirectoryReference.Exists(InIntermediateProjectFilesDirectory))
			{
				try
				{
					DirectoryReference.Delete(InIntermediateProjectFilesDirectory, true);
				}
				catch (Exception Ex)
				{
					Log.TraceInformation("Error while trying to clean project files path {0}. Ignored.", InIntermediateProjectFilesDirectory);
					Log.TraceInformation("\t" + Ex.Message);
				}
			}
		}
		
		// Allocates a generator-specific project file object
		protected override ProjectFile AllocateProjectFile(FileReference InitFilePath)
		{
			return new VCProjectFile
			(
				InitFilePath, 
				/*OnlyGameProject, */
				ProjectFileFormat, 
				bAddFastPDBToProjects, 
				bUsePerFileIntellisense,
				bUsePrecompiled, 
				bEditorDependsOnShaderCompileWorker, 
				bBuildLiveCodingConsole, 
				BuildToolOverride, 
				ExcludedIncludePaths
			);
		}

		// ProjectFileGenerator interface
		public override MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
		{
			return new VisualStudioSolutionFolder(InitOwnerProjectFileGenerator, InitFolderName);
		}

		// "4.0", "12.0", or "14.0", etc...
		public static string GetMSBuildToolsVersionString(VCProjectFileFormat ProjectFileFormat)
		{
			switch (ProjectFileFormat)
            {
                case VCProjectFileFormat.VisualStudio2012:
                    return Tag.MSBuildToolsVersion.VS2012;
				case VCProjectFileFormat.VisualStudio2013:
					return Tag.MSBuildToolsVersion.VS2013;
				case VCProjectFileFormat.VisualStudio2015:
					return Tag.MSBuildToolsVersion.VS2015;
				case VCProjectFileFormat.VisualStudio2017:
					return Tag.MSBuildToolsVersion.VS2017;
				case VCProjectFileFormat.VisualStudio2019:
					return Tag.MSBuildToolsVersion.VS2019; // Correct as of VS2019 Preview 1
			}

			return string.Empty;
		}

		// for instance: <PlatformToolset>v110</PlatformToolset>
		public static string GetPlatformToolSetVersionString(VCProjectFileFormat ProjectFileFormat)
		{
            switch (ProjectFileFormat)
            {
                case VCProjectFileFormat.VisualStudio2012:
                    return Tag.PlatformToolsVersion.VS2012;
                case VCProjectFileFormat.VisualStudio2013:
                    return Tag.PlatformToolsVersion.VS2013;
                case VCProjectFileFormat.VisualStudio2015:
					return Tag.PlatformToolsVersion.VS2015;
				case VCProjectFileFormat.VisualStudio2017:
                    return Tag.PlatformToolsVersion.VS2017;
				case VCProjectFileFormat.VisualStudio2019:
					return Tag.PlatformToolsVersion.VS2019; // Correct as of VS2019 Preview 2

            }
			return string.Empty;
		}

		public static void AppendPlatformToolsetProperty(StringBuilder VCProjectFileContent, VCProjectFileFormat ProjectFileFormat)
		{
			VCProjectFileContent.AppendLine(Tag.CppProjectContents.Indent(2) + Tag.CppProjectContents.Format.PlatformToolset, GetPlatformToolSetVersionString(ProjectFileFormat));

			if(ProjectFileFormat == VCProjectFileFormat.VisualStudio2019)
			{
				VCProjectFileContent.AppendLine(Tag.CppProjectContents.Indent(2) + Tag.CppProjectContents.Format.PlatformToolSetCondition, "\"'$(VisualStudioVersion)' == '15.0'\"", "v141");
			}
		}

		// Configures project generator based on command-line options
		protected override void ConfigureProjectFileGeneration(String[] Arguments, ref bool IncludeAllPlatforms)
		{
			// Call parent implementation first
			base.ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);
		}

		// Selects which platforms and build configurations we want in the project file
		protected override void SetupSupportedPlatformsAndConfigurations
		(
			// True if we should include ALL platforms that are supported on this machine.
		    // Otherwise, only desktop platforms will be included.
			bool IncludeAllPlatforms, 
			out string SupportedPlatformNames // Output string for supported platforms, returned as comma-separated values.
		)
		{
			// Call parent implementation to figure out the actual platforms
			base.SetupSupportedPlatformsAndConfigurations(IncludeAllPlatforms, out SupportedPlatformNames);

			// If we have a non-default setting for visual studio, check the compiler exists. If not, revert to the default.
			if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2015)
			{
				if (!WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2015_DEPRECATED))
				{
					Log.TraceWarning("Visual Studio C++ 2015 installation not found - ignoring preferred project file format.");
					ProjectFileFormat = VCProjectFileFormat.Default;
				}
			}
			else if(ProjectFileFormat == VCProjectFileFormat.VisualStudio2017)
			{
				if (!WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2017))
				{
					Log.TraceWarning("Visual Studio C++ 2017 installation not found - ignoring preferred project file format.");
					ProjectFileFormat = VCProjectFileFormat.Default;
				}
			}
			else if(ProjectFileFormat == VCProjectFileFormat.VisualStudio2019)
			{
				if (!WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2019))
				{
					Log.TraceWarning("Visual Studio C++ 2019 installation not found - ignoring preferred project file format.");
					ProjectFileFormat = VCProjectFileFormat.Default;
				}
			}

			// Certain platforms override the project file format because their debugger add-ins may not yet support the latest
			// version of Visual Studio.  This is their chance to override that.
			// ...but only if the user didn't override this via the command-line.
			if (ProjectFileFormat == VCProjectFileFormat.Default)
			{
				// Pick the best platform installed by default
				if (WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2019) && 
					WindowsPlatform.HasIDE(WindowsCompiler.VisualStudio2019))
				{
					ProjectFileFormat = VCProjectFileFormat.VisualStudio2019;
				}
				else if (WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2017) && 
					     WindowsPlatform.HasIDE(WindowsCompiler.VisualStudio2017))
				{
					ProjectFileFormat = VCProjectFileFormat.VisualStudio2017;
				}
				else if (WindowsPlatform.HasCompiler(WindowsCompiler.VisualStudio2015_DEPRECATED) && 
					     WindowsPlatform.HasIDE(WindowsCompiler.VisualStudio2015_DEPRECATED))
				{
					ProjectFileFormat = VCProjectFileFormat.VisualStudio2015;
				}

				// Allow the SDKs to override
				foreach (BuildTargetPlatform SupportedPlatform in SupportedPlatforms)
				{
					BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(SupportedPlatform, true);
					if (BuildPlatform != null)
					{
						// Don't worry about platforms that we're missing SDKs for
						if (BuildPlatform.HasRequiredSDKsInstalled() == SDKStatus.Valid)
						{
							VCProjectFileFormat ProposedFormat = BuildPlatform.GetRequiredVisualStudioVersion();

							if (ProposedFormat != VCProjectFileFormat.Default)
							{
								// Reduce the Visual Studio version to the max supported by each platform we plan to include.
								if (ProjectFileFormat == VCProjectFileFormat.Default || ProposedFormat < ProjectFileFormat)
								{
									ProjectFileFormat = ProposedFormat;
								}
							}
						}
					}
				}
			}
		}

		// Used to sort VC solution config names along with the config and platform values
		class VCSolutionConfigCombination
		{
			public string VCSolutionConfigAndPlatformName;

			public TargetConfiguration Configuration;

			public BuildTargetPlatform Platform;

			public TargetType TargetConfigurationName;

            public override string ToString() 
				=> String.Format("{0}={1} {2} {3}", VCSolutionConfigAndPlatformName, Configuration, Platform, TargetConfigurationName);
        }

		// Composes a string to use for the Visual Studio solution configuration,
		// given a build configuration and target rules configuration name
		string MakeSolutionConfigurationName(TargetConfiguration BuildConfiguration, TargetType TargetType)
		{
			string SolutionConfigName = BuildConfiguration.ToString();

			// Don't bother postfixing "Game" or "Program" -- that will be the default when using "Debug", "Development", etc.
			// Also don't postfix "RocketGame" when we're building Rocket game projects.  That's the only type of game there is in that case!
			if (TargetType != TargetType.Game 
				&& TargetType != TargetType.Program)
			{
				SolutionConfigName += " " + TargetType.ToString();
			}

			return SolutionConfigName;
		}

		static IDictionary<MasterProjectFolder, Guid> GenerateProjectFolderGuids(MasterProjectFolder RootFolder)
		{
			IDictionary<MasterProjectFolder, Guid> Guids = new Dictionary<MasterProjectFolder, Guid>();
			foreach (MasterProjectFolder Folder in RootFolder.SubFolders)
			{
				RecursivleyGenerateProjectFolderGuids("MyEngine", Folder, Guids);
			}
			return Guids;
		}

        private static void RecursivleyGenerateProjectFolderGuids(string ParentPath, MasterProjectFolder Folder, IDictionary<MasterProjectFolder, Guid> Guids)
		{
			string Path = String.Format("{0}/{1}", ParentPath, Folder.FolderName);
			Guids[Folder] = MakeMd5Guid(Encoding.UTF8.GetBytes(Path));

			foreach (MasterProjectFolder SubFolder in Folder.SubFolders)
			{
				RecursivleyGenerateProjectFolderGuids(Path, SubFolder, Guids);
			}
		}

        private static Guid MakeMd5Guid(byte[] Input)
		{
			byte[] Hash = MD5.Create().ComputeHash(Input);
			Hash[6] = (byte)(0x30 | (Hash[6] & 0x0f)); // 0b0011'xxxx Version 3 UUID (MD5)
			Hash[8] = (byte)(0x80 | (Hash[8] & 0x3f)); // 0b10xx'xxxx RFC 4122 UUID
			Array.Reverse(Hash, 0, 4);
			Array.Reverse(Hash, 4, 2);
			Array.Reverse(Hash, 6, 2);
			return new Guid(Hash);
		}

		// Writes the project files to disk
		// <returns>True if successful</returns>
		protected override bool WriteProjectFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			if(!base.WriteProjectFiles(PlatformProjectGenerators))
			{
				return false;
			}

			// Write AutomationReferences file
			if (AutomationProjectFiles.Any())
			{
				XNamespace NS = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

				DirectoryReference AutomationToolDir = DirectoryReference.Combine
				(
					BuildTool.EngineSourceDirectory, 
					Tag.Directory.ExternalTools, 
					Tag.Module.ExternalTool.AutomationTool
				);

				// See $(EngineDir)\Source\Programs\AutomationTool\AutomationTool.csproj.References
				new XDocument
					(
					    new XElement
					    (
							NS + Tag.XML.Element.Project,
						    new XAttribute(Tag.XML.Attribute.ToolsVersion, GetMSBuildToolsVersionString(ProjectFileFormat)),
						    new XAttribute(Tag.XML.Attribute.DefaultTargets, "Build"),
						    new XElement
							(
								NS + Tag.XML.Element.ItemGroup, from AutomationProject in AutomationProjectFiles select new XElement
		                        (
								    NS + Tag.XML.Element.ProjectReference,
								    new XAttribute(Tag.XML.Attribute.Include, AutomationProject.ProjectFilePath.MakeRelativeTo(AutomationToolDir)),
								    new XElement(NS + Tag.XML.Element.Project, (AutomationProject as VCSharpProjectFile).ProjectGUID.ToString("B")),
								    new XElement(NS + Tag.XML.Element.Name, AutomationProject.ProjectFilePath.GetFileNameWithoutExtension()),
								    new XElement(NS + Tag.XML.Element.Private, "false")
							    )
						    )
					    )
				    ).Save(FileReference.Combine(AutomationToolDir, Tag.Module.ExternalTool.AutomationTool + Tag.Ext.VCSharpProjectReferences).FullName);
			}

			return true;
		}

		protected override bool WriteMasterProjectFile(ProjectFile BuildToolProject, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			bool bResultSuccess = true;

			string SolutionFileName = MasterProjectName + Tag.Ext.Solution;

			// Setup solution file content
			StringBuilder VCSolutionFileContent = new StringBuilder();

			// Solution file header. Note that a leading newline is required for file type detection to work correclty in the shell.
			if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2019)
			{
				VCSolutionFileContent.AppendLine();
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFileFormatVersion, "12.00");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMajorVersion, "16");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFullVersion, "16.0.28315.86");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMinimumOldestVSVersion, "10.0.40219.1");
			}
			else if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2017)
			{
				VCSolutionFileContent.AppendLine();
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFileFormatVersion, "12.00");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMajorVersion, "15");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFullVersion, "15.0.25807.0");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMinimumOldestVSVersion, "10.0.40219.1");
			}
			else if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2015)
			{
				VCSolutionFileContent.AppendLine();
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFileFormatVersion, "12.00");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMajorVersion, "14");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFullVersion, "15.0.22310.1");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMinimumOldestVSVersion, "10.0.40219.1");
			}
			else if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2013)
			{
				VCSolutionFileContent.AppendLine();
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFileFormatVersion, "12.00");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMajorVersion, "2013");
            }
            else if (ProjectFileFormat == VCProjectFileFormat.VisualStudio2012)
            {
				VCSolutionFileContent.AppendLine();
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderFileFormatVersion, "12.00");
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.HeaderMajorVersion, "2012");
			}
			else
			{
				throw new BuildException("Unexpected ProjectFileFormat");
			}

			IDictionary<MasterProjectFolder, Guid> ProjectFolderGuids = GenerateProjectFolderGuids(RootFolder);

			string SingleIndent = Tag.SolutionContents.Indent(1);
			string DoubleIndent = Tag.SolutionContents.Indent(2);
			string TripleIndent = Tag.SolutionContents.Indent(3);

			// Solution folders, files and project entries
			{
				// Solution folders
				{
					IEnumerable<MasterProjectFolder> AllSolutionFolders = ProjectFolderGuids.Keys.OrderBy(Folder => Folder.FolderName).ThenBy(Folder => ProjectFolderGuids[Folder]);

					// AllSolutionFolders
					// [0] -> "Engine"     ChildProject {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
					// [1] -> "Programs"   ChildProject {D:\UERelease\Engine\Intermediate\ProjectFiles\BenchmarkTool.vcxproj}, {D:\UERelease\Engine\Intermediate\ProjectFiles\BlankProgram.vcxproj}
					// [2] -> "DataSmith"  ChildProject 그 세트들...
					// [3] -> "Automation" ChildProject {D:\UERelease\Engine\Source\Programs\AutomationTool\AllDesktop\AllDesktop.Automation.csproj} ... 
					foreach (MasterProjectFolder CurFolder in AllSolutionFolders)
					{
						// Format : B - 32 digits separated by hyphens, enclosed in braces:
						// { 00000000 - 0000 - 0000 - 0000 - 000000000000}
						string FolderGUIDString = ProjectFolderGuids[CurFolder].ToString("B").ToUpperInvariant();
						VCSolutionFileContent.AppendLine(Tag.SolutionContents.ProjectDeclaration, Tag.GUID.SolutionFolders, CurFolder.FolderName, CurFolder.FolderName, FolderGUIDString);

						// Add any files that are inlined right inside the solution folder
						if (0 < CurFolder.Files.Count)
						{
							VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.ProjectSection + Tag.SolutionContents.SolutionItems + " = " + Tag.SolutionContents.PreProject);

							foreach (string CurFile in CurFolder.Files)
							{
								// Syntax is:  <relative file path> = <relative file path>
								VCSolutionFileContent.AppendLine(DoubleIndent + CurFile + " = " + CurFile);
							}
							VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndProjectSection);
						}

						VCSolutionFileContent.AppendLine(Tag.SolutionContents.EndProject);
					}
				}

				// Project files { Engine + Programs + DataSmith + Automation } Solution Folder's Projects
				foreach (MSBuildProjectFile CurProject in AllProjectFiles)
				{
					// Visual Studio uses different GUID types depending on the project type
					string ProjectTypeGUID = CurProject.ProjectTypeGUID;

					// NOTE: The project name in the solution doesn't actually *have* to match the project file name on disk.  However,
					//       we prefer it when it does match so we use the actual file name here.
					string ProjectNameInSolution = CurProject.ProjectFilePath.GetFileNameWithoutExtension();

					// Use the existing project's GUID that's already known to us
					string ProjectGUID = CurProject.ProjectGUID.ToString("B").ToUpperInvariant();

					VCSolutionFileContent.AppendLine
					(
						Tag.SolutionContents.ProjectDeclaration,
						ProjectTypeGUID, 
						ProjectNameInSolution, 
						CurProject.ProjectFilePath.MakeRelativeTo(ProjectFileGenerator.MasterProjectPath), 
						ProjectGUID
					);

					// Setup dependency on BuildTool, if we need that.
					// This makes sure that BuildTool is freshly compiled before kicking off any build operations on this target project
					if (!CurProject.IsStubProject)
					{
						List<ProjectFile> Dependencies = new List<ProjectFile>();
						if (CurProject.IsGeneratedProject 
							&& BuildToolProject != null 
							&& BuildToolProject != CurProject)
						{
							Dependencies.Add(BuildToolProject);
							Dependencies.AddRange(BuildToolProject.DependsOnProjects);
						}
						Dependencies.AddRange(CurProject.DependsOnProjects);

						if (0 < Dependencies.Count)
						{
							VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.ProjectSection + Tag.SolutionContents.ProjectDependencies + " = " + Tag.SolutionContents.PostProject);

							// Setup any addition dependencies this project has...
							foreach (ProjectFile DependsOnProject in Dependencies)
							{
								string DependsOnProjectGUID = ((MSBuildProjectFile)DependsOnProject).ProjectGUID.ToString("B").ToUpperInvariant();
								VCSolutionFileContent.AppendLine(DoubleIndent + DependsOnProjectGUID + " = " + DependsOnProjectGUID);
							}

							VCSolutionFileContent.AppendLine(Tag.SolutionContents.Indent(1) + Tag.SolutionContents.EndProjectSection);
						}
					}

					VCSolutionFileContent.AppendLine(Tag.SolutionContents.EndProject);
				}

				// .natvis
				// See :
				// https://docs.microsoft.com/en-us/visualstudio/debugger/create-custom-views-of-native-objects?view=vs-2019,
				// %VSINSTALLDIR%\Xml\Schemas\natvis.xsd
				// C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Xml\Schemas\1042

				// Get the path to the visualizers file.
				// Try to make it relative to the solution directory, but fall back to a full path if it's a foreign project.
				FileReference VisualizersFile = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Extras, Tag.Directory.VisualStudioDebugging, Tag.Binary.DebuggingVisualizer);

				// Add the visualizers at the solution level. Doesn't seem to be picked up from a makefile project in VS2017 15.8.5.
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.ProjectDeclaration, Tag.GUID.SolutionFolders, "Visualizers", "Visualizers", Tag.GUID.DebuggerVisualizer);
				VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.ProjectSection + Tag.SolutionContents.SolutionItems + " = " + Tag.SolutionContents.PreProject);
				VCSolutionFileContent.AppendLine(DoubleIndent + "{0} = {0}", VisualizersFile.MakeRelativeTo(MasterProjectPath));
				VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndProjectSection);
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.EndProject);
			}

			// Solution configuration platforms.  This is just a list of all of the platforms and configurations that
			// appear in Visual Studio's build configuration selector.
			List<VCSolutionConfigCombination> SolutionConfigCombinations = new List<VCSolutionConfigCombination>();

			// The "Global" section has source control, solution configurations, project configurations,
			// preferences, and project hierarchy data
			{
				VCSolutionFileContent.AppendLine(Tag.SolutionContents.Global);
				{
					{
						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.SolutionConfigurationPlatforms + " = " + Tag.SolutionContents.PreSolution);

						Dictionary<string, Tuple<TargetConfiguration, TargetType>> SolutionConfigurationsValidForProjects = new Dictionary<string, Tuple<TargetConfiguration, TargetType>>();
						HashSet<BuildTargetPlatform> PlatformsValidForProjects = new HashSet<BuildTargetPlatform>();

						foreach (TargetConfiguration CurConfiguration in SupportedConfigurations)
						{
							if (InstalledPlatformInfo.IsValidConfiguration(CurConfiguration, EProjectType.Code))
							{
								foreach (BuildTargetPlatform CurPlatform in SupportedPlatforms)
								{
									if (InstalledPlatformInfo.IsValidPlatform(CurPlatform, EProjectType.Code))
									{
										foreach (ProjectFile CurProject in AllProjectFiles)
										{
											if (!CurProject.IsStubProject)
											{
												if (CurProject.ProjectTargets.Count == 0)
												{
													throw new BuildException("Expecting project '" + CurProject.ProjectFilePath + "' to have at least one ProjectTarget associated with it!");
												}

												// Figure out the set of valid target configuration names
												foreach (ProjectTarget ProjectTarget in CurProject.ProjectTargets)
												{
													if (VCProjectFile.IsValidProjectPlatformAndConfiguration(ProjectTarget, CurPlatform, CurConfiguration/*, PlatformProjectGenerators*/))
													{
														PlatformsValidForProjects.Add(CurPlatform);

														// Default to a target configuration name of "Game", since that will collapse down to an empty string
														TargetType TargetType = TargetType.Game;
														if (ProjectTarget.TargetRules != null)
														{
															TargetType = ProjectTarget.TargetRules.Type;
														}

														string SolutionConfigName = MakeSolutionConfigurationName(CurConfiguration, TargetType);
														SolutionConfigurationsValidForProjects[SolutionConfigName] = new Tuple<TargetConfiguration, TargetType>(CurConfiguration, TargetType);
													}
												}
											}
										}
									}
								}
							}
						}

						foreach (BuildTargetPlatform CurPlatform in PlatformsValidForProjects)
						{
							foreach (KeyValuePair<string, Tuple<TargetConfiguration, TargetType>> SolutionConfigKeyValue in SolutionConfigurationsValidForProjects)
							{
								// e.g.  "Development|Win64 = Development|Win64"
								string SolutionConfigName = SolutionConfigKeyValue.Key;
								TargetConfiguration Configuration = SolutionConfigKeyValue.Value.Item1;
								TargetType TargetType = SolutionConfigKeyValue.Value.Item2;

								string SolutionPlatformName = CurPlatform.ToString();

								string SolutionConfigAndPlatformPair = SolutionConfigName + "|" + SolutionPlatformName;
								SolutionConfigCombinations.Add
									(
										new VCSolutionConfigCombination
										{
											VCSolutionConfigAndPlatformName = SolutionConfigAndPlatformPair,
											Configuration = Configuration,
											Platform = CurPlatform,
											TargetConfigurationName = TargetType
										}
									);
							}
						}

						// Sort the list of solution platform strings alphabetically (Visual Studio prefers it)
						SolutionConfigCombinations.Sort
						(
							new Comparison<VCSolutionConfigCombination>
							(
								(x, y) => 
								{ 
									return String.Compare
									(
										x.VCSolutionConfigAndPlatformName, 
										y.VCSolutionConfigAndPlatformName, 
										StringComparison.InvariantCultureIgnoreCase
									);}));

						HashSet<string> AppendedSolutionConfigAndPlatformNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
						foreach (VCSolutionConfigCombination SolutionConfigCombination in SolutionConfigCombinations)
						{
							// We alias "Game" and "Program" to both have the same solution configuration, so we're careful not to add the same combination twice.
							if (!AppendedSolutionConfigAndPlatformNames.Contains(SolutionConfigCombination.VCSolutionConfigAndPlatformName))
							{
								VCSolutionFileContent.AppendLine(DoubleIndent + SolutionConfigCombination.VCSolutionConfigAndPlatformName + " = " + SolutionConfigCombination.VCSolutionConfigAndPlatformName);
								AppendedSolutionConfigAndPlatformNames.Add(SolutionConfigCombination.VCSolutionConfigAndPlatformName);
							}
						}

						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndGlobalSection);
					}

					// Assign each project's "project configuration" to our "solution platform + configuration" pairs.
					// This also sets up which projects are actually built when building the solution.
					{
						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.GlobalSection + Tag.SolutionContents.ProjectConfigurationPlatforms + " = " + Tag.SolutionContents.PostSolution);

						foreach (MSBuildProjectFile CurProject in AllProjectFiles)
						{
							foreach (VCSolutionConfigCombination SolutionConfigCombination in SolutionConfigCombinations)
							{
								// Get the context for the current solution context
								MSBuildProjectContext ProjectContext = CurProject.GetMatchingProjectContext
								(
                                    SolutionConfigCombination.TargetConfigurationName,
                                    SolutionConfigCombination.Configuration,
                                    SolutionConfigCombination.Platform,
                                    PlatformProjectGenerators
								);

								// Override the configuration to build for UBT
								if (bBuildUBTInDebug && CurProject == BuildToolProject)
								{
									ProjectContext.ConfigurationName = Tag.Configuration.Debug;
								}

								// Write the solution mapping (e.g.  "{4232C52C-680F-4850-8855-DC39419B5E9B}.Debug|iOS.ActiveCfg = iOS_Debug|Win32")
								string CurProjectGUID = CurProject.ProjectGUID.ToString("B").ToUpperInvariant();
								VCSolutionFileContent.AppendLine(DoubleIndent + "{0}.{1}.{2} = {3}", CurProjectGUID, SolutionConfigCombination.VCSolutionConfigAndPlatformName, Tag.SolutionContents.ActiveConfiguration, ProjectContext.Name);
								if (ProjectContext.bBuildByDefault)
								{
									VCSolutionFileContent.AppendLine(DoubleIndent + "{0}.{1}.{2}.0 = {3}", CurProjectGUID, SolutionConfigCombination.VCSolutionConfigAndPlatformName, Tag.SolutionContents.BuildConfiguration, ProjectContext.Name);
									if(ProjectContext.bDeployByDefault)
									{
										VCSolutionFileContent.AppendLine(DoubleIndent + "{0}.{1}.{2}.0 = {3}", CurProjectGUID, SolutionConfigCombination.VCSolutionConfigAndPlatformName, Tag.SolutionContents.DeployConfiguration, ProjectContext.Name);
									}
								}
							}
						}

						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndGlobalSection);
					}

					// Setup other solution properties
					{
						// HideSolutionNode sets whether or not the top-level solution entry is completely hidden in the UI.
						// We don't want that, as we need users to be able to right click on the solution tree item.
						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.GlobalSection + Tag.SolutionContents.SolutionProperties + " = " + Tag.SolutionContents.PreSolution);
						VCSolutionFileContent.AppendLine(DoubleIndent + Tag.SolutionContents.HideSolutionNode + " = FALSE");
						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndGlobalSection);
					}

					// Solution directory hierarchy
					{
						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.GlobalSection + Tag.SolutionContents.NestedProjects + " = " + Tag.SolutionContents.PreSolution);

                        // Every entry in this section is in the format "Guid1 = Guid2".
                        // Guid1 is the child project (or solution filter)'s GUID,
                        // and Guid2 is the solution filter directory to parent the child project (or solution filter) to.
                        // This sets up the hierarchical solution explorer tree for all solution folders and projects.
                        void RecursivelyFolderProcessorFunction(StringBuilder LocalVCSolutionFileContent, List<MasterProjectFolder> LocalMasterProjectFolders)
                        {
                            foreach (MasterProjectFolder CurFolder in LocalMasterProjectFolders)
                            {
                                string CurFolderGUIDString = ProjectFolderGuids[CurFolder].ToString("B").ToUpperInvariant();

                                foreach (MSBuildProjectFile ChildProject in CurFolder.ChildProjects)
                                {
                                    //	e.g. "{BF6FB09F-A2A6-468F-BE6F-DEBE07EAD3EA} = {C43B6BB5-3EF0-4784-B896-4099753BCDA9}"
                                    LocalVCSolutionFileContent.AppendLine(DoubleIndent + ChildProject.ProjectGUID.ToString("B").ToUpperInvariant() + " = " + CurFolderGUIDString);
                                }

                                foreach (MasterProjectFolder SubFolder in CurFolder.SubFolders)
                                {
                                    //	e.g. "{BF6FB09F-A2A6-468F-BE6F-DEBE07EAD3EA} = {C43B6BB5-3EF0-4784-B896-4099753BCDA9}"
                                    LocalVCSolutionFileContent.AppendLine(DoubleIndent + ProjectFolderGuids[SubFolder].ToString("B").ToUpperInvariant() + " = " + CurFolderGUIDString);
                                }

                                // Recurse into subfolders
                                RecursivelyFolderProcessorFunction(LocalVCSolutionFileContent, CurFolder.SubFolders);
                            }
                        }

                        RecursivelyFolderProcessorFunction(VCSolutionFileContent, RootFolder.SubFolders);

						VCSolutionFileContent.AppendLine(SingleIndent + Tag.SolutionContents.EndGlobalSection);
					}
				}

				VCSolutionFileContent.AppendLine(Tag.SolutionContents.EndGlobal);
			}

			// Save the solution file
			if (bResultSuccess)
			{
				string SolutionFilePath = FileReference.Combine(MasterProjectPath, SolutionFileName).FullName;
				bResultSuccess = WriteFileIfChanged(SolutionFilePath, VCSolutionFileContent.ToString());
			}

			// Save a solution config file which selects the development editor configuration by default.
			if (bResultSuccess && bWriteSolutionOptionFile)
			{
				// Figure out the filename for the SUO file. VS will automatically import the options from earlier versions if necessary.
				FileReference SolutionOptionsFileName;
				switch (ProjectFileFormat)
                {
                    case VCProjectFileFormat.VisualStudio2012:
						SolutionOptionsFileName = FileReference.Combine(MasterProjectPath, Path.ChangeExtension(SolutionFileName, Tag.Ext.VS11SolutionUserOption));
                        break;
					case VCProjectFileFormat.VisualStudio2013:
						SolutionOptionsFileName = FileReference.Combine(MasterProjectPath, Path.ChangeExtension(SolutionFileName, Tag.Ext.VS12SolutionUserOption));
						break;
					case VCProjectFileFormat.VisualStudio2015:
						SolutionOptionsFileName = FileReference.Combine(MasterProjectPath, Tag.Directory.DotVS, Path.GetFileNameWithoutExtension(SolutionFileName), Tag.Directory.V14, Tag.Ext.SolutionUserOption);
						break;
					case VCProjectFileFormat.VisualStudio2017:
						SolutionOptionsFileName = FileReference.Combine(MasterProjectPath, Tag.Directory.DotVS, Path.GetFileNameWithoutExtension(SolutionFileName), Tag.Directory.V15, Tag.Ext.SolutionUserOption);
						break;
					case VCProjectFileFormat.VisualStudio2019:
						SolutionOptionsFileName = FileReference.Combine(MasterProjectPath, Tag.Directory.DotVS, Path.GetFileNameWithoutExtension(SolutionFileName), Tag.Directory.V15, Tag.Ext.SolutionUserOption); // Still uses v15
						break;
					default:
						throw new BuildException("Unsupported Visual Studio version");
				}

				// Check it doesn't exist before overwriting it. Since these files store the user's preferences, it'd be bad form to overwrite them.
				if (!FileReference.Exists(SolutionOptionsFileName))
				{
					DirectoryReference.CreateDirectory(SolutionOptionsFileName.Directory);

					VCSolutionOptions Options = new VCSolutionOptions(ProjectFileFormat);

					// Set the default configuration and startup project
					VCSolutionConfigCombination DefaultConfig 
						= SolutionConfigCombinations.Find(x => x.Configuration == TargetConfiguration.Development && x.Platform == BuildTargetPlatform.Win64 && x.TargetConfigurationName == TargetType.Editor);
					if (DefaultConfig != null)
					{
                        List<VCBinarySetting> Settings = new List<VCBinarySetting> { new VCBinarySetting("ActiveCfg", DefaultConfig.VCSolutionConfigAndPlatformName) };
                       
						if (DefaultProject != null)
						{
							Settings.Add(new VCBinarySetting("StartupProject", ((MSBuildProjectFile)DefaultProject).ProjectGUID.ToString("B")));
						}

						Options.SetConfiguration(Settings);
					}

					// Mark all the projects as closed by default, apart from the startup project
					VCSolutionExplorerState ExplorerState = new VCSolutionExplorerState();
					if(VCProjectFileFormat.VisualStudio2017 <= ProjectFileFormat)
					{
						RecursivelyBuildSolutionExplorerState_VS2017(RootFolder, "", ExplorerState, DefaultProject);
					}
					else
					{
						BuildSolutionExplorerState_VS2015(AllProjectFiles, ExplorerState, DefaultProject, IncludeEnginePrograms);
					}
					Options.SetExplorerState(ExplorerState);

					// Write the file
					if (0 < Options.Sections.Count)
					{
						Options.Write(SolutionOptionsFileName.FullName);
					}
				}
			}

			return bResultSuccess;
		}

		[Obsolete]
        protected override void WriteDebugSolutionFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators, DirectoryReference IntermediateProjectFilesPath)
        {
            StringBuilder VSDebugSolutionFile = new StringBuilder();
			// SupportedPlatforms => Win32, Win64, HoloLens, Mac, IOS, ... 
			foreach (BuildTargetPlatform SupportedPlatform in SupportedPlatforms)
			{
				PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(SupportedPlatform, true);
			}
			if (0 < VSDebugSolutionFile.Length)
			{
				VSDebugSolutionFile.Insert(0, "<DebugSolution>" + ProjectFileGenerator.NewLine);
				VSDebugSolutionFile.Append("</DebugSolution>" + ProjectFileGenerator.NewLine );

				string ConfigFilePath = FileReference.Combine(IntermediateProjectFilesPath, Tag.OutputFile.DebugEngineSolution).FullName;
                WriteFileIfChanged(ConfigFilePath, VSDebugSolutionFile.ToString());
			}
		}

        private static void RecursivelyBuildSolutionExplorerState_VS2017
		(
            MasterProjectFolder Folder,
            string Suffix,
            VCSolutionExplorerState ExplorerState,
            ProjectFile DefaultProject
		)
		{
			foreach(ProjectFile Project in Folder.ChildProjects)
			{
				string ProjectIdentifier = String.Format("{0}{1}", Project.ProjectFilePath.GetFileNameWithoutExtension(), Suffix);
				if (Project == DefaultProject)
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(ProjectIdentifier, new string[] { ProjectIdentifier }));
				}
				else
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(ProjectIdentifier, new string[] { }));
				}
			}

			foreach(MasterProjectFolder SubFolder in Folder.SubFolders)
			{
				string SubFolderName = SubFolder.FolderName + Suffix;
				if(SubFolderName == String.Format("Automation;Programs", Tag.Module.ExternalTool.))
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(SubFolderName, new string[] {}));
				}
				else
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(SubFolderName, new string[] { SubFolderName }));
				}

				RecursivelyBuildSolutionExplorerState_VS2017(SubFolder, ";" + SubFolderName, ExplorerState, DefaultProject);
			}
		}

		static void BuildSolutionExplorerState_VS2015(List<ProjectFile> AllProjectFiles, VCSolutionExplorerState ExplorerState, ProjectFile DefaultProject, bool IncludeEnginePrograms)
		{
			foreach (ProjectFile ProjectFile in AllProjectFiles)
			{
				string ProjectName = ProjectFile.ProjectFilePath.GetFileNameWithoutExtension();
				if (ProjectFile == DefaultProject)
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(ProjectName, new string[] { ProjectName }));
				}
				else
				{
					ExplorerState.OpenProjects.Add(new Tuple<string, string[]>(ProjectName, new string[] { }));
				}
			}
			if (IncludeEnginePrograms)
			{
				ExplorerState.OpenProjects.Add(new Tuple<string, string[]>("Automation", new string[0]));
			}
		}

		// Takes a string and "cleans it up" to make it parsable by the Visual Studio source control provider's file format
		public string CleanupStringForSCC(string Str)
		{
			string Cleaned = Str;

			// SCC is expecting paths to contain only double-backslashes for path separators.  It's a bit weird but we need to do it.
			Cleaned = Cleaned.Replace(Path.DirectorySeparatorChar.ToString(), Path.DirectorySeparatorChar.ToString() + Path.DirectorySeparatorChar.ToString());
			Cleaned = Cleaned.Replace(Path.AltDirectorySeparatorChar.ToString(), Path.DirectorySeparatorChar.ToString() + Path.DirectorySeparatorChar.ToString());

			// SCC is expecting not to see spaces in these strings, so we'll replace spaces with "\u0020"
			Cleaned = Cleaned.Replace(" ", "\\u0020");

			return Cleaned;
		}
	}
}
