using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Microsoft.Win32;
using System.Linq;
using System.Diagnostics;
using BuildToolUtilities;
using System.Xml.Linq;

namespace BuildTool
{
#pragma warning disable IDE0079 // Remove unnecessary suppression
	// Represents a folder within the master project (e.g. Visual Studio solution)
	abstract class MasterProjectFolder
	{
		public string FolderName { get; private set; }

		private readonly ProjectFileGenerator      OwnerProjectFileGenerator;
		public  readonly List<MasterProjectFolder> SubFolders    = new List<MasterProjectFolder>();
		public  readonly List<ProjectFile>         ChildProjects = new List<ProjectFile>();

		// These are files that aren't part of any project,
		// but display in the IDE under the project folder and can be browsed/opened by the user easily in the user interface
		public readonly List<string> Files = new List<string>();

		public MasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
		{
			OwnerProjectFileGenerator = InitOwnerProjectFileGenerator;
			FolderName = InitFolderName;
		}

		// Adds a new sub-folder to this folder
		public MasterProjectFolder AddSubFolder(string SubFolderName)
		{
			MasterProjectFolder ResultFolder = null;

			List<string> FolderNames = SubFolderName.Split(new char[2] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, 2, StringSplitOptions.RemoveEmptyEntries).ToList();
			string       FirstFolderName = FolderNames[0];

			bool AlreadyExists = false;
			foreach (MasterProjectFolder ExistingFolder in SubFolders)
			{
				if (ExistingFolder.FolderName.Equals(FirstFolderName, StringComparison.InvariantCultureIgnoreCase))
				{
					ResultFolder = ExistingFolder;
					AlreadyExists = true;
					break;
				}
			}

			if (!AlreadyExists)
			{
				ResultFolder = OwnerProjectFileGenerator.AllocateMasterProjectFolder(OwnerProjectFileGenerator, FirstFolderName);
				SubFolders.Add(ResultFolder);
			}

			if (1 < FolderNames.Count)
			{
				ResultFolder = ResultFolder.AddSubFolder(FolderNames[1]);
			}

			return ResultFolder;
		}

		// Recursively searches for the specified project and returns the folder that it lives in, or null if not found
		public MasterProjectFolder FindFolderForProject(ProjectFile ProjectToFind)
		{
			foreach (MasterProjectFolder CurFolder in SubFolders)
			{
				MasterProjectFolder FoundFolder = CurFolder.FindFolderForProject(ProjectToFind);
				if (FoundFolder != null)
				{
					return FoundFolder;
				}
			}

			foreach (ProjectFile ChildProject in ChildProjects)
			{
				if (ChildProject == ProjectToFind)
				{
					return this;
				}
			}

			return null;
		}
	}

	// The type of project files to generate
	enum ProjectFileFormat
	{
		Make,
		CMake,
		QMake,
		KDevelop,
		CodeLite,
		VisualStudio,
		VisualStudio2012,
		VisualStudio2013,
		VisualStudio2015,
		VisualStudio2017,
		VisualStudio2019,
		XCode,
		Eddie,
		VisualStudioCode,
		VisualStudioMac,
		CLion,
		Rider
	}

    // Static class containing 
    internal static class ProjectFileGeneratorSettings
	{
		// Default list of project file formats to generate.
		[XMLConfigFile(Category = "ProjectFileGenerator", Name = "Format")]
		public static string Format = null;

		// Parses a list of project file formats from a string
		public static IEnumerable<ProjectFileFormat> ParseFormatList(string Formats)
		{
			foreach (string FormatName in Formats.Split('+').Select(x => x.Trim()))
			{
				if (Enum.TryParse(FormatName, true, out ProjectFileFormat Format))
				{
					yield return Format;
				}
				else
				{
					Log.TraceError("Invalid project file format '{0}'", FormatName);
				}
			}
		}
	}

	// Base class for all project file generators
	abstract class ProjectFileGenerator
	{
		// Global static that enables generation of project files.
		// Doesn't actually compile anything.
		// This is enabled only via BuildTool command-line.
		// [CommandLine("-GenerateProjectFiles")]
		public static bool bGenerateProjectFiles = false;

		// True if we're generating lightweight project files for a single game only, excluding most engine code, documentation, etc.
		// [CommandLine("-GenerateGameProjectFiles")]
		public bool bGeneratingGameProjectFiles = false;
		
		// From Arguments "-Platforms="
		// [CommandLine("-Platforms=")]
		readonly List<BuildTargetPlatform> ProjectPlatforms = new List<BuildTargetPlatform>(); // Optional list of platforms to generate projects for

		// Whether to append the list of platform names after the solution
		public bool bAppendPlatformSuffix;

		// When bGeneratingGameProjectFiles=true, this is the game name we're generating projects for
		protected string GameProjectName = null;

		// Whether we should include configurations for "Test" and "Shipping" in generated projects. Pass "-NoShippingConfigs" to disable this.
		[XMLConfigFile]
		public static bool bIncludeTestAndShippingConfigs = true;

		// True if intellisense data should be generated (takes a while longer).
		[XMLConfigFile]
		bool bGenerateIntelliSenseData = true;

		// True if we should include documentation in the generated projects.
		[XMLConfigFile]
		protected bool bIncludeDocumentation = false;

		// True if all documentation languages should be included in generated projects,
		// otherwise only INT files will be included.
		[XMLConfigFile]
		bool bAllDocumentationLanguages = false;

		// True if build targets should pass the -useprecompiled argument.
		[XMLConfigFile]
		public bool bUsePrecompiled = false;

		// True if we should include engine source in the generated solution.
		[XMLConfigFile]
		protected bool bIncludeEngineSourceInSolution = true;

		// Whether to include enterprise source in the generated solution.
		// DataSmith, DataPrepEditor, AxFImport, LidarPointCloud, MDLImport, StaticMeshEditorExtension, VariantManager, VariantManagerContent
		[XMLConfigFile]
		protected bool bIncludeEnterpriseSource = true;

		// True if shader source files should be included in generated projects.
		[XMLConfigFile]
		protected bool bIncludeShaderSourceInProject = true;

		// True if build system files should be included.
		[XMLConfigFile]
		bool bIncludeBuildSystemFiles = true;

		// True if we should include config (.ini) files in the generated project.
		[XMLConfigFile]
		protected bool bIncludeConfigFiles = true;

		// True if we should include localization files in the generated project.
		[XMLConfigFile]
		private readonly bool bIncludeLocalizationFiles = false;

		// True if we should include template files in the generated project.
		[XMLConfigFile]
		protected bool bIncludeTemplateFiles = true;

		// True if we should include program projects in the generated solution.
		[XMLConfigFile]
		protected bool IncludeEnginePrograms = true;

		// Whether to include temporary targets generated by UAT to support content only projects with non-default settings.
		[XMLConfigFile]
		bool bIncludeTempTargets = false;

		// True if we should include .NET Core projects in the generated solution.
		[XMLConfigFile]
		bool bIncludeDotNETCoreProjects = false;

		// True if we should reflect "Source" sub-directories on disk in the master project as master project directories.
		// This (arguably) adds some visual clutter to the master project but it is truer to the on-disk file organization.
		[XMLConfigFile]
		readonly bool bKeepSourceSubDirectories = true;
		
		// Names of platforms to include in the generated project files
		[XMLConfigFile(Name = "Platforms")]
		private readonly string[] PlatformNames = null;

		// Names of configurations to include in the generated project files.
		// See TargetConfiguration for valid entries
		[XMLConfigFile(Name = "Configurations")]
		private readonly string[] ConfigurationNames = null;

		// Relative path to the directory where the master project file will be saved to
		public static DirectoryReference MasterProjectPath = BuildTool.RootDirectory; // We'll save the master project to our "root" folder

		// Name of the engine project that contains all of the engine code, config files and other files
		public const string EngineProjectFileNameBase = "MyEngine";

		// Name of the  enterprise project that contains all of the enterprise code, config files and other files
		public const string EnterpriseProjectFileNameBase = "Studio";

		// When ProjectsAreIntermediate is true, this is the directory to store generated project files
		// @todo projectfiles: Ideally, projects for game modules/targets would be created in the game's Intermediate folder!
		public static DirectoryReference IntermediateProjectFilesPath = DirectoryReference.Combine(BuildTool.EngineDirectory, "Intermediate", "ProjectFiles");

		// Path to timestamp file, recording when was the last time projects were created.
		public static string ProjectTimestampFile = Path.Combine(IntermediateProjectFilesPath.FullName, "Timestamp");

		// Global static new line string used by ProjectFileGenerator to generate project files.
		public static readonly string NewLine = Environment.NewLine;

		// If true, we'll parse subdirectories of third-party projects to locate source and header files to include in the
		// generated projects.
		// This can make the generated projects quite a bit bigger, but makes it easier to open files directly from the IDE.
		bool bGatherThirdPartySource = false;

		// Indicates whether we should process dot net core based C# projects
		private readonly bool AllowDotNetCoreProjects = false;

		// Name of the master project file
		// for example, the base file name for the Visual Studio solution file, or the Xcode project file on Mac.
		[XMLConfigFile]
		protected string MasterProjectName = "MyEngine";

		// If true, sets the master project name according to the name of the folder it is in.
		[XMLConfigFile]
		protected bool bMasterProjectNameFromFolder = false;

		// Maps all module names that were included in generated project files, to actual project file objects.
		protected Dictionary<string, ProjectFile> ModuleToProjectFileMap = new Dictionary<string, ProjectFile>(StringComparer.InvariantCultureIgnoreCase);

		// If generating project files for a single project, the path to its .uproject file.
		public readonly FileReference OnlyGameProject;

		// File extension for project files we'll be generating (e.g. ".vcxproj")
		public abstract string ProjectFileExtension { get; }

		// The default project to be built for the solution.
		protected ProjectFile DefaultProject;

		// The project for BuildTool.  Note that when generating project files for installed builds, we won't have
		// an BuildTool project at all.
		protected ProjectFile BuildToolProject;

		// List of platforms that we'll support in the project files
		protected List<BuildTargetPlatform> SupportedPlatforms = new List<BuildTargetPlatform>();

		// List of build configurations that we'll support in the project files
		protected List<TargetConfiguration> SupportedConfigurations = new List<TargetConfiguration>();

		// Map of project file names to their project files.  This includes every single project file in memory or otherwise that
		// we know about so far.  Note that when generating project files, this map may even include project files that we won't
		// be including in the generated projects.
		protected readonly Dictionary<FileReference, ProjectFile> ProjectFileMap = new Dictionary<FileReference, ProjectFile>();

		// List of project files that we'll be generating
		protected readonly List<ProjectFile> GeneratedProjectFiles = new List<ProjectFile>();

		// List of other project files that we want to include in a generated solution file,
		// even though we aren't generating them ourselves.
		// Note that these may *not* always be C++ project files (e.g. C#)
		protected readonly List<ProjectFile> OtherProjectFiles = new List<ProjectFile>();

		protected readonly List<ProjectFile> AutomationProjectFiles = new List<ProjectFile>();

		// List of top-level folders in the master project file
		protected MasterProjectFolder RootFolder;

        virtual public bool GetbGenerateIntelliSenseData() => bGenerateIntelliSenseData;

		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// CommandLine 받고, CommandArgument.ApplyTo(this);
		// Default constructor.
		// <param name="InOnlyGameProject">The project file passed in on the command line</param>
		public ProjectFileGenerator(FileReference InOnlyGameProject)
		{
			AllowDotNetCoreProjects = Environment.CommandLine.Contains("-dotnetcore");

			if(!FileReference.Exists(InOnlyGameProject))
            {
				throw new BuildException("ProjectFileGenerator::InOnlyGameProject doesn't exist.");
			}

			OnlyGameProject = InOnlyGameProject;
			// CommandLine 받고, CommandArgument.ApplyTo(this);
			XMLConfig.ApplyTo(this);
		}

		// Adds all *.automation.csproj files to the solution.
		void AddAutomationModules(List<FileReference> BuildProjectFiles, MasterProjectFolder ProgramsFolder)
		{
			MasterProjectFolder Folder = ProgramsFolder.AddSubFolder("Automation");
			List<DirectoryReference> BuildFolders = new List<DirectoryReference>();
			foreach (FileReference BuildProjectFile in BuildProjectFiles)
			{
				DirectoryReference GameBuildFolder = DirectoryReference.Combine(BuildProjectFile.Directory, "Build");
				if (DirectoryReference.Exists(GameBuildFolder))
				{
					BuildFolders.Add(GameBuildFolder);
				}
			}

			// Find all the automation modules .csproj files to add
			// D:\UERelease\Engine\Source\Programs\AutomationTool\*\*.Automation.csproj
			// { AllDesktop, Android, AutomationUtils, BuildGraph, Gauntlet, HoloLens, IOS, Linux, Lumin, Mac, OneSkyLocalization, Scripts, TVOS, Win, XLocLocalization }
			List<FileReference> AutomationModuleFiles = RulesCompiler.FindAllRulesSourceFiles(RulesCompiler.RulesFileType.AutomationModule, null, ForeignPlugins: null, AdditionalSearchPaths: BuildFolders);
			foreach (FileReference AutomationProjectFile in AutomationModuleFiles)
			{
				if (FileReference.Exists(AutomationProjectFile))
				{
					VCSharpProjectFile Project = new VCSharpProjectFile(AutomationProjectFile) { ShouldBuildForAllSolutionTargets = false/*true;*/ };
					AddExistingProjectFile(Project, bForceDevelopmentConfiguration: true);
					AutomationProjectFiles.Add(Project);
					Folder.ChildProjects.Add(Project);

					if (!AutomationProjectFile.IsUnderDirectory(BuildTool.EngineDirectory))
					{
						FileReference PropsFile = new FileReference(AutomationProjectFile.FullName + ".props");
						CreateAutomationProjectPropsFile(PropsFile);
					}
				}
			}
		}

		// Creates a .props file next to each automation project which specifies the path to the engine directory
		private void CreateAutomationProjectPropsFile(FileReference PropsFile)
		{
			using (FileStream Stream = FileReference.Open(PropsFile, FileMode.Create, FileAccess.Write, FileShare.Read))
			{
				using (StreamWriter Writer = new StreamWriter(Stream, Encoding.UTF8))
				{
					Writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
					Writer.WriteLine("<Project ToolsVersion=\"Current\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
					Writer.WriteLine("\t<PropertyGroup>");
					Writer.WriteLine("\t\t<EngineDir Condition=\"'$(EngineDir)' == ''\">{0}</EngineDir>", BuildTool.EngineDirectory);
					Writer.WriteLine("\t</PropertyGroup>");
					Writer.WriteLine("</Project>");
				}
			}
		}

		// Finds all csproj within Engine/Source/Programs, and add them if their CSharp.prog file exists.
		void DiscoverCSharpProgramProjects(MasterProjectFolder ProgramsFolder)
		{
			string[] UnsupportedPlatformNames = Utils.MakeListOfUnsupportedPlatforms(SupportedPlatforms, bIncludeUnbuildablePlatforms: true).ToArray();

			List<FileReference> FoundProjects = new List<FileReference>();

			DirectoryReference EngineExtras = DirectoryReference.Combine(BuildTool.EngineDirectory, "Extras");
			DiscoverCSharpProgramProjectsRecursively(EngineExtras, FoundProjects);

			// [0] = {D:\UERelease\Engine\Source\Programs}
			DirectoryReference[] AllEngineDirectories = BuildTool.GetAllEngineDirectories("Source/Programs");

			foreach (DirectoryReference EngineDir in AllEngineDirectories)
			{
				// AutomationToolLauncher\AutomationToolLauncher.csproj
				// BuildAgent, DotNETCommon\DotNETUtilities.csporj, 
				// GitDependencies 외 IOS, MemoryProfiler2, nDisplayLauncher 8개
				DiscoverCSharpProgramProjectsRecursively(EngineDir, FoundProjects);
			}

			foreach (FileReference FoundProject in FoundProjects)
			{
				foreach (DirectoryReference EngineDir in AllEngineDirectories)
				{
					if (FoundProject.IsUnderDirectory(EngineDir))
					{
						if (!FoundProject.ContainsAnyNames(UnsupportedPlatformNames, EngineDir))
						{
							VCSharpProjectFile Project = new VCSharpProjectFile(FoundProject);

							if (AllowDotNetCoreProjects || !Project.IsDotNETCoreProject())
							{
								Project.ShouldBuildForAllSolutionTargets = true;
								Project.ShouldBuildByDefaultForSolutionTargets = true;

								AddExistingProjectFile(Project, bForceDevelopmentConfiguration : false);
								ProgramsFolder.ChildProjects.Add(Project);
							}
						}
						break;
					}
				}
			}
		}

		private static void DiscoverCSharpProgramProjectsRecursively(DirectoryReference SearchFolder, List<FileReference> FoundProjects)
		{
			// Scan all the files in this directory
			bool bSearchSubFolders = true;
			foreach (FileReference File in DirectoryLookupCache.EnumerateFiles(SearchFolder))
			{
				// If we find a csproj or sln, we should not recurse this directory.
				bool bIsCsProj = File.HasExtension(".csproj");
				bool bIsSln = File.HasExtension(".sln");
				bSearchSubFolders &= !(bIsCsProj || bIsSln);
				// If we found an sln, ignore completely.
				if (bIsSln)
				{
					break;
				}
				// For csproj files, add them to the sln if the CSharp.prog file also exists.
				if (bIsCsProj && FileReference.Exists(FileReference.Combine(SearchFolder, "CSharp.prog")))
				{
					FoundProjects.Add(File);
				}
			}

			// If we didn't find anything to stop the search, search all the subdirectories too
			if (bSearchSubFolders)
			{
				foreach (DirectoryReference SubDirectory in DirectoryLookupCache.EnumerateDirectories(SearchFolder))
				{
					DiscoverCSharpProgramProjectsRecursively(SubDirectory, FoundProjects);
				}
			}
		}
		
		// Finds the game projects that we're generating project files for
		// <returns>List of project files</returns>
		public List<FileReference> FindGameProjects()
		{
			List<FileReference> ProjectFiles = new List<FileReference>();
			if (OnlyGameProject != null)
			{
				ProjectFiles.Add(OnlyGameProject);
			}
			else
			{
				ProjectFiles.AddRange(NativeProjects.EnumerateProjectFiles());
			}
			return ProjectFiles;
		}

		// Gets the user's preferred IDE from their editor settings
		// <param name="ProjectFile">Project file being built</param>
		// <param name="Format">Preferred format for the project being built</param>
		// <returns>True if a preferred IDE was set, false otherwise</returns>
		public static bool GetPreferredSourceCodeAccessor(FileReference ProjectFile, out ProjectFileFormat Format)
		{
			ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.EditorSettings, DirectoryReference.FromFile(ProjectFile), BuildHostPlatform.Current.Platform);

			if (Ini.GetString("/Script/SourceCodeAccess.SourceCodeAccessSettings", "PreferredAccessor", out string PreferredAccessor))
			{
				PreferredAccessor = PreferredAccessor.ToLowerInvariant();
				if (PreferredAccessor == "clionsourcecodeaccessor")
				{
					Format = ProjectFileFormat.CLion;
					return true;
				}
				else if (PreferredAccessor == "codelitesourcecodeaccessor")
				{
					Format = ProjectFileFormat.CodeLite;
					return true;
				}
				else if (PreferredAccessor == "xcodesourcecodeaccessor")
				{
					Format = ProjectFileFormat.XCode;
					return true;
				}
				else if (PreferredAccessor == "visualstudiocode")
				{
					Format = ProjectFileFormat.VisualStudioCode;
					return true;
				}
				else if (PreferredAccessor == "kdevelopsourcecodeaccessor")
				{
					Format = ProjectFileFormat.KDevelop;
					return true;
				}
				else if (PreferredAccessor == "visualstudiosourcecodeaccessor")
				{
					Format = ProjectFileFormat.VisualStudio;
					return true;
				}
				else if (PreferredAccessor == "visualstudio2015")
				{
					Format = ProjectFileFormat.VisualStudio2015;
					return true;
				}
				else if (PreferredAccessor == "visualstudio2017")
				{
					Format = ProjectFileFormat.VisualStudio2017;
					return true;
				}
				else if (PreferredAccessor == "visualstudio2019")
				{
					Format = ProjectFileFormat.VisualStudio2019;
					return true;
				}
			}

			Format = ProjectFileFormat.VisualStudio;
			return false;
		}

		// Generates a Visual Studio solution file and Visual C++ project files for all known engine and game targets.
		// Does not actually build anything.
		public virtual bool GenerateProjectFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators, String[] Arguments)
		{
			bool bSuccess = true;

			// Parse project generator options
			bool IncludeAllPlatforms = true;

			// 여기서 InlcudeEnginePrograms 확정
			ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);

			if (bGeneratingGameProjectFiles || BuildTool.IsEngineInstalled())
			{
				Log.TraceInformation("Discovering modules, targets and source code for project...");

				MasterProjectPath = OnlyGameProject.Directory;

				// Set the project file name
				MasterProjectName = OnlyGameProject.GetFileNameWithoutExtension();

				if (!DirectoryReference.Exists(DirectoryReference.Combine(MasterProjectPath, "Source")))
				{
					if (!DirectoryReference.Exists(DirectoryReference.Combine(MasterProjectPath, "Intermediate", "Source")))
					{
						if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
						{
							MasterProjectPath = BuildTool.EngineDirectory;
							GameProjectName = "DefaultGame";
						}
						if (!DirectoryReference.Exists(DirectoryReference.Combine(MasterProjectPath, "Source")))
						{
							throw new BuildException("Directory '{0}' is missing 'Source' folder.", MasterProjectPath);
						}
					}
				}
				IntermediateProjectFilesPath = DirectoryReference.Combine(MasterProjectPath, "Intermediate", "ProjectFiles");
			}
			else
			{
				// Set the master project name from the folder name

				if (Environment.GetEnvironmentVariable("UE_NAME_PROJECT_AFTER_FOLDER") == "1")
				{
					MasterProjectName += "_" + Path.GetFileName(MasterProjectPath.ToString());
				}
				else if (bMasterProjectNameFromFolder)
				{
					string NewMasterProjectName = MasterProjectPath.GetDirectoryName();

					if (!String.IsNullOrEmpty(NewMasterProjectName))
					{
						MasterProjectName = NewMasterProjectName;
					}
				}

				// Write out the name of the master project file, so the runtime knows to use it
				FileReference MasterProjectNameLocation = FileReference.Combine(BuildTool.EngineDirectory, "Intermediate", "ProjectFiles", "MasterProjectName.txt");
				DirectoryReference.CreateDirectory(MasterProjectNameLocation.Directory);
				FileReference.WriteAllText(MasterProjectNameLocation, MasterProjectName);
			}

			// Modify the name if specific platforms were given
			if (0 < ProjectPlatforms.Count && bAppendPlatformSuffix)
			{
				// Sort the platforms names so we get consistent names
				List<string> SortedPlatformNames = new List<string>();

				foreach (BuildTargetPlatform SpecificPlatform in ProjectPlatforms)
				{
					SortedPlatformNames.Add(SpecificPlatform.ToString());
				}

				SortedPlatformNames.Sort();

				MasterProjectName += "_";

				foreach (string SortedPlatform in SortedPlatformNames)
				{
					MasterProjectName += SortedPlatform;
					IntermediateProjectFilesPath = new DirectoryReference(IntermediateProjectFilesPath.FullName + SortedPlatform);
				}

				// the master project name is always read from our intermediate directory and not the overriden one for this set of platforms
				FileReference MasterProjectNameLocation = FileReference.Combine(BuildTool.EngineDirectory, "Intermediate", "ProjectFiles", "MasterProjectName.txt");
				DirectoryReference.CreateDirectory(MasterProjectNameLocation.Directory);
				FileReference.WriteAllText(MasterProjectNameLocation, MasterProjectName);
			}

			bool bCleanProjectFiles = Arguments.Any(x => x.Equals("-CleanProjects", StringComparison.InvariantCultureIgnoreCase));

			if (bCleanProjectFiles)
			{
				CleanProjectFiles(MasterProjectPath, MasterProjectName, IntermediateProjectFilesPath);
			}

			// Figure out which platforms we should generate project files for.

			SetupSupportedPlatformsAndConfigurations(IncludeAllPlatforms: IncludeAllPlatforms, SupportedPlatformNames: out string SupportedPlatformNames);

			Log.TraceVerbose("Detected supported platforms: " + SupportedPlatformNames);

			RootFolder = AllocateMasterProjectFolder(this, "<Root>");

			// Build the list of games to generate projects for
			List<FileReference> AllGameProjects = FindGameProjects();

			// Find all of the target files.
			// This will filter out any modules or targets that don't belong to platforms we're generating project files for.
			List<FileReference> AllTargetFiles = DiscoverTargets(AllGameProjects);

			// Sort the targets by name. When we have multiple targets of a given type for a project,
			// we'll use the order to determine which goes in the primary project file
			// (so that client names with a suffix will go into their own project).
			AllTargetFiles = AllTargetFiles.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).ToList();

			// Remove any game projects that don't have a target
			AllGameProjects.RemoveAll(x => !AllTargetFiles.Any(y => y.IsUnderDirectory(x.Directory)));

			// Find all of the module files.
			// This will filter out any modules or targets that don't belong to platforms
			// we're generating project files for.
			List<FileReference> AllModuleFiles = DiscoverModules(AllGameProjects);

			ProjectFile                            EngineProject;     // {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
			ProjectFile                            EnterpriseProject; // null
			List<ProjectFile>                      GameProjects;      // 0
			List<ProjectFile>                      ModProjects;       // 0
			Dictionary<FileReference, ProjectFile> ProgramProjects;   // *.vcxproj (42개)

			{
				// Setup buildable projects for all targets
				AddProjectsForAllTargets
				(
					// PlatformProjectGenerators,
					AllGameProjects,
					AllTargetFiles,
					Arguments,
					out EngineProject,     // null -> EngineCode_UnitTest.vcxproj ->::ProjectTargets { Client.Target, Editor.Target, Game.Target, Server.Target}
					out EnterpriseProject, // null -> null
					out GameProjects,      // null -> count 0
					out ProgramProjects    // count = 0 -> 42
				);

				// Add projects for mods
				AddProjectsForMods(GameProjects, out ModProjects);

				// Add all game projects and game config files
				AddAllGameProjects(GameProjects/*, SupportedPlatformNames, RootFolder*/);

				// Set the game to be the default project
				if (0 < ModProjects.Count)
				{
					DefaultProject = ModProjects.First();
				}
				else if (bGeneratingGameProjectFiles && GameProjects.Count > 0)
				{
					DefaultProject = GameProjects.First();
				}

				//Related Debug Project Files - Tuple here has the related Debug Project, SolutionFolder
				List<Tuple<ProjectFile, string>> DebugProjectFiles = new List<Tuple<ProjectFile, string>>();

				// Place projects into root level solution folders
				if (bIncludeEngineSourceInSolution)
				{
					// If we're still missing an engine project because we don't have any targets for it, make one up.
					if (EngineProject == null)
					{
						FileReference ProjectFilePath = FileReference.Combine(IntermediateProjectFilesPath, EngineProjectFileNameBase + ProjectFileExtension);

						EngineProject = FindOrAddProject(ProjectFilePath, BuildTool.EngineDirectory, true, out bool bAlreadyExisted);
						EngineProject.IsForeignProject   = false;
						EngineProject.IsGeneratedProject = true;
						EngineProject.IsStubProject      = true;
					}

					if (EngineProject != null)
					{
						RootFolder.AddSubFolder("Engine").ChildProjects.Add(EngineProject);

						// Engine config files
						if (bIncludeConfigFiles)
						{
							AddEngineConfigFiles(EngineProject);
							if (IncludeEnginePrograms)
							{
								AddHeaderToolConfigFiles(EngineProject);   // Add "D:\\UERelease\\Engine\\Saved\\BuildTool\\BuildConfiguration.xml"
								AddBuildToolConfigFilesToEngineProject(EngineProject); // Add "C:\\Users\\sunkyung\\AppData\\Roaming\\Engine\\BuildTool\\BuildConfiguration.xml"
							}
						}

						// Engine Extras files
						AddEngineExtrasFiles(EngineProject); // Do Nothing

						// Platform Extension files
						// AutomationTool, Binaries, Content을 제외한 모든 *.Build.cs 포함
						AddPlatformExtensionFiles(EngineProject); // Add to EngineProject.SourceFileMap, EngineProject.SourceFiles

						// Engine localization files
						if (bIncludeLocalizationFiles)
						{
							AddEngineLocalizationFiles(EngineProject);
						}

						// Engine template files
						if (bIncludeTemplateFiles)
						{
							AddEngineTemplateFiles(EngineProject);
						}

						if (bIncludeShaderSourceInProject)
						{
							Log.TraceVerbose("Adding shader source code...");

							// Find shader source files and generate stub project
							AddEngineShaderSource(EngineProject);
						}

						if (bIncludeBuildSystemFiles)
						{
							Log.TraceVerbose("Adding build system files...");
							// BuildDirectory = {D:\UERelease\Engine\Build}
							AddEngineBuildFiles(EngineProject);
						}

						if (bIncludeDocumentation)
						{
							AddEngineDocumentation(EngineProject);
						}

						List<Tuple<ProjectFile, string>> NewProjectFiles = EngineProject.WriteDebugProjectFiles(SupportedPlatforms, SupportedConfigurations, PlatformProjectGenerators);

						if (NewProjectFiles != null)
						{
							DebugProjectFiles.AddRange(NewProjectFiles);
						}
					}

					if (EnterpriseProject != null)
					{
						RootFolder.AddSubFolder(BuildTool.EnterpriseDirectory.GetDirectoryName()).ChildProjects.Add(EnterpriseProject);
					}

					foreach (ProjectFile CurModProject in ModProjects)
					{
						RootFolder.AddSubFolder("Mods").ChildProjects.Add(CurModProject);
					}

					// {D:\UERelease\ Templates, Samples, Enterprise\Templates, Enterprise\Samples
					DirectoryReference TemplatesDirectory           = DirectoryReference.Combine(BuildTool.RootDirectory,       "Templates");
					DirectoryReference SamplesDirectory             = DirectoryReference.Combine(BuildTool.RootDirectory,       "Samples"  );
					DirectoryReference EnterpriseTemplatesDirectory = DirectoryReference.Combine(BuildTool.EnterpriseDirectory, "Templates");
					DirectoryReference EnterpriseSamplesDirectory   = DirectoryReference.Combine(BuildTool.EnterpriseDirectory, "Samples"  );

					foreach (ProjectFile CurGameProject in GameProjects)
					{
						// Templates go under a different solution folder than games
						FileReference BuildProjectFile = CurGameProject.ProjectTargets.First().ProjectFilePath;
						if (BuildProjectFile.IsUnderDirectory(TemplatesDirectory) || BuildProjectFile.IsUnderDirectory(EnterpriseTemplatesDirectory))
						{
							DirectoryReference TemplateGameDirectory = CurGameProject.BaseDir;
							if (TemplateGameDirectory.IsUnderDirectory((BuildTool.EnterpriseDirectory)))
							{
								RootFolder.AddSubFolder(BuildTool.EnterpriseDirectory.GetDirectoryName() + Path.DirectorySeparatorChar + "Templates").ChildProjects.Add(CurGameProject);
							}
							else
							{
								RootFolder.AddSubFolder("Templates").ChildProjects.Add(CurGameProject);
							}
						}
						else if (BuildProjectFile.IsUnderDirectory(SamplesDirectory) || BuildProjectFile.IsUnderDirectory(EnterpriseSamplesDirectory))
						{
							DirectoryReference SampleGameDirectory = CurGameProject.BaseDir;
							if (SampleGameDirectory.IsUnderDirectory((BuildTool.EnterpriseDirectory)))
							{
								RootFolder.AddSubFolder(BuildTool.EnterpriseDirectory.GetDirectoryName() + Path.DirectorySeparatorChar + "Samples").ChildProjects.Add(CurGameProject);
							}
							else
							{
								RootFolder.AddSubFolder("Samples").ChildProjects.Add(CurGameProject);
							}
						}
						else
						{
							RootFolder.AddSubFolder("Games").ChildProjects.Add(CurGameProject);
						}

						List<Tuple<ProjectFile, string>> NewProjectFiles = CurGameProject.WriteDebugProjectFiles(SupportedPlatforms, SupportedConfigurations, PlatformProjectGenerators);

						if (NewProjectFiles != null)
						{
							DebugProjectFiles.AddRange(NewProjectFiles);
						}

					}

					//Related Debug Project Files - Tuple has the related Debug Project, SolutionFolder
					foreach (Tuple<ProjectFile, string> DebugProjectFile in DebugProjectFiles)
					{
						AddExistingProjectFile(DebugProjectFile.Item1, bForceDevelopmentConfiguration: false);

						//add it to the Android Debug Projects folder in the solution
						RootFolder.AddSubFolder(DebugProjectFile.Item2).ChildProjects.Add(DebugProjectFile.Item1);
					}

					foreach (KeyValuePair<FileReference, ProjectFile> CurProgramProject in ProgramProjects)
					{
						ProjectTarget Target = CurProgramProject.Value.ProjectTargets.FirstOrDefault(t => t.TargetRules.SolutionDirectory.HasValue());

						if (Target != null)
						{
							RootFolder.AddSubFolder(Target.TargetRules.SolutionDirectory).ChildProjects.Add(CurProgramProject.Value);
						}
						else
						{
							if (CurProgramProject.Key.IsUnderDirectory(BuildTool.EnterpriseDirectory))
							{
								RootFolder.AddSubFolder(BuildTool.EnterpriseDirectory.GetDirectoryName() + Path.DirectorySeparatorChar + "Programs").ChildProjects.Add(CurProgramProject.Value);
							}
							else
							{
								RootFolder.AddSubFolder("Programs").ChildProjects.Add(CurProgramProject.Value);
							}
						}
					}

					// Add all of the config files for generated program targets
					AddEngineExternalToolConfigFiles(ProgramProjects);
				}
			}

			// Setup "stub" projects for all modules
			AddProjectsForAllModules(AllGameProjects, ProgramProjects, ModProjects, AllModuleFiles, bGatherThirdPartySource);

			// EnginePrograms => UBT + AUT + Source/Programs  *.csproj
			if (IncludeEnginePrograms)
			{
				// ProgramsFolder in ProjectFileGenerator::SubFolders (List<MasterProjectFolder>)
				MasterProjectFolder ProgramsFolder = RootFolder.AddSubFolder("Programs");

				// Add BuildTool to the master project
				AddBuildToolProject(ProgramsFolder);

				// Add AutomationTool to the master project
				// ProgramsFolder.ChildProjects = Count = 27
				ProgramsFolder.ChildProjects.Add(AddSimpleCSharpProject("AutomationTool", bShouldBuildForAllSolutionTargets: true, bForceDevelopmentConfiguration: true));

				// Add automation.csproj files to the master project
				AddAutomationModules(AllGameProjects, ProgramsFolder); // Put *.automation.csproj at ProjectFileGenerator::AutomationProject

				// Discover C# programs which should additionally be included in the solution.
				DiscoverCSharpProgramProjects(ProgramsFolder);

				// ProgramsFolder ( = ProjectFileGenerator.SubFolders )
				// RootPath의 SubDirectories에 있는 모든 *.vcxproj, *.csproj
				// ...\Intermediate\ProjectFiles\*.vcxproj
				// Source\Programs\ { BuildTool, AutomationTool, AutomationToolLauncher, BuildAgent ,DotNETUtilities, GitDependencies, MemoryProfiler2, NDisplay/Listener }
			}

			// Eliminate all redundant master project folders.
			// E.g., folders which contain only one project and that project
			// has the same name as the folder itself.  To the user, projects "feel like" folders already in the IDE, so we
			// want to collapse them down where possible.
			RecursivelyEliminateRedundantMasterProjectSubFolders(RootFolder, "");

			{
				// Figure out which targets we need about IntelliSense for.
				// We only need to worry about targets for projects that we're actually generating in this session.
				List<Tuple<ProjectFile, ProjectTarget>> IntelliSenseTargetFiles = new List<Tuple<ProjectFile, ProjectTarget>>();
				
				{
					// Engine targets
					if (EngineProject != null)
					{
						// {Client.Target}, {Editor.Target}, {Game.Target}, {Server.Target}
						foreach (ProjectTarget ProjectTarget in EngineProject.ProjectTargets)
						{
							if (ProjectTarget.TargetFilePath != null)
							{
								// Only bother with the editor target
								// We want to make sure that definitions are setup to be as inclusive as possible
								// for good quality IntelliSense.  For example, we want WITH_EDITORONLY_DATA=1, so using the editor targets works well.
								if (ProjectTarget.TargetRules.Type == TargetType.Editor)
								{
									// EngineProject = {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
									// ProjectTarget = {Editor.Target}
									IntelliSenseTargetFiles.Add(Tuple.Create(EngineProject, ProjectTarget));
								}
							}
						}
					}

					// Enterprise targets
					if (EnterpriseProject != null)
					{
						foreach (ProjectTarget ProjectTarget in EnterpriseProject.ProjectTargets)
						{
							if (ProjectTarget.TargetFilePath != null)
							{
								// Only bother with the editor target.
								// We want to make sure that definitions are setup to be as inclusive as possible for good quality IntelliSense.
								// For example, we want WITH_EDITORONLY_DATA=1, so using the editor targets works well.
								if (ProjectTarget.TargetRules.Type == TargetType.Editor)
								{
									// [0] = {(D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj, Editor.Target)}
									IntelliSenseTargetFiles.Add(Tuple.Create(EnterpriseProject, ProjectTarget));
								}
							}
						}
					}

					// Program targets
					foreach (ProjectFile ProgramProject in ProgramProjects.Values)
					{
						foreach (ProjectTarget ProjectTarget in ProgramProject.ProjectTargets)
						{
							if (ProjectTarget.TargetFilePath != null)
							{
								IntelliSenseTargetFiles.Add(Tuple.Create(ProgramProject, ProjectTarget));
								// [1] = {(D:\UERelease\Engine\Intermediate\ProjectFiles\BenchmarkTool.vcxproj, BenchmarkTool.Target)}
								// ...
								// [42] = {(D:\UERelease\Engine\Intermediate\ProjectFiles\BootstrapPackagedGame.vcxproj, BootstrapPackagedGame.Target)}
							}
						}
					}

					// Game/template targets
					foreach (ProjectFile GameProject in GameProjects)
					{
						foreach (ProjectTarget ProjectTarget in GameProject.ProjectTargets)
						{
							if (ProjectTarget.TargetFilePath != null)
							{
								// Only bother with the editor target.
								// We want to make sure that definitions are setup to be as inclusive as possible for good quality IntelliSense.
								// For example, we want WITH_EDITORONLY_DATA=1, so using the editor targets works well.
								if (ProjectTarget.TargetRules.Type == TargetType.Editor)
								{
									IntelliSenseTargetFiles.Add(Tuple.Create(GameProject, ProjectTarget));
								}
							}
						}
					}
				}

				// write out any additional debug information for the solution (such as VS configuration)
				WriteDebugSolutionFiles(PlatformProjectGenerators, IntermediateProjectFilesPath);

				// 여기서부터 시작.

				// Generate IntelliSense data if we need to.
				// This involves having UBT simulate the action compilation of
				// the targets so that we can extra the compiler defines, include paths, etc.
				// 바인딩하는 단계

				List<Tuple<ProjectFile, ProjectTarget>> DebugIntelliSenseTargetFiles = new List<Tuple<ProjectFile, ProjectTarget>>
				{
					IntelliSenseTargetFiles[0]
				};
				// IntelliSenseTargetFiles = DebugIntellisenseTargetFiles;

#if DEBUG
				GenerateIntelliSenseData(Arguments, DebugIntelliSenseTargetFiles);

				// Write the project files
				// 직접 쓰는 단계
				// Directory 만들 때 가장 주목.
				WriteProjectFiles(PlatformProjectGenerators);
# else
				GenerateIntelliSenseData(Arguments, IntelliSenseTargetFiles);

				// Write the project files
				// 직접 쓰는 단계
				WriteProjectFiles(PlatformProjectGenerators);
#endif
				Log.TraceVerbose("Project generation complete ({0} generated, {1} imported)", GeneratedProjectFiles.Count, OtherProjectFiles.Count);

				// Generate all the target info files for the editor
				foreach (FileReference ProjectFile in AllGameProjects)
				{
					RulesAssembly RulesAssembly = RulesCompiler.CreateProjectRulesAssembly(ProjectFile, false, false);
				}
			}

			return bSuccess;
		}

		// Adds detected UBT configuration files (BuildConfiguration.xml) to engine project.
		// <param name="EngineProject">Engine project to add files to.</param>
		private void AddBuildToolConfigFilesToEngineProject(ProjectFile EngineProject)
		{
			EngineProject.AddAliasedFileToProject
			(
				new AliasedFile
				(
					XMLConfig.GetSchemaLocation(),
					XMLConfig.GetSchemaLocation().FullName,
					Path.Combine("Programs", "BuildTool")
				));

			List<XMLConfig.InputFile> InputFiles = XMLConfig.FindInputFiles();
			foreach (XMLConfig.InputFile InputFile in InputFiles)
			{
				EngineProject.AddAliasedFileToProject
					(
						new AliasedFile
						(
							InputFile.Location,
							InputFile.Location.FullName,
							Path.Combine("Config", "BuildTool", InputFile.FolderName)
						)
					);
			}
		}

		// Clean project files
		public virtual void CleanProjectFiles(DirectoryReference InMasterProjectDirectory, string InMasterProjectName, DirectoryReference InIntermediateProjectFilesDirectory)
        {
        }

		// Configures project generator based on command-line options
		// <param name="Arguments">Arguments passed into the program</param>
		// <param name="IncludeAllPlatforms">True if all platforms should be included</param>
		protected virtual void ConfigureProjectFileGeneration(String[] Arguments, ref bool IncludeAllPlatforms)
		{
			if (PlatformNames != null)
			{
				foreach (string PlatformName in PlatformNames)
				{
					if (BuildTargetPlatform.TryParse(PlatformName, out BuildTargetPlatform Platform) && 
						!ProjectPlatforms.Contains(Platform))
					{
						ProjectPlatforms.Add(Platform);
					}
				}
			}

			bool bAlwaysIncludeEngineModules = false;
			foreach (string CurArgument in Arguments)
			{
				if (CurArgument.StartsWith("-"))
				{
					if (CurArgument.StartsWith("-Platforms=", StringComparison.InvariantCultureIgnoreCase))
					{
						// Parse the list... will be in Foo+Bar+New format
						string PlatformList = CurArgument.Substring(11);
						while (0 < PlatformList.Length)
						{
							string PlatformString = PlatformList;
							Int32 PlusIdx = PlatformList.IndexOf("+");
							if (PlusIdx != -1)
							{
								PlatformString = PlatformList.Substring(0, PlusIdx);
								PlatformList = PlatformList.Substring(PlusIdx + 1);
							}
							else
							{
								// We are on the last platform... clear the list to exit the loop
								PlatformList = "";
							}

							// Is the string a valid platform? If so, add it to the list
							if (BuildTargetPlatform.TryParse(PlatformString, out BuildTargetPlatform SpecifiedPlatform))
							{
								if (ProjectPlatforms.Contains(SpecifiedPlatform) == false)
								{
									ProjectPlatforms.Add(SpecifiedPlatform);
								}
							}
							else
							{
								Log.TraceWarning("ProjectFiles invalid platform specified: {0}", PlatformString);
							}

							// Append the platform suffix to the solution name
							bAppendPlatformSuffix = true;
						}
					}
					else switch (CurArgument.ToUpperInvariant())
						{
							case "-ALLPLATFORMS":
								IncludeAllPlatforms = true;
								break;

							case "-CURRENTPLATFORM":
								IncludeAllPlatforms = false;
								break;

							case "-THIRDPARTY":
								bGatherThirdPartySource = true;
								break;

							case "-GAME":
								// Generates project files for a single game
								bGeneratingGameProjectFiles = true;
								break;

							case "-ENGINE":
								// Forces engine modules and targets to be included in game-specific project files
								bAlwaysIncludeEngineModules = true;
								break;

							case "-NOINTELLISENSE":
								bGenerateIntelliSenseData = false;
								break;

							case "-INTELLISENSE":
								bGenerateIntelliSenseData = true;
								break;

							case "-SHIPPINGCONFIGS":
								bIncludeTestAndShippingConfigs = true;
								break;

							case "-NOSHIPPINGCONFIGS":
								bIncludeTestAndShippingConfigs = false;
								break;

							case "-ALLLANGUAGES":
								bAllDocumentationLanguages = true;
								break;

							case "-USEPRECOMPILED":
								bUsePrecompiled = true;
								break;

							case "-VSCODE":
								bIncludeDotNETCoreProjects = true;
								break;

							case "-INCLUDETEMPTARGETS":
								bIncludeTempTargets = true;
								break;
						}
				}
			}

			if (bGeneratingGameProjectFiles || BuildTool.IsEngineInstalled())
			{
				if (OnlyGameProject == null)
				{
					throw new BuildException("A game project path was not specified, which is required when generating project files using an installed build or passing -game on the command line");
				}

				GameProjectName = OnlyGameProject.GetFileNameWithoutExtension();
				if (String.IsNullOrEmpty(GameProjectName))
				{
					throw new BuildException("A valid game project was not found in the specified location (" + OnlyGameProject.Directory.FullName + ")");
				}

				bool bInstalledEngineWithSource = BuildTool.IsEngineInstalled() && DirectoryReference.Exists(BuildTool.EngineSourceDirectory);

				bIncludeEngineSourceInSolution = bAlwaysIncludeEngineModules || bInstalledEngineWithSource;
				bIncludeDocumentation = false;
				bIncludeBuildSystemFiles = false;
				bIncludeShaderSourceInProject = true;
				bIncludeTemplateFiles = false;
				bIncludeConfigFiles = true;
				IncludeEnginePrograms = bAlwaysIncludeEngineModules;
			}
			else
			{
				// At least one extra argument was specified, but we weren't expected it.  Ignored.
			}

			// If we're generating a solution for only one project, only include the enterprise folder if it's an enterprise project
			if (OnlyGameProject == null)
			{
				bIncludeEnterpriseSource = bIncludeEngineSourceInSolution;
			}
			else
			{
				bIncludeEnterpriseSource = bIncludeEngineSourceInSolution && UProjectDescriptor.FromFile(OnlyGameProject).IsEnterpriseProject;
			}
		}

		// Adds all game project files, including target projects and config files
		protected void AddAllGameProjects(List<ProjectFile> GameProjects/*, string SupportedPlatformNames, MasterProjectFolder ProjectsFolder*/)
		{
			HashSet<DirectoryReference> UniqueGameProjectDirectories = new HashSet<DirectoryReference>();
			foreach (ProjectFile ItrGameProject in GameProjects)
			{
				DirectoryReference GameProjectDirectory = ItrGameProject.BaseDir;
				if (UniqueGameProjectDirectories.Add(GameProjectDirectory))
				{
					// @todo projectfiles: We have engine localization files, but should we also add GAME localization files?

					ItrGameProject.AddFilesToProject(SourceFileSearch.FindFiles(GameProjectDirectory, SearchSubdirectories: false), GameProjectDirectory);

					DirectoryReference GamePlatformsDirectory = BuildTool.AppendSuffixPlatforms(GameProjectDirectory);
					if (DirectoryReference.Exists(GamePlatformsDirectory))
					{
						ItrGameProject.AddFilesToProject(SourceFileSearch.FindFiles(GamePlatformsDirectory), GameProjectDirectory);
					}

					// Game config files
					if (bIncludeConfigFiles)
					{
						DirectoryReference GameConfigDirectory = DirectoryReference.Combine(GameProjectDirectory, "Config");
						if (DirectoryReference.Exists(GameConfigDirectory))
						{
							ItrGameProject.AddFilesToProject(SourceFileSearch.FindFiles(GameConfigDirectory), GameProjectDirectory);
						}
					}

					// Game build files
					if (bIncludeBuildSystemFiles)
					{
						DirectoryReference GameBuildDirectory = DirectoryReference.Combine(GameProjectDirectory, "Build");
						if (DirectoryReference.Exists(GameBuildDirectory))
						{
							List<string> SubdirectoryNamesToExclude = new List<string>
							{
								"Receipts",
								"Scripts",
								"FileOpenOrder",
								"PipelineCaches"
							};

							ItrGameProject.AddFilesToProject(SourceFileSearch.FindFiles(GameBuildDirectory, SubdirectoryNamesToExclude), GameProjectDirectory);
						}
					}

					DirectoryReference GameShaderDirectory = DirectoryReference.Combine(GameProjectDirectory, "Shaders");
					if (DirectoryReference.Exists(GameShaderDirectory))
					{
						ItrGameProject.AddFilesToProject(SourceFileSearch.FindFiles(GameShaderDirectory), GameProjectDirectory);
					}
				}
			}
		}

		// Adds all engine localization text files to the specified project
		private void AddEngineLocalizationFiles(ProjectFile EngineProject)
		{
			DirectoryReference EngineLocalizationDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Content", "Localization");
			if (DirectoryReference.Exists(EngineLocalizationDirectory))
			{
				EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(EngineLocalizationDirectory), BuildTool.EngineDirectory);
			}
		}

		// Adds all engine template text files to the specified project
		private void AddEngineTemplateFiles(ProjectFile EngineProject)
		{
			// { D:\UERelease\Engine\Content\Editor\Templates}
			DirectoryReference EngineTemplateDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Content", "Editor", "Templates");

			if (DirectoryReference.Exists(EngineTemplateDirectory))
			{
				List<FileReference> TempFileReferences = SourceFileSearch.FindFiles(EngineTemplateDirectory);

				EngineProject.AddFilesToProject(TempFileReferences, BuildTool.EngineDirectory);
			}
		}

		// Adds all engine config files to the specified project
		private void AddEngineConfigFiles(ProjectFile EngineProject)
		{
			DirectoryReference EngineConfigDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Config");
			if (DirectoryReference.Exists(EngineConfigDirectory))
			{
				EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(EngineConfigDirectory), BuildTool.EngineDirectory);
			}
		}

		// Adds all engine extras files to the specified project
		protected virtual void AddEngineExtrasFiles(ProjectFile EngineProject)
		{
		}

		// Adds additional files from the platform extensions folder
		protected virtual void AddPlatformExtensionFiles(ProjectFile EngineProject)
		{
			// @todo: this will add the same files to the solution (like the UBT source files that also get added to BuildTool project).
			// not sure of a good filtering method here
			DirectoryReference PlatformExtensionsDirectory = BuildTool.EnginePlatformExtensionsDirectory;
			if (DirectoryReference.Exists(PlatformExtensionsDirectory))
			{
				List<string> SubdirectoryNamesToExclude = new List<string>
				{
					"AutomationTool", //automation files are added separately to the AutomationTool project
					"Binaries",
					"Content"
				};
				// BuildTool.EngineDirectory = {D:\UERelease\Engine}
				// PlatformExtensionsDirectory     = {D:\UERelease\Engine\Platforms}
				EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(PlatformExtensionsDirectory, SubdirectoryNamesToExclude), BuildTool.EngineDirectory);
			}
		}

		// Adds HeaderTool config files to the specified project
		private void AddHeaderToolConfigFiles(ProjectFile EngineProject)
		{
			DirectoryReference UHTConfigDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Programs", "HeaderTool", "Config");
			if (DirectoryReference.Exists(UHTConfigDirectory))
			{
				EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(UHTConfigDirectory), BuildTool.EngineDirectory);
			}
		}

		// Finds any additional plugin files.
		// <returns>List of additional plugin files</returns>
		private List<FileReference> DiscoverExtraPlugins(List<FileReference> AllGameProjects)
		{
			List<FileReference> AddedPlugins = new List<FileReference>();

			foreach (FileReference GameProject in AllGameProjects)
			{
				// Check the user preference to see if they'd like to include nativized assets as a generated project.
				bool bIncludeNativizedAssets = false;
				ConfigHierarchy Config = ConfigCache.ReadHierarchy(ConfigHierarchyType.Game, GameProject.Directory, BuildHostPlatform.Current.Platform);
				if (Config != null)
				{
					Config.TryGetValue("/Script/EditorEd.ProjectPackagingSettings", "bIncludeNativizedAssetsInProjectGeneration", out bIncludeNativizedAssets);
				}

				// Note: Whether or not we include nativized assets here has no bearing on whether or not they actually get built.
				if (bIncludeNativizedAssets)
				{
					AddedPlugins.AddRange(Plugins.EnumeratePlugins(DirectoryReference.Combine(GameProject.Directory, "Intermediate", "Plugins")).Where(x => x.GetFileNameWithoutExtension() == "NativizedAssets"));
				}
			}
			return AddedPlugins;
		}

		// Finds all module files (filtering by platform)
		// <returns>Filtered list of module files</returns>
		protected List<FileReference> DiscoverModules(List<FileReference> AllGameProjects)
		{
			List<FileReference> AllModuleFiles = new List<FileReference>();

			// Locate all modules (*.Build.cs files)
			List<FileReference> FoundModuleFiles = RulesCompiler.FindAllRulesSourceFiles(RulesCompiler.RulesFileType.Module, GameFolders: AllGameProjects.Select(x => x.Directory).ToList(), ForeignPlugins: DiscoverExtraPlugins(AllGameProjects), AdditionalSearchPaths: null);
			foreach (FileReference BuildFileName in FoundModuleFiles)
			{
				AllModuleFiles.Add(BuildFileName);
			}
			return AllModuleFiles;
		}

		// List of non-redistributable folders
		private static readonly string[] NoRedistFolders = new string[]
		{
			Path.DirectorySeparatorChar + "NoRedist"        + Path.DirectorySeparatorChar,
			Path.DirectorySeparatorChar + "NotForLicensees" + Path.DirectorySeparatorChar
		};

        // Checks if a module is in a non-redistributable folder
#pragma warning disable IDE0051 // Remove unused private members
        private static bool IsNoRedistModule(FileReference ModulePath)
#pragma warning restore IDE0051 // Remove unused private members
        {
			foreach (string NoRedistFolderName in NoRedistFolders)
			{
				if (0 <= ModulePath.FullName.IndexOf(NoRedistFolderName, StringComparison.InvariantCultureIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		// Finds all target files (filtering by platform)
		// <returns>Filtered list of target files</returns>
		protected List<FileReference> DiscoverTargets(List<FileReference> AllGameProjects)
		{
			List<FileReference> AllTargetFiles = new List<FileReference>();

			// Make a list of all platform name strings that we're *not* including in the project files
			List<string> UnsupportedPlatformNameStrings = Utils.MakeListOfUnsupportedPlatforms(SupportedPlatforms, bIncludeUnbuildablePlatforms: true);

			// Locate all targets (*.Target.cs files)
			List<FileReference> FoundTargetFiles = RulesCompiler.FindAllRulesSourceFiles(RulesCompiler.RulesFileType.Target, AllGameProjects.Select(x => x.Directory).ToList(), ForeignPlugins: DiscoverExtraPlugins(AllGameProjects), AdditionalSearchPaths: null, bIncludeTempTargets: bIncludeTempTargets);
			foreach (FileReference CurTargetFile in FoundTargetFiles)
			{
				string CleanTargetFileName = StringUtils.CleanDirectorySeparators(CurTargetFile.FullName);

				// remove the local root
				string LocalRoot = BuildTool.RootDirectory.FullName;
				string Search = CleanTargetFileName;
				if (Search.StartsWith(LocalRoot, StringComparison.InvariantCultureIgnoreCase))
				{
					if (LocalRoot.EndsWith("\\") || LocalRoot.EndsWith("/"))
					{
						Search = Search.Substring(LocalRoot.Length - 1);
					}
					else
					{
						Search = Search.Substring(LocalRoot.Length);
					}
				}

				if (OnlyGameProject != null)
				{
					string ProjectRoot = OnlyGameProject.Directory.FullName;
					if (Search.StartsWith(ProjectRoot, StringComparison.InvariantCultureIgnoreCase))
					{
						if (ProjectRoot.EndsWith("\\") || ProjectRoot.EndsWith("/"))
						{
							Search = Search.Substring(ProjectRoot.Length - 1);
						}
						else
						{
							Search = Search.Substring(ProjectRoot.Length);
						}
					}
				}

				// Skip targets in unsupported platform directories
				bool IncludeThisTarget = true;
				foreach (string CurPlatformName in UnsupportedPlatformNameStrings)
				{
					if (Search.IndexOf(Path.DirectorySeparatorChar + CurPlatformName + Path.DirectorySeparatorChar, StringComparison.InvariantCultureIgnoreCase) != -1)
					{
						IncludeThisTarget = false;
						break;
					}
				}

				if (IncludeThisTarget)
				{
					AllTargetFiles.Add(CurTargetFile);
				}
			}

			return AllTargetFiles;
		}

		// Recursively collapses all sub-folders that are redundant.
		// Should only be called after we're done adding files and projects to the master project.
		// <param name="Folder">The folder whose sub-folders we should potentially collapse into</param>
		// <param name="ParentMasterProjectFolderPath"></param>
		void RecursivelyEliminateRedundantMasterProjectSubFolders(MasterProjectFolder Folder, string ParentMasterProjectFolderPath)
		{
			// NOTE: This is for diagnostics output only
			string MasterProjectFolderPath = String.IsNullOrEmpty(ParentMasterProjectFolderPath) ? Folder.FolderName : (ParentMasterProjectFolderPath + "/" + Folder.FolderName);

			// We can eliminate folders that meet all of these requirements:
			//		1) Have only a single project file in them
			//		2) Have no files in the folder except project files, and no sub-folders
			//		3) The project file matches the folder name
			//
			// Additionally, if KeepSourceSubDirectories==false, we can eliminate directories called "Source".
			//
			// Also, we can kill folders that are completely empty.

			foreach (MasterProjectFolder SubFolder in Folder.SubFolders)
			{
				RecursivelyEliminateRedundantMasterProjectSubFolders(SubFolder, MasterProjectFolderPath);
			}

			List<MasterProjectFolder> SubFoldersToAdd = new List<MasterProjectFolder>();
			List<MasterProjectFolder> SubFoldersToRemove = new List<MasterProjectFolder>();
			foreach (MasterProjectFolder SubFolder in Folder.SubFolders)
			{
				bool CanCollapseFolder = false;

				// 1)
				if (SubFolder.ChildProjects.Count == 1)
				{
					// 2)
					if (SubFolder.Files.Count == 0 &&
						SubFolder.SubFolders.Count == 0)
					{
						// 3)
						if (SubFolder.FolderName.Equals(SubFolder.ChildProjects[0].ProjectFilePath.GetFileNameWithoutAnyExtensions(), StringComparison.InvariantCultureIgnoreCase))
						{
							CanCollapseFolder = true;
						}
					}
				}

				if (!bKeepSourceSubDirectories)
				{
					if (SubFolder.FolderName.Equals("Source", StringComparison.InvariantCultureIgnoreCase))
					{
						// Avoid collapsing the Engine's Source directory, since there are so many other solution folders in
						// the parent directory.
						if (!Folder.FolderName.Equals("Engine", StringComparison.InvariantCultureIgnoreCase))
						{
							CanCollapseFolder = true;
						}
					}
				}

				if (SubFolder.ChildProjects.Count == 0 && SubFolder.Files.Count == 0 && SubFolder.SubFolders.Count == 0)
				{
					// Folder is totally empty
					CanCollapseFolder = true;
				}

				if (CanCollapseFolder)
				{
					// OK, this folder is redundant and can be collapsed away.

					SubFoldersToAdd.AddRange(SubFolder.SubFolders);
					SubFolder.SubFolders.Clear();

					Folder.ChildProjects.AddRange(SubFolder.ChildProjects);
					SubFolder.ChildProjects.Clear();

					Folder.Files.AddRange(SubFolder.Files);
					SubFolder.Files.Clear();

					SubFoldersToRemove.Add(SubFolder);
				}
			}

			foreach (MasterProjectFolder SubFolderToRemove in SubFoldersToRemove)
			{
				Folder.SubFolders.Remove(SubFolderToRemove);
			}

			Folder.SubFolders.AddRange(SubFoldersToAdd);

			// After everything has been collapsed, do a bit of data validation
			Validate(Folder, ParentMasterProjectFolderPath);
		}

		// Validate the specified Folder.
		// Default implementation requires for project file names to be unique!
		// <param name="Folder">Folder.</param>
		// <param name="MasterProjectFolderPath">Parent master project folder path.</param>
		protected virtual void Validate(MasterProjectFolder Folder, string MasterProjectFolderPath)
		{
			foreach (ProjectFile CurChildProject in Folder.ChildProjects)
			{
				foreach (ProjectFile OtherChildProject in Folder.ChildProjects)
				{
					if (CurChildProject != OtherChildProject)
					{
						if (CurChildProject.ProjectFilePath.GetFileNameWithoutAnyExtensions().Equals(OtherChildProject.ProjectFilePath.GetFileNameWithoutAnyExtensions(), StringComparison.InvariantCultureIgnoreCase))
						{
							throw new BuildException("Detected collision between two project files with the same path in the same master project folder, " + OtherChildProject.ProjectFilePath.FullName + " and " + CurChildProject.ProjectFilePath.FullName + " (master project folder: " + MasterProjectFolderPath + ")");
						}
					}
				}
			}

			foreach (MasterProjectFolder SubFolder in Folder.SubFolders)
			{
				// If the parent folder already has a child project or file item with the same name as this sub-folder, then
				// that's considered an error (it should never have been allowed to have a folder name that collided
				// with project file names or file items, as that's not supported in Visual Studio.)
				foreach (ProjectFile CurChildProject in Folder.ChildProjects)
				{
					if (CurChildProject.ProjectFilePath.GetFileNameWithoutAnyExtensions().Equals(SubFolder.FolderName, StringComparison.InvariantCultureIgnoreCase))
					{
						throw new BuildException("Detected collision between a master project sub-folder " + SubFolder.FolderName + " and a project within the outer folder " + CurChildProject.ProjectFilePath + " (master project folder: " + MasterProjectFolderPath + ")");
					}
				}
				foreach (string CurFile in Folder.Files)
				{
					if (Path.GetFileName(CurFile).Equals(SubFolder.FolderName, StringComparison.InvariantCultureIgnoreCase))
					{
						throw new BuildException("Detected collision between a master project sub-folder " + SubFolder.FolderName + " and a file within the outer folder " + CurFile + " (master project folder: " + MasterProjectFolderPath + ")");
					}
				}
				foreach (MasterProjectFolder CurFolder in Folder.SubFolders)
				{
					if (CurFolder != SubFolder)
					{
						if (CurFolder.FolderName.Equals(SubFolder.FolderName, StringComparison.InvariantCultureIgnoreCase))
						{
							throw new BuildException("Detected collision between a master project sub-folder " + SubFolder.FolderName + " and a sibling folder " + CurFolder.FolderName + " (master project folder: " + MasterProjectFolderPath + ")");
						}
					}
				}
			}
		}

		// Adds BuildTool to the master project
		private void AddBuildToolProject(MasterProjectFolder ProgramsFolder)
		{
			List<string> ProjectDirectoryNames = new List<string> { "BuildTool" };

			if (AllowDotNetCoreProjects)
			{
				ProjectDirectoryNames.Add("BuildTool_NETCore");
			}

			// ProjectDirectoryName = "BuildTool"
			foreach (string ProjectDirectoryName in ProjectDirectoryNames)
			{
				DirectoryReference BuildToolProjectDirectory = DirectoryReference.Combine(BuildTool.EngineSourceDirectory, "Programs", ProjectDirectoryName);
				// ProjectDirectory = { D:\UERelease\Engine\Source\Programs\BuildTool}
				if (DirectoryReference.Exists(BuildToolProjectDirectory))
				{
					// ProjectFileName = {D:\UERelease\Engine\Source\Programs\BuildTool\BuildTool.csproj}
					FileReference ProjectFileName = FileReference.Combine(BuildToolProjectDirectory, "BuildTool.csproj");

					if (FileReference.Exists(ProjectFileName))
					{
						// BuildToolProject = {D:\UERelease\Engine\Source\Programs\BuildTool\BuildTool.csproj}
						VCSharpProjectFile BuildToolProject = new VCSharpProjectFile(ProjectFileName) { ShouldBuildForAllSolutionTargets = true };

						bool bBuildToolProjectDotNETCoreProject = BuildToolProject.IsDotNETCoreProject();

						if (bIncludeDotNETCoreProjects || !bBuildToolProjectDotNETCoreProject)
						{
							if (!bBuildToolProjectDotNETCoreProject)
							{
								// Store it off as we need it when generating target projects.
								// UBTProject = {D:\UERelease\Engine\Source\Programs\BuildTool\BuildTool.csproj}
								BuildToolProject = BuildToolProject;
							}

							// Add the project
							AddExistingProjectFile(BuildToolProject, bNeedsAllPlatformAndConfigurations: true, bForceDevelopmentConfiguration: true);

							// Put this in a solution folder
							// ProgramsFolder.Child Project = 26(.vcxproj, csproj)
							ProgramsFolder.ChildProjects.Add(BuildToolProject);
						}
					}
				}
			}
		}

		// Adds a C# project to the master project
		// <param name="ProjectName">Name of project file to add</param>
		// <param name="bShouldBuildForAllSolutionTargets"></param>
		// <param name="bForceDevelopmentConfiguration"></param>
		// <param name="bShouldBuildByDefaultForSolutionTargets"></param>
		// <returns>ProjectFile if the operation was successful, otherwise null.</returns>
		private VCSharpProjectFile AddSimpleCSharpProject
		(
            string ProjectName,
            bool bShouldBuildForAllSolutionTargets = false,
            bool bForceDevelopmentConfiguration = false,
            bool bShouldBuildByDefaultForSolutionTargets = true
		)
		{
			// Usually AutomationTool
			FileReference CSharpProjectFileName = FileReference.Combine(BuildTool.EngineSourceDirectory, "Programs", ProjectName, Path.GetFileName(ProjectName) + ".csproj");
			FileInfo Info = new FileInfo(CSharpProjectFileName.FullName);
			VCSharpProjectFile Project;
			if (Info.Exists)
			{
				Project = new VCSharpProjectFile(CSharpProjectFileName)
				{
					ShouldBuildForAllSolutionTargets       = bShouldBuildForAllSolutionTargets,
					ShouldBuildByDefaultForSolutionTargets = bShouldBuildByDefaultForSolutionTargets
				};
				AddExistingProjectFile(Project, bForceDevelopmentConfiguration: bForceDevelopmentConfiguration);
			}
			else
			{
				throw new BuildException(CSharpProjectFileName.FullName + " doesn't exist!");
			}

			return Project;
		}

#pragma warning disable IDE0051 // Remove unused private members
        // Check the registry for MVC3 project support
        private bool CheckRegistryKey(RegistryKey RootKey, string VisualStudioVersion)
        {
			bool bInstalled = false;
			RegistryKey VSSubKey = RootKey.OpenSubKey("SOFTWARE\\Microsoft\\VisualStudio\\" + VisualStudioVersion + "\\Projects\\{E53F8FEA-EAE0-44A6-8774-FFD645390401}");
			if (VSSubKey != null)
			{
				bInstalled = true;
				VSSubKey.Close();
			}

			return bInstalled;
		}

		// Check to see if a Visual Studio Extension is installed
		private bool CheckVisualStudioExtensionPackage(string VisualStudioFolder, string VisualStudioVersion, string Extension)
		{
			DirectoryInfo DirInfo = new DirectoryInfo(Path.Combine(VisualStudioFolder, VisualStudioVersion, "Extensions"));
			if (DirInfo.Exists)
			{
				List<FileInfo> PackageDefs = DirInfo.GetFiles("*.pkgdef", SearchOption.AllDirectories).ToList();
				List<string> PackageDefNames = PackageDefs.Select(x => x.Name).ToList();
				if (PackageDefNames.Contains(Extension))
				{
					return true;
				}
			}

			return false;
		}
#pragma warning restore IDE0051 // Remove unused private members

		// Adds all of the config files for program targets to their project files
		private void AddEngineExternalToolConfigFiles(Dictionary<FileReference, ProjectFile> ProgramProjects)
		{
			if (bIncludeConfigFiles)
			{
				foreach (KeyValuePair<FileReference, ProjectFile> FileAndProject in ProgramProjects)
				{
					string ProgramName = FileAndProject.Key.GetFileNameWithoutAnyExtensions();
					ProjectFile ProgramProjectFile = FileAndProject.Value;

					// @todo projectfiles: The config folder for programs is kind of weird -- you end up going UP a few directories to get to it.  This stuff is not great.
					// @todo projectfiles: Fragile assumption here about Programs always being under /Engine/Programs

					DirectoryReference ExternalToolDirectory;

					if (FileAndProject.Key.IsUnderDirectory(BuildTool.EnterpriseDirectory))
					{
						ExternalToolDirectory = DirectoryReference.Combine(BuildTool.EnterpriseDirectory, "Programs", ProgramName);
					}
					else
					{
						ExternalToolDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Programs", ProgramName);
					}

					DirectoryReference ProgramConfigDirectory = DirectoryReference.Combine(ExternalToolDirectory, "Config");
					if (DirectoryReference.Exists(ProgramConfigDirectory))
					{
						ProgramProjectFile.AddFilesToProject(SourceFileSearch.FindFiles(ProgramConfigDirectory), ExternalToolDirectory);
					}
				}
			}
		}

		// Generates data for IntelliSense (compile definitions, include paths)
		// <param name="Arguments">Incoming command-line arguments to UBT</param>
		// <param name="Targets">Targets to build for intellisense</param>
		private void GenerateIntelliSenseData(string[] Arguments, List<Tuple<ProjectFile, ProjectTarget>> Targets)
		{
			if (bGenerateIntelliSenseData && 0 < Targets.Count)
			{
				string ProgressInfoText = Utils.IsRunningOnMono ? "Generating data for project indexing..." : "Binding IntelliSense data...";

				// Debugger.Break();
				// Process.GetCurrentProcess().Kill();
				// return;

				using (ProgressWriter Progress = new ProgressWriter(ProgressInfoText, true))
				{
					//                                      Targets.Count => 43 {*.vcxproj, *.Target}
					for (int TargetIndex = 0; TargetIndex < Targets.Count; ++TargetIndex)
					{
						//            Target_PF = {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
						//            Target_PT = {Editor.Target}
						ProjectFile   Target_vcxprojFile   = Targets[TargetIndex].Item1;
						ProjectTarget Target_ProjectTarget = Targets[TargetIndex].Item2;
						// Target_ProjectTarget.ProjectFilePath = {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
						// Target_ProjectTarget.TargetFilePath  = {D:\UERelease\Engine\Source\Editor.Target.cs}

						// Ignore projects for platforms we can't build on this host
						//                   AvailableIntellisensePlatform = Win64
						BuildTargetPlatform AvailableIntellisensePlatform = BuildHostPlatform.Current.Platform;

						if (Target_ProjectTarget.SupportedPlatforms.All(x => x != AvailableIntellisensePlatform))
						{
							continue;
						}

						Log.TraceVerbose("Found target: " + Target_ProjectTarget.Name);

						List<string> NewArguments = new List<string>(Arguments.Length + 4);

						if (Target_ProjectTarget.TargetRules.Type != TargetType.Program)
						{
							NewArguments.Add("-precompile");
						}

						// NewRguments => -precompile, -ProjectFiles
						NewArguments.AddRange(Arguments);

						// VERY IMPORTANT PART  VERY IMPORTANT PART VERY IMPORTANT PART VERY IMPORTANT PART VERY IMPORTANT PART VERY IMPORTANT PART
						try
						{
							// Get the architecture from the target platform
							// DefaultArchitecture = ""
							string DefaultArchitecture = BuildPlatform.GetBuildPlatform(AvailableIntellisensePlatform).GetDefaultArchitecture(Target_ProjectTarget.ProjectFilePath);

							// Create the target descriptor
							// CurTarget.BuildProjectFilePath        = null
							// CurTarget.Name                        = "Editor"
							// IntellisensePlatform                  = {Win64}
							// TargetConfiguration.Development       = Development
							// DefaultArchitecture                   = ""
							// Arguments                             = -precompile, -ProjectFiles
							BuildTargetDescriptor TargetDesc = new BuildTargetDescriptor
							(
								Target_ProjectTarget.ProjectFilePath, // null
								Target_ProjectTarget.Name,                  // Editor
								AvailableIntellisensePlatform,
								TargetConfiguration.Development,
								DefaultArchitecture,
								new CommandLineArguments(NewArguments.ToArray()) // -precompile, -ProjectFiles
							);

							// Create the target
							BuildTarget ProjectFileBuildTarget = BuildTarget.CreateNewBuildTarget(TargetDesc, false, bUsePrecompiled);

							CppCompileEnvironment GlobalCompileEnvironment = ProjectFileBuildTarget.CreateCppCompileEnvironment();

							foreach (BuildBinary IterBuildBinary in ProjectFileBuildTarget.AllApplicationBuildBinaries)
							{
								CppCompileEnvironment IterBinaryCompileEnvironment = IterBuildBinary.OnlyCopyBuildingType(GlobalCompileEnvironment);

								foreach (BuildModuleCPP IterLinkedBuildModuleCPP in IterBuildBinary.LinkTogetherModules.OfType<BuildModuleCPP>())
								{
									if (ModuleToProjectFileMap.TryGetValue(IterLinkedBuildModuleCPP.ModuleRuleFileName, out ProjectFile ProjectFileForIDE) 
										&& ProjectFileForIDE == Target_vcxprojFile)
									{
										CppCompileEnvironment IterLinkedCppModule_CompileEnvironment
											= IterLinkedBuildModuleCPP.CreateCompileEnvironmentWithPCHAndForceIncludes(ProjectFileBuildTarget.Rules, IterBinaryCompileEnvironment);

										ProjectFileForIDE.AddPreprocessorDefintionsAndIncludePaths(IterLinkedBuildModuleCPP, IterLinkedCppModule_CompileEnvironment);
										//Debugger.Break();
									}
								}
							}

							FileReference MakefileLocation = TargetMakefile.GetLocation(TargetDesc.ProjectFile, TargetDesc.Name, TargetDesc.Platform, TargetDesc.Configuration);
							
							if (FileReference.Exists(MakefileLocation))
							{
								/**/
								// FileReference.Delete(MakefileLocation);
							}
						}
						catch (Exception Ex)
						{
							Log.TraceWarning("Exception while generating include data for {0}: {1}", Target_ProjectTarget.Name, Ex.ToString());
						}

						// Display progress
						Progress.Write(TargetIndex + 1, Targets.Count);
					}
				}
			}
		}

		// Selects which platforms and build configurations we want in the project file
		// <param name="IncludeAllPlatforms">True if we should include ALL platforms that are supported on this machine.
		// Otherwise, only desktop platforms will be included. </param>
		// <param name="SupportedPlatformNames">Output string for supported platforms, returned as comma-separated values.</param>
		protected virtual void SetupSupportedPlatformsAndConfigurations(bool IncludeAllPlatforms, out string SupportedPlatformNames)
		{
			StringBuilder SupportedPlatformsString = new StringBuilder();

			foreach (BuildTargetPlatform Platform in BuildTargetPlatform.GetValidPlatforms())
			{
				bool bValidDesktopPlatform = IsValidDesktopPlatform(Platform);

				// project is in the explicit platform list or we include them all, we add the valid desktop platforms as they are required
				bool bInProjectPlatformsList =
					ProjectPlatforms.Count == 0 ||
					ProjectPlatforms.Contains(Platform) ||
					bValidDesktopPlatform;

				// project is a desktop platform or we have specified some platforms explicitly
				bool IsRequiredPlatform = bValidDesktopPlatform || 0 < ProjectPlatforms.Count;

				// Only include desktop platforms unless we were explicitly asked to include all platforms or restricted to a single platform.
				if (bInProjectPlatformsList && (IncludeAllPlatforms || IsRequiredPlatform))
				{
					// If there is a build platform present, add it to the SupportedPlatforms list
					BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(Platform, true);
					if (BuildPlatform != null)
					{
						if (InstalledPlatformInfo.IsValidPlatform(Platform, EProjectType.Code))
						{
							SupportedPlatforms.Add(Platform);

							if (0 < SupportedPlatformsString.Length)
							{
								SupportedPlatformsString.Append(", ");
							}
							SupportedPlatformsString.Append(Platform.ToString());
						}
					}
				}
			}

			List<TargetConfiguration> AllowedTargetConfigurations = new List<TargetConfiguration>();

			if (ConfigurationNames == null)
			{
				AllowedTargetConfigurations = Enum.GetValues(typeof(TargetConfiguration)).Cast<TargetConfiguration>().ToList();
				// Unknown, Debug, DebugGame, Development, Shipping, Test, 
			}
			else
			{
				foreach (string ConfigName in ConfigurationNames)
				{
					try
					{
						TargetConfiguration AllowedConfiguration = (TargetConfiguration)Enum.Parse(typeof(TargetConfiguration), ConfigName);
						AllowedTargetConfigurations.Add(AllowedConfiguration);
					}
					catch (Exception)
					{
						Log.TraceWarning("Invalid entry found in Configurations: {0}. Must be a member of TargetConfiguration", ConfigName);
						continue;
					}
				}
			}

			// Add all configurations
			foreach (TargetConfiguration CurConfiguration in AllowedTargetConfigurations)
			{
				if (CurConfiguration != TargetConfiguration.Unknown)
				{
					if (InstalledPlatformInfo.IsValidConfiguration(CurConfiguration, EProjectType.Code))
					{
						SupportedConfigurations.Add(CurConfiguration);
					}
				}
			}

			SupportedPlatformNames = SupportedPlatformsString.ToString();
		}

		// Is this a valid platform. Used primarily for Installed vs non-Installed builds.
		static public bool IsValidDesktopPlatform(BuildTargetPlatform InPlatform)
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
			{
				return InPlatform == BuildTargetPlatform.Linux;
			}
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				return InPlatform == BuildTargetPlatform.Mac;
			}
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				return InPlatform == BuildTargetPlatform.Win64;
			}

			throw new BuildException("Invalid RuntimePlatform:" + BuildHostPlatform.Current.Platform);
		}

		protected FileReference FindGameContainingFile(List<FileReference> AllGames, FileReference TargetFileDir)
		{
			foreach (FileReference Game in AllGames)
			{
				if (TargetFileDir.IsUnderDirectory(Game.Directory))
				{
					return Game;
				}
			}
			return null;
		}

		// Finds all modules and code files, given a list of games to process
		protected void AddProjectsForAllModules
		(
			List<FileReference>                    AllGames, 
			Dictionary<FileReference, ProjectFile> AllProgramProjects, 
			List<ProjectFile>                      AllModProjects, 
			List<FileReference>                    AllModuleFiles, 
			bool                                   bGatherThirdPartySource
		)
		{
			// *.vcxproj에 해당되는 파일들 모두 묶음
			HashSet<ProjectFile> TempProjectsWithPlugins = new HashSet<ProjectFile>();

			// .Target.cs을 제외한 .Build.cs(모듈)에 해당되는 헤더(*.h), 소스(*.cpp) 파일들 찾아서
			// ProjectFileGenerator.ProjectsWithPlugins에 저장
			foreach (FileReference CurModuleFile in AllModuleFiles)
			{
				Log.TraceVerbose("AddProjectsForAllModules " + CurModuleFile);

				// The module's "base directory" is simply the directory where its xxx.Build.cs file is stored.  We'll always
				// harvest source files for this module in this base directory directory and all of its sub-directories.
				string ModuleName = CurModuleFile.GetFileNameWithoutAnyExtensions(); // Remove both ".cs" and ".Build"

				bool WantProjectFileForModule = true;

				// check for engine, or platform extension engine folders
				if (!bIncludeEngineSourceInSolution)
				{
					foreach (DirectoryReference EngineDirectory in BuildTool.GetAllEngineDirectories())
					{
						if (CurModuleFile.IsUnderDirectory(EngineDirectory))
						{
							// We were asked to exclude engine modules from the generated projects
							WantProjectFileForModule = false;
							break;
						}
					}
				}

				if (CurModuleFile.IsUnderDirectory(BuildTool.EnterpriseDirectory) && !bIncludeEnterpriseSource)
				{
					// We were asked to exclude enterprise modules from the generated projects
					WantProjectFileForModule = false;
				}

				if (WantProjectFileForModule)
				{
					ProjectFile ProjectFile = FindProjectForModule(CurModuleFile, AllGames, AllProgramProjects, AllModProjects, out DirectoryReference BaseFolder);

					// Update our module map
					ModuleToProjectFileMap[ModuleName] = ProjectFile;
					ProjectFile.IsGeneratedProject = true;

					// Only search subdirectories for non-external modules.
					// We don't want to add all of the source and header files for every third-party module, unless we were configured to do so.
					bool SearchSubdirectories = !CurModuleFile.ContainsName("ThirdParty", BuildTool.RootDirectory) || bGatherThirdPartySource;

					if (bGatherThirdPartySource)
					{
						Log.TraceInformation("Searching for third-party source files...");
					}


					// Find all of the source files (and other files) and add them to the project
					List<FileReference> FoundFiles = SourceFileSearch.FindModuleSourceFiles(CurModuleFile, SearchSubdirectories: SearchSubdirectories);
					// remove any target files, they are technically not part of the module and are explicitly added when the project is created
					FoundFiles.RemoveAll(f => f.FullName.EndsWith(".Target.cs"));
					ProjectFile.AddFilesToProject(FoundFiles, BaseFolder);

					// Check if there's a plugin directory here
					if (!TempProjectsWithPlugins.Contains(ProjectFile))
					{
						DirectoryReference PluginFolder = DirectoryReference.Combine(BaseFolder, "Plugins");
						if (DirectoryReference.Exists(PluginFolder))
						{
							// Add all the plugin files for this project
							foreach (FileReference PluginFileName in Plugins.EnumeratePlugins(PluginFolder))
							{
								if (!AllModProjects.Any(x => x.BaseDir == PluginFileName.Directory))
								{
									// Insert *.uplugin, *.inl
									// to ProjectFileGenerator.SourceFileMap,
									//    ProjectFileGenerator.SourceFiles 
									AddPluginFilesToProject(PluginFileName, BaseFolder, ProjectFile);
								}
							}
						}
						TempProjectsWithPlugins.Add(ProjectFile);
					}
				}
			}
		}

		private void AddPluginFilesToProject(FileReference PluginFileName, DirectoryReference BaseFolder, ProjectFile ProjectFile)
		{
			// Add the .uplugin file
			// ProjectFile    = {D:\UERelease\Engine\Intermediate\ProjectFiles\EngineCode_UnitTest.vcxproj}
			// PluginFileName = {D:\UERelease\Engine\Plugins\2D\Paper2D\Paper2D.uplugin}
			// BaseFolder     = {D:\UERelease\Engine}
			ProjectFile.AddFileToProject(PluginFileName, BaseFolder);

			// Add plugin config files if we have any
			if (bIncludeConfigFiles)
			{
				DirectoryReference PluginConfigFolder = DirectoryReference.Combine(PluginFileName.Directory, "Config");
				if (DirectoryReference.Exists(PluginConfigFolder))
				{
					ProjectFile.AddFilesToProject(SourceFileSearch.FindFiles(PluginConfigFolder), BaseFolder);
				}
			}

			// Add plugin "resource" files if we have any
			DirectoryReference PluginResourcesFolder = DirectoryReference.Combine(PluginFileName.Directory, "Resources");
			if (DirectoryReference.Exists(PluginResourcesFolder))
			{
				ProjectFile.AddFilesToProject(SourceFileSearch.FindFiles(PluginResourcesFolder), BaseFolder);
			}

			// Add plugin shader files if we have any
			DirectoryReference PluginShadersFolder = DirectoryReference.Combine(PluginFileName.Directory, "Shaders");
			if (DirectoryReference.Exists(PluginShadersFolder))
			{
				ProjectFile.AddFilesToProject(SourceFileSearch.FindFiles(PluginShadersFolder), BaseFolder);
			}
		}

		private ProjectFile FindOrAddProjectHelper(string InProjectFileNameBase, DirectoryReference InBaseFolder)
		{
			// Setup a project file entry for this module's project.  Remember, some projects may host multiple modules!
			FileReference ProjectFileName = FileReference.Combine(IntermediateProjectFilesPath, InProjectFileNameBase + ProjectFileExtension);
#pragma warning disable IDE0059 // Unnecessary assignment of a value
			return FindOrAddProject(ProjectFileName, InBaseFolder, IncludeInGeneratedProjects: true, bAlreadyExisted: out bool bProjectAlreadyExisted);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
		}

		private ProjectFile FindProjectForModule(FileReference CurModuleFile, List<FileReference> AllGames, Dictionary<FileReference, ProjectFile> ProgramProjects, List<ProjectFile> ModProjects, out DirectoryReference BaseFolder)
		{
			// Starting at the base directory of the module find a project which has the same directory as base, walking up the directory hierarchy until a match is found
			BaseFolder = null;

			DirectoryReference Path = CurModuleFile.Directory;
			while (!Path.IsRootDirectory())
			{
				// Figure out which game project this target belongs to
				foreach (FileReference Game in AllGames)
				{
					// the source and the actual game directory are conceptually the same
					if (Path == Game.Directory || Path == DirectoryReference.Combine(Game.Directory, "Source"))
					{
						FileReference ProjectInfo = Game;
						BaseFolder = ProjectInfo.Directory;
						return FindOrAddProjectHelper(ProjectInfo.GetFileNameWithoutExtension(), BaseFolder);
					}
				}
				// Check if it's a mod
				foreach (ProjectFile ModProject in ModProjects)
				{
					if (Path == ModProject.BaseDir)
					{
						BaseFolder = ModProject.BaseDir;
						return ModProject;
					}
				}

				// Check if this module is under any program project base folder
				if (ProgramProjects != null)
				{
					foreach (ProjectFile ProgramProject in ProgramProjects.Values)
					{
						if (Path == ProgramProject.BaseDir)
						{
							BaseFolder = ProgramProject.BaseDir;
							return ProgramProject;
						}
					}
				}

				// check for engine, or platform extension engine folders
				if (Path == BuildTool.EngineDirectory)
				{
					BaseFolder = BuildTool.EngineDirectory;
					return FindOrAddProjectHelper(EngineProjectFileNameBase, BaseFolder);
				}
				if (Path == BuildTool.EnginePlatformExtensionsDirectory)
				{
					BaseFolder = BuildTool.EngineDirectory;
					return FindOrAddProjectHelper(EngineProjectFileNameBase, BaseFolder);
				}
				if (Path == BuildTool.EnterpriseDirectory)
				{
					BaseFolder = BuildTool.EnterpriseDirectory;
					return FindOrAddProjectHelper(EnterpriseProjectFileNameBase, BaseFolder);
				}

				// no match found, lets search the parent directory
				Path = Path.ParentDirectory;
			}

			throw new BuildException("Found a module file (" + CurModuleFile + ") that did not exist within any of the known game folders or other source locations");
		}

		// Creates project entries for all known targets (*.Target.cs files)
		private void AddProjectsForAllTargets
		(
			// PlatformProjectGeneratorCollection PlatformProjectGenerators,
			List<FileReference>                AllGames,
			List<FileReference>                AllTargetFiles,
			String[]                           Arguments,
			out ProjectFile                            OutAllEngineProject,
			out ProjectFile                            OutAllEnterpriseProject,
			out List<ProjectFile>                      OutAllGameProjects,
			out Dictionary<FileReference, ProjectFile> OutAllProgramProjects
		)
		{
			// As we're creating project files, we'll also keep track of whether we created an "engine" project and return that if we have one
			OutAllEngineProject     = null;
			OutAllEnterpriseProject = null;
			OutAllGameProjects      = new List<ProjectFile>();
			OutAllProgramProjects   = new Dictionary<FileReference, ProjectFile>();

			// Get some standard directories
			//                 EnterpriseSourceProgramsDirectory = { D:\UERelease\Enterprise\Source\Programs}
			DirectoryReference EnterpriseSourceProgramsDirectory = DirectoryReference.Combine(BuildTool.EnterpriseSourceDirectory, "Programs");

			foreach (FileReference IterTargetFilePath in AllTargetFiles)
			{
				// Remove both ".cs" and ".Target"
				string IterTargetName = IterTargetFilePath.GetFileNameWithoutAnyExtensions();

				// Client Editor, Game Server
				// Check to see if this is an Engine target.
				// That is, the target is located under the "Engine" folder
				bool IsEngineTarget           = false;
				bool IsEnterpriseTarget       = false;
				bool WantProjectFileForTarget = true;

				if (BuildTool.GetAllEngineDirectories().Any(x => IterTargetFilePath.IsUnderDirectory(x)))
				{
					// This is an engine target
					IsEngineTarget = true;

					if (BuildTool.GetAllEngineDirectories("Source/Programs").Any(x => IterTargetFilePath.IsUnderDirectory(x)))
					{
						WantProjectFileForTarget = IncludeEnginePrograms;
					}
					else if (BuildTool.GetAllEngineDirectories("Source").Any(x => IterTargetFilePath.IsUnderDirectory(x)))
					{
						WantProjectFileForTarget = bIncludeEngineSourceInSolution;
					}
				}
				else if (IterTargetFilePath.IsUnderDirectory(BuildTool.EnterpriseSourceDirectory))
				{
					// This is an enterprise target
					IsEnterpriseTarget = true;

					if (IterTargetFilePath.IsUnderDirectory(EnterpriseSourceProgramsDirectory))
					{
						WantProjectFileForTarget = bIncludeEnterpriseSource && IncludeEnginePrograms;
					}
					else
					{
						WantProjectFileForTarget = bIncludeEnterpriseSource;
					}
				}

				if (WantProjectFileForTarget)
				{
					RulesAssembly ProgramsDLL_RulesAssembly;

					FileReference CheckProjectFile = AllGames.FirstOrDefault(x => IterTargetFilePath.IsUnderDirectory(x.Directory));

					if (CheckProjectFile == null)
					{
						if (IterTargetFilePath.IsUnderDirectory(BuildTool.EnterpriseDirectory))
						{
							ProgramsDLL_RulesAssembly = RulesCompiler.CreateEnterpriseRulesAssembly(false, false);
						}
						else
						{   // ReadOnly, bSkipCompile
							ProgramsDLL_RulesAssembly = RulesCompiler.CreateEngineRulesAssembly(false, false);
						}
					}
					else
					{
						ProgramsDLL_RulesAssembly = RulesCompiler.CreateProjectRulesAssembly(CheckProjectFile, false, false);
					}

					// Create target rules for all of the platforms and configuration combinations that we want to enable support for.
					// Just use the current platform as we only need to recover the target type and both should be supported for all targets...
					TargetRules IterTargetFileRulesObject =
						ProgramsDLL_RulesAssembly.CreateTargetRules
					    (
						    IterTargetName,
						    BuildHostPlatform.Current.Platform,
						    TargetConfiguration.Development,
						    "",
						    CheckProjectFile,
						    new CommandLineArguments(GetTargetArguments(Arguments))
					    );

					bool IsProgramTarget          = false;
					string ProjectFileNameBase    = null;
					DirectoryReference GameFolder = null;

					if (IterTargetFileRulesObject.Type == TargetType.Program)
					{
						IsProgramTarget     = true;
						ProjectFileNameBase = IterTargetName;
					}
					else if (IsEngineTarget)
					{
						ProjectFileNameBase = EngineProjectFileNameBase;
					}
					else if (IsEnterpriseTarget)
					{
						ProjectFileNameBase = EnterpriseProjectFileNameBase;
					}
					else
					{
						// Figure out which game project this target belongs to
						FileReference ProjectInfo = FindGameContainingFile(AllGames, IterTargetFilePath);

						if (ProjectInfo == null)
						{
							throw new BuildException("Found a non-engine target file (" + IterTargetFilePath + ") that did not exist within any of the known game folders");
						}

						GameFolder          = ProjectInfo.Directory;
						ProjectFileNameBase = ProjectInfo.GetFileNameWithoutExtension();
					}

					// Get the suffix to use for this project file.
					// If we have multiple targets of the same type, we'll have to split them out into separate IDE project files.
					string GeneratedProjectName = IterTargetFileRulesObject.GeneratedProjectName;

					if (GeneratedProjectName == null)
					{
						if (ProjectFileMap.TryGetValue(GetProjectLocation(ProjectFileNameBase), out ProjectFile ExistingProjectFile) &&
							ExistingProjectFile.ProjectTargets.Any(x => x.TargetRules.Type == IterTargetFileRulesObject.Type))
						{
							GeneratedProjectName = IterTargetFileRulesObject.Name;
						}
						else
						{
							GeneratedProjectName = ProjectFileNameBase;
						}
					}

					// @todo projectfiles: We should move all of the Target.cs files out of sub-folders to clean up the project directories a bit (e.g. GameUncooked folder)
					// *.vcxproj
					FileReference ProjectFilePath = GetProjectLocation(GeneratedProjectName);
					/*
					if (IterTargetFileRulesObject.Type == TargetType.Game || IterTargetFileRulesObject.Type == TargetType.Client || IterTargetFileRulesObject.Type == TargetType.Server)
					{
						// Allow platforms to generate stub projects here...
						PlatformProjectGenerators.GenerateGameProjectStubs
						(
							InGenerator: this,
							InTargetName: IterTargetName,
							InTargetFilepath: IterTargetFilePath.FullName,
							InTargetRules: IterTargetFileRulesObject,
							InPlatforms: SupportedPlatforms,
							InConfigurations: SupportedConfigurations
						);
					}
					*/
					DirectoryReference BaseFolder;
					if (IsProgramTarget)
					{
						BaseFolder = IterTargetFilePath.Directory;
					}
					else if (IsEngineTarget)
					{
						BaseFolder = BuildTool.EngineDirectory;
					}
					else if (IsEnterpriseTarget)
					{
						BaseFolder = BuildTool.EnterpriseDirectory;
					}
					else
					{
						BaseFolder = GameFolder;
					}

					// ProjectFile -> *.vcxproj
					ProjectFile IterProjectFile = FindOrAddProject(ProjectFilePath, BaseFolder, IncludeInGeneratedProjects: true, bAlreadyExisted: out bool bProjectAlreadyExisted);
					IterProjectFile.IsForeignProject   = CheckProjectFile != null && !NativeProjects.IsNativeProject(CheckProjectFile);
					IterProjectFile.IsGeneratedProject = true;
					IterProjectFile.IsStubProject      = BuildTool.IsProjectInstalled();

					if (IterTargetFileRulesObject.bBuildInSolutionByDefault.HasValue)
					{
						IterProjectFile.ShouldBuildByDefaultForSolutionTargets = IterTargetFileRulesObject.bBuildInSolutionByDefault.Value;
					}

					// Add the project to the right output list
					if (IsProgramTarget)
					{
						OutAllProgramProjects[IterTargetFilePath] = IterProjectFile;
					}
					else if (IsEngineTarget)
					{
						OutAllEngineProject = IterProjectFile;
						if (BuildTool.IsEngineInstalled())
						{
							// Allow engine projects to be created but not built for Installed Engine builds
							OutAllEngineProject.IsForeignProject = false;
							OutAllEngineProject.IsGeneratedProject = true;
							OutAllEngineProject.IsStubProject = true;
						}
					}
					else if (IsEnterpriseTarget)
					{
						OutAllEnterpriseProject = IterProjectFile;
						if (BuildTool.IsEnterpriseInstalled())
						{
							// Allow enterprise projects to be created but not built for Installed Engine builds
							OutAllEnterpriseProject.IsForeignProject = false;
							OutAllEnterpriseProject.IsGeneratedProject = true;
							OutAllEnterpriseProject.IsStubProject = true;
						}
					}
					else
					{
						if (!bProjectAlreadyExisted)
						{
							OutAllGameProjects.Add(IterProjectFile);

							// Add the .uproject file for this game/template
							FileReference UProjectFilePath = FileReference.Combine(BaseFolder, ProjectFileNameBase + ".uproject");
							if (FileReference.Exists(UProjectFilePath))
							{
								IterProjectFile.AddFileToProject(UProjectFilePath, BaseFolder);
							}
							else
							{
								throw new BuildException("Not expecting to find a game with no .uproject file.  File '{0}' doesn't exist", UProjectFilePath);
							}
						}
					}

					foreach (ProjectTarget ExistingProjectTarget in IterProjectFile.ProjectTargets)
					{
						if (ExistingProjectTarget.TargetRules.Type == IterTargetFileRulesObject.Type)
						{
							throw new BuildException("Not expecting project {0} to already have a target rules of with configuration name {1} ({2}) while trying to add: {3}", ProjectFilePath, IterTargetFileRulesObject.Type.ToString(), ExistingProjectTarget.TargetRules.ToString(), IterTargetFileRulesObject.ToString());
						}

						// Not expecting to have both a game and a program in the same project.  These would alias because we share the project and solution configuration names (just because it makes sense to)
						if ((ExistingProjectTarget.TargetRules.Type == TargetType.Game && IterTargetFileRulesObject.Type == TargetType.Program) ||
							(ExistingProjectTarget.TargetRules.Type == TargetType.Program && IterTargetFileRulesObject.Type == TargetType.Game))
						{
							throw new BuildException("Not expecting project {0} to already have a Game/Program target ({1}) associated with it while trying to add: {2}", ProjectFilePath, ExistingProjectTarget.TargetRules.ToString(), IterTargetFileRulesObject.ToString());
						}
					}

					ProjectTarget ProjectTarget = new ProjectTarget()
					{
						TargetRules           = IterTargetFileRulesObject,
						TargetFilePath        = IterTargetFilePath,
						ProjectFilePath       = ProjectFilePath,
						ProjectFilePath = CheckProjectFile,
						SupportedPlatforms    = IterTargetFileRulesObject.GetSupportedPlatforms().Where(x => BuildPlatform.GetBuildPlatform(x, true) != null).ToArray(),
						CreateRulesDelegate   = (Platform, Configuration) => ProgramsDLL_RulesAssembly.CreateTargetRules(IterTargetName, Platform, Configuration, "", CheckProjectFile, new CommandLineArguments(GetTargetArguments(Arguments)))
					};

					IterProjectFile.ProjectTargets.Add(ProjectTarget);

					// Make sure the *.Target.cs file is in the project.
					IterProjectFile.AddFileToProject(IterTargetFilePath, BaseFolder);

					Log.TraceVerbose("Generating target {0} for {1}", IterTargetFileRulesObject.Type.ToString(), ProjectFilePath);
				}
			}
		}

		// Adds separate project files for all mods
		protected void AddProjectsForMods(List<ProjectFile> GameProjects, out List<ProjectFile> ModProjects)
		{
			// Find all the mods for game projects
			ModProjects = new List<ProjectFile>();
			if (GameProjects.Count == 1)
			{
				ProjectFile GameProject = GameProjects.First();
				foreach (PluginInfo PluginInfo in Plugins.ReadProjectPlugins(GameProject.BaseDir))
				{
					if (PluginInfo.Descriptor.Modules != null && 
						0 < PluginInfo.Descriptor.Modules.Count && 
						PluginInfo.Type == PluginType.Mod)
					{
						FileReference ModProjectFilePath = FileReference.Combine(PluginInfo.RootDirectory, "Mods", PluginInfo.Name + ProjectFileExtension);

#pragma warning disable IDE0059 // Unnecessary assignment of a value
						ProjectFile ModProjectFile = FindOrAddProject(ModProjectFilePath, PluginInfo.RootDirectory, IncludeInGeneratedProjects: true, bAlreadyExisted: out bool bProjectAlreadyExisted);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
						ModProjectFile.IsForeignProject   = GameProject.IsForeignProject;
						ModProjectFile.IsGeneratedProject = true;
						ModProjectFile.IsStubProject      = false;
						ModProjectFile.PluginFilePath     = PluginInfo.File;
						ModProjectFile.ProjectTargets.AddRange(GameProject.ProjectTargets);

						AddPluginFilesToProject(PluginInfo.File, PluginInfo.RootDirectory, ModProjectFile);

						ModProjects.Add(ModProjectFile);
					}
				}
			}
		}

		// Gets the location for a project file (*.vcxproj)
		protected FileReference GetProjectLocation(string BaseName)
		{
			// IntermediateProjectFilesPath = {D:\UERelease\Engine\Intermediate\ProjectFiles}
			// BaseName = "BlankProgram"
			// ProjectFileExtension = ".vcxproj"
			return FileReference.Combine(IntermediateProjectFilesPath, BaseName + ProjectFileExtension);
		}

		// Adds shader source code to the specified project
		protected void AddEngineShaderSource(ProjectFile EngineProject)
		{
			// Setup a project file entry for this module's project.  Remember, some projects may host multiple modules!
			DirectoryReference ShadersDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Shaders");
			if (DirectoryReference.Exists(ShadersDirectory))
			{
				List<string> SubdirectoryNamesToExclude = new List<string>();
				{
					// Don't include binary shaders in the project file.
					SubdirectoryNamesToExclude.Add("Binaries");

					// We never want shader intermediate files in our project file
					SubdirectoryNamesToExclude.Add("PDBDump");
					SubdirectoryNamesToExclude.Add("WorkingDirectory");
				}

				EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(ShadersDirectory, SubdirectoryNamesToExclude), BuildTool.EngineDirectory);
			}
		}

		// Adds engine build infrastructure files to the specified project
		protected void AddEngineBuildFiles(ProjectFile EngineProject)
		{
			DirectoryReference BuildDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Build");

			List<string> SubdirectoryNamesToExclude = new List<string> { "Receipts" };
			EngineProject.AddFilesToProject(SourceFileSearch.FindFiles(BuildDirectory, SubdirectoryNamesToExclude), BuildTool.EngineDirectory);
		}

		// Adds engine documentation to the specified project
		protected void AddEngineDocumentation(ProjectFile EngineProject)
		{
			// NOTE: The project folder added here will actually be collapsed away later if not needed
			DirectoryReference DocumentationProjectDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, "Documentation");
			DirectoryReference DocumentationSourceDirectory  = DirectoryReference.Combine(BuildTool.EngineDirectory, "Documentation", "Source");
			DirectoryInfo DirInfo = new DirectoryInfo(DocumentationProjectDirectory.FullName);
			if (DirInfo.Exists && DirectoryReference.Exists(DocumentationSourceDirectory))
			{
				Log.TraceVerbose("Adding documentation files...");

				List<string> SubdirectoryNamesToExclude = new List<string>();
				{
					// We never want any of the images or attachment files included in our generated project
					SubdirectoryNamesToExclude.Add("Images");
					SubdirectoryNamesToExclude.Add("Attachments");

					// The API directory is huge, so don't include any of it
					SubdirectoryNamesToExclude.Add("API");

					// Omit Javascript source because it just confuses the Visual Studio IDE
					SubdirectoryNamesToExclude.Add("Javascript");
				}

				List<FileReference> DocumentationFiles = SourceFileSearch.FindFiles(DocumentationSourceDirectory, SubdirectoryNamesToExclude);

				// Filter out non-English documentation files if we were configured to do so
				if (!bAllDocumentationLanguages)
				{
					List<FileReference> FilteredDocumentationFiles = new List<FileReference>();
					foreach (FileReference DocumentationFile in DocumentationFiles)
					{
						bool bPassesFilter = true;
						if (DocumentationFile.FullName.EndsWith(".udn", StringComparison.InvariantCultureIgnoreCase))
						{
							string LanguageSuffix = Path.GetExtension(Path.GetFileNameWithoutExtension(DocumentationFile.FullName));
							if (!String.IsNullOrEmpty(LanguageSuffix) &&
								!LanguageSuffix.Equals(".int", StringComparison.InvariantCultureIgnoreCase))
							{
								bPassesFilter = false;
							}
						}

						if (bPassesFilter)
						{
							FilteredDocumentationFiles.Add(DocumentationFile);
						}
					}
					DocumentationFiles = FilteredDocumentationFiles;
				}

				EngineProject.AddFilesToProject(DocumentationFiles, BuildTool.EngineDirectory);
			}
			else
			{
				Log.TraceVerbose("Skipping documentation project... directory not found");
			}
		}

		// Adds a new project file and returns an object that represents that project file (or if the project file is already known, returns that instead.)
		// <param name="FilePath">Full path to the project file</param>
		// <param name="BaseDir">Base directory for files in this project</param>
		// <param name="IncludeInGeneratedProjects">True if this project should be included in the set of generated projects.  Only matters when actually generating project files.</param>
		// <param name="bAlreadyExisted">True if we already had this project file</param>
		// <returns>Object that represents this project file in  Build Tool</returns>
		public ProjectFile FindOrAddProject(FileReference FilePath, DirectoryReference BaseDir, bool IncludeInGeneratedProjects, out bool bAlreadyExisted)
		{
			if (FilePath == null)
			{
				throw new BuildException("Not valid to call FindOrAddProject() with an empty file path!");
			}

			if (ProjectFileMap.TryGetValue(FilePath, out ProjectFile OutExistingProjectFile))
			{
				bAlreadyExisted = true;
				return OutExistingProjectFile;
			}

			// Add a new project file for the specified path
			ProjectFile NewProjectFile = AllocateProjectFile(FilePath);
			NewProjectFile.BaseDir = BaseDir;
			ProjectFileMap[FilePath] = NewProjectFile;

			if (IncludeInGeneratedProjects)
			{
				GeneratedProjectFiles.Add(NewProjectFile);
			}

			bAlreadyExisted = false;
			return NewProjectFile;
		}
		
		// Allocates a generator-specific project file object
		// <param name="InitFilePath">Path to the project file</param>
		// <returns>The newly allocated project file object</returns>
		protected abstract ProjectFile AllocateProjectFile(FileReference InitFilePath);

		// Allocates a generator-specific master project folder object
		// <param name="OwnerProjectFileGenerator">Project file generator that owns this object</param>
		// <param name="FolderName">Name for this folder</param>
		// <returns>The newly allocated project folder object</returns>
		public abstract MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator OwnerProjectFileGenerator, string FolderName);

		// Returns a list of all the known project files
		// <returns>Project file list</returns>
		public List<ProjectFile> AllProjectFiles
		{
			get
			{
				List<ProjectFile> CombinedList = new List<ProjectFile>();
				CombinedList.AddRange(GeneratedProjectFiles);
				CombinedList.AddRange(OtherProjectFiles);
				return CombinedList;
			}
		}

		// Writes the project files to disk
		protected virtual bool WriteProjectFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			using (ProgressWriter Progress = new ProgressWriter("Writing project files...", true))
			{
				int TotalProjectFileCount = GeneratedProjectFiles.Count + 1; // +1 for the master project file(UBT), which we'll save next

				for (int ProjectFileIndex = 0; ProjectFileIndex < GeneratedProjectFiles.Count; ++ProjectFileIndex)
				{
					// Debugger.Break();
					ProjectFile CurProject = GeneratedProjectFiles[ProjectFileIndex];
					if (!CurProject.WriteProjectFile(SupportedPlatforms, SupportedConfigurations, PlatformProjectGenerators))
					{
						return false;
					}

					Progress.Write(ProjectFileIndex + 1, TotalProjectFileCount);
				}

				Debugger.Break();
				WriteMasterProjectFile(BuildToolProject, PlatformProjectGenerators);
				Progress.Write(TotalProjectFileCount, TotalProjectFileCount);
			}
			return true;
		}

		// Writes the master project file (e.g. Visual Studio Solution file)
		protected abstract bool WriteMasterProjectFile(ProjectFile BuildToolProject, PlatformProjectGeneratorCollection PlatformProjectGenerators);

		// Writes any additional solution-wide debug files (e.g. VS hints)
		// goto : VCProjectFileGenerator.cs::WriteDebugSolutionFiles
		protected virtual void WriteDebugSolutionFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators, DirectoryReference IntermediateProjectFilesPath)
        {
        }

        // Writes the specified string content to a file.  
        // Before writing to the file, it loads the existing file (if present) to see if the contents have changed
        // returns True if the file was saved, or if it didn't need to be overwritten because the content was unchanged
#pragma warning disable IDE0060 // Remove unused parameter
        public static bool WriteFileIfChanged(string FileNameToWrite, string NewFileContents, Encoding InEncoding = null)
#pragma warning restore IDE0060 // Remove unused parameter
        {
			// Check to see if the file already exists, and if so, load it up
			string LoadedFileContent = null;
			bool FileAlreadyExists = File.Exists(FileNameToWrite);
			if (FileAlreadyExists)
			{
				try
				{
					LoadedFileContent = File.ReadAllText(FileNameToWrite);
				}
				catch (Exception)
				{
					Log.TraceInformation("Error while trying to load existing file {0}.  Ignored.", FileNameToWrite);
				}
			}

			// Don't bother saving anything out if the new file content is the same as the old file's content
			bool FileNeedsSave = true;
			if (LoadedFileContent != null)
			{
				bool bIgnoreProjectFileWhitespaces = true;
				if (ProjectFileComparer.CompareOrdinalIgnoreCase(LoadedFileContent, NewFileContents, bIgnoreProjectFileWhitespaces) == 0)
				{
					// Exact match!
					FileNeedsSave = false;
				}

				if (!FileNeedsSave)
				{
					Log.TraceVerbose("Skipped saving {0} because contents haven't changed.", Path.GetFileName(FileNameToWrite));
				}
			}

			if (FileNeedsSave)
			{
				// Save the file
				return true;
				Debugger.Break();
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(FileNameToWrite));
					// When WriteAllText is passed Encoding.UTF8 it likes to write a BOM marker
					// at the start of the file (adding two bytes to the file length).  For most
					// files this is only mildly annoying but for Makefiles it can actually make
					// them un-useable.
					// TODO(sbc): See if we can just drop the Encoding.UTF8 argument on all
					// platforms.  In this case UTF8 encoding will still be used but without the
					// BOM, which is, AFAICT, desirable in almost all cases.
					if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux 
						|| BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
					{
						File.WriteAllText(FileNameToWrite, NewFileContents, new UTF8Encoding());
					}
					else
					{
						File.WriteAllText(FileNameToWrite, NewFileContents, InEncoding ?? Encoding.UTF8);
					}

					Log.TraceVerbose("Saved {0}", Path.GetFileName(FileNameToWrite));
				}
				catch (Exception ex)
				{
					// Unable to write to the project file.
					string Message = string.Format("Error while trying to write file {0}.  The file is probably read-only.", FileNameToWrite);
					Log.TraceInformation("");
					Log.TraceError(Message);
					throw new BuildException(ex, Message);
				}
			}

			return true;
		}

		// Adds the given project to the OtherProjects list
		// <returns>True if successful</returns>
		public void AddExistingProjectFile(ProjectFile InProject, bool bNeedsAllPlatformAndConfigurations = false, bool bForceDevelopmentConfiguration = false, bool bProjectDeploys = false, List<BuildTargetPlatform> InSupportedPlatforms = null, List<TargetConfiguration> InSupportedConfigurations = null)
		{
			if (InProject.ProjectTargets.Count != 0)
			{
				throw new BuildException("Expecting existing project to not have any ProjectTargets defined yet.");
			}

			ProjectTarget ProjectTarget = new ProjectTarget { SupportedPlatforms = new BuildTargetPlatform[0], ProjectDeploys = bProjectDeploys };
			if (bForceDevelopmentConfiguration)
			{
				ProjectTarget.ForceDevelopmentConfiguration = true;
			}

			if (bNeedsAllPlatformAndConfigurations)
			{
				// Add all platforms
				ProjectTarget.ExtraSupportedPlatforms.AddRange(BuildTargetPlatform.GetValidPlatforms());

				// Add all configurations
				Array AllConfigurations = Enum.GetValues(typeof(TargetConfiguration));
				// AllConfigurations => Unknown, Debug, DebugGame, Development, Shipping, Test,
				foreach (TargetConfiguration CurConfiguration in AllConfigurations)
				{
					ProjectTarget.ExtraSupportedConfigurations.Add(CurConfiguration);
				}
			}
			else if (InSupportedPlatforms != null || InSupportedConfigurations != null)
			{
				if (InSupportedPlatforms != null)
				{
					// Add all explicitly specified platforms
					foreach (BuildTargetPlatform CurPlatfrom in InSupportedPlatforms)
					{
						ProjectTarget.ExtraSupportedPlatforms.Add(CurPlatfrom);
					}
				}
				else
				{
					// Otherwise, add all platforms
					ProjectTarget.ExtraSupportedPlatforms.AddRange(BuildTargetPlatform.GetValidPlatforms());
				}

				if (InSupportedConfigurations != null)
				{
					// Add all explicitly specified configurations
					foreach (TargetConfiguration CurConfiguration in InSupportedConfigurations)
					{
						ProjectTarget.ExtraSupportedConfigurations.Add(CurConfiguration);
					}
				}
				else
				{
					// Otherwise, add all configurations
					Array AllConfigurations = Enum.GetValues(typeof(TargetConfiguration));
					foreach (TargetConfiguration CurConfiguration in AllConfigurations)
					{
						ProjectTarget.ExtraSupportedConfigurations.Add(CurConfiguration);
					}
				}
			}
			else
			{
				bool bFoundDevelopmentConfig = false;
				bool bFoundDebugConfig = false;

				try
				{
					foreach (string Config in XElement.Load(InProject.ProjectFilePath.FullName).Elements
						("{http://schemas.microsoft.com/developer/msbuild/2003}PropertyGroup")
										   .Where(node => node.Attribute("Condition") != null)
										   .Select(node => node.Attribute("Condition").ToString())
										   .ToList())
					{
						if (Config.Contains("Development|"))
						{
							bFoundDevelopmentConfig = true;
						}
						else if (Config.Contains("Debug|"))
						{
							bFoundDebugConfig = true;
						}
					}
				}
				catch
				{
					Trace.TraceError("Unable to parse existing project file {0}", InProject.ProjectFilePath.FullName);
				}

				if (!bFoundDebugConfig || !bFoundDevelopmentConfig)
				{
					throw new BuildException("Existing C# project {0} must contain a {1} configuration", InProject.ProjectFilePath.FullName, bFoundDebugConfig ? "Development" : "Debug");
				}

				// For existing project files, just support the default desktop platforms and configurations
				ProjectTarget.ExtraSupportedPlatforms.AddRange(Utils.GetPlatformsInClass(BuildPlatformClass.Desktop));
				// Debug and Development only
				ProjectTarget.ExtraSupportedConfigurations.Add(TargetConfiguration.Debug);
				ProjectTarget.ExtraSupportedConfigurations.Add(TargetConfiguration.Development);
			}

			InProject.ProjectTargets.Add(ProjectTarget);

			// Existing projects must always have a GUID.  This will throw an exception if one isn't found.
			InProject.LoadGUIDFromExistingProject();

			OtherProjectFiles.Add(InProject);
		}

		public virtual string[] GetTargetArguments(string[] Arguments)
		{
			return new string[0]; // by default we do not forward any arguments to the targets
		}
	}

	// Helper class used for comparing the existing and generated project files.
	class ProjectFileComparer
	{
		//static readonly string GUIDRegexPattern = "(\\{){0,1}[0-9a-fA-F]{8}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{4}\\-[0-9a-fA-F]{12}(\\}){0,1}";
		//static readonly string GUIDReplaceString = "GUID";

		// Used by CompareOrdinalIgnoreWhitespaceAndCase to determine if a whitespace can be ignored.
		
		// <param name="Whitespace">Whitespace character.</param>
		// <returns>true if the character can be ignored, false otherwise.</returns>
		static bool CanIgnoreWhitespace(char Whitespace)
		{
			// Only ignore spaces and tabs.
			return Whitespace == ' ' || Whitespace == '\t';
		}

		/*
		// Replaces all GUIDs in the project file with "GUID" text.
		
		// <param name="ProjectFileContents">Contents of the project file to remove GUIDs from.</param>
		// <returns>String with all GUIDs replaced with "GUID" text.</returns>
		static string StripGUIDs(string ProjectFileContents)
		{
			// Replace all GUIDs with "GUID" text.
			return System.Text.RegularExpressions.Regex.Replace(ProjectFileContents, GUIDRegexPattern, GUIDReplaceString);
		}
		*/

		// Compares two project files ignoring whitespaces, case and GUIDs.
		// <remarks>
		// Compares two specified String objects by evaluating the numeric values of the corresponding Char objects in each string.
		// Only space and tabulation characters are ignored. Ignores leading whitespaces at the beginning of each line and 
		// differences in whitespace sequences between matching non-whitespace sub-strings.
		// </remarks>
		// <param name="StrA">The first string to compare.</param>
		// <param name="StrB">The second string to compare. </param>
		// <returns>An integer that indicates the lexical relationship between the two comparands.</returns>
		public static int CompareOrdinalIgnoreWhitespaceAndCase(string StrA, string StrB)
		{
			// Remove GUIDs before processing the strings.
			// StrA = StripGUIDs(StrA);
			// StrB = StripGUIDs(StrB);

			int IndexA = 0;
			int IndexB = 0;
			while (IndexA < StrA.Length && IndexB < StrB.Length)
			{
				char A = Char.ToLowerInvariant(StrA[IndexA]);
				char B = Char.ToLowerInvariant(StrB[IndexB]);
				if (Char.IsWhiteSpace(A) && Char.IsWhiteSpace(B) && CanIgnoreWhitespace(A) && CanIgnoreWhitespace(B))
				{
					// Skip whitespaces in both strings
					for (IndexA++; IndexA < StrA.Length && Char.IsWhiteSpace(StrA[IndexA]) == true; IndexA++) ;
					for (IndexB++; IndexB < StrB.Length && Char.IsWhiteSpace(StrB[IndexB]) == true; IndexB++) ;
				}
				else if (Char.IsWhiteSpace(A) && IndexA > 0 && StrA[IndexA - 1] == '\n')
				{
					// Skip whitespaces in StrA at the beginning of each line
					for (IndexA++; IndexA < StrA.Length && Char.IsWhiteSpace(StrA[IndexA]) == true; IndexA++) ;
				}
				else if (Char.IsWhiteSpace(B) && IndexB > 0 && StrB[IndexB - 1] == '\n')
				{
					// Skip whitespaces in StrA at the beginning of each line
					for (IndexB++; IndexB < StrB.Length && Char.IsWhiteSpace(StrB[IndexB]) == true; IndexB++) ;
				}
				else if (A != B)
				{
					return A - B;
				}
				else
				{
					IndexA++;
					IndexB++;
				}
			}
			// Check if we reached the end in both strings
			return (StrA.Length - IndexA) - (StrB.Length - IndexB);
		}

		// Compares two project files ignoring case and GUIDs.
		// <param name="StrA">The first string to compare.</param>
		// <param name="StrB">The second string to compare. </param>
		// <returns>An integer that indicates the lexical relationship between the two comparands.</returns>
		public static int CompareOrdinalIgnoreCase(string StrA, string StrB)
		{
			// Remove GUIDs before processing the strings.
			//StrA = StripGUIDs(StrA);
			//StrB = StripGUIDs(StrB);

			// Use simple ordinal comparison.
			return String.Compare(StrA, StrB, StringComparison.InvariantCultureIgnoreCase);
		}

		// Compares two project files ignoring case and GUIDs.
		// <see cref="CompareOrdinalIgnoreWhitespaceAndCase"/>
		// <param name="StrA">The first string to compare.</param>
		// <param name="StrB">The second string to compare. </param>
		// <param name="bIgnoreWhitespace">True if whitsapces should be ignored.</param>
		// <returns>An integer that indicates the lexical relationship between the two comparands.</returns>
		public static int CompareOrdinalIgnoreCase(string StrA, string StrB, bool bIgnoreWhitespace)
		{
			if (bIgnoreWhitespace)
			{
				return CompareOrdinalIgnoreWhitespaceAndCase(StrA, StrB);
			}
			else
			{
				return CompareOrdinalIgnoreCase(StrA, StrB);
			}
		}
	}
#pragma warning restore IDE0079 // Remove unnecessary suppression
}
