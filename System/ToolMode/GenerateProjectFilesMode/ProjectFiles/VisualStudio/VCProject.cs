using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security;
using BuildToolUtilities;

namespace BuildTool
{
#pragma warning disable IDE0079 // Remove unnecessary suppression
	// Specifies the context for building an MSBuild project
	internal sealed class MSBuildProjectContext
	{
		public string ConfigurationName;
		public string PlatformName;
		public bool   bBuildByDefault;
		public bool   bDeployByDefault;

		public MSBuildProjectContext(string ConfigurationName, string PlatformName)
		{
			this.ConfigurationName = ConfigurationName;
			this.PlatformName      = PlatformName;
		}

        public string Name => String.Format("{0}|{1}", ConfigurationName, PlatformName);

        public override string ToString() => Name;
    }

	// Represents an arbitrary MSBuild project
	abstract class MSBuildProjectFile : ProjectFile
	{
        public virtual string ProjectTypeGUID => throw new BuildException("Unrecognized type of project file for Visual Studio solution");

        // Constructs a new project file object
        public MSBuildProjectFile(FileReference InitFilePath)
			: base(InitFilePath)
		{
			// Each project gets its own GUID.  This is stored in the project file and referenced in the solution file.

			// First, check to see if we have an existing file on disk.  If we do, then we'll try to preserve the
			// GUID by loading it from the existing file.
			if (FileReference.Exists(ProjectFilePath))
			{
				try
				{
					LoadGUIDFromExistingProject();
				}
				catch (Exception)
				{
					// Failed to find GUID, so just create a new one
					ProjectGUID = Guid.NewGuid();
				}
			}

			if (ProjectGUID == Guid.Empty)
			{
				// Generate a brand new GUID
				ProjectGUID = Guid.NewGuid();
			}
		}

		// Attempts to load the project's GUID from an existing project file on disk
		public override void LoadGUIDFromExistingProject()
		{
			// Only load GUIDs if we're in project generation mode.  Regular builds don't need GUIDs for anything.
			if (ProjectFileGenerator.bGenerateProjectFiles)
			{
				XmlDocument Doc = new XmlDocument();
				Doc.Load(ProjectFilePath.FullName);

				// @todo projectfiles: Ideally we could do a better job about preserving GUIDs when only minor changes are made
				// to the project (such as adding a single new file.) It would make diffing changes much easier!

				// @todo projectfiles: Can we "seed" a GUID based off the project path and generate consistent GUIDs each time?

				XmlNodeList Elements = Doc.GetElementsByTagName(Tag.XML.Element.ProjectGuid);
				foreach (XmlElement Element in Elements)
				{
					ProjectGUID = Guid.ParseExact(Element.InnerText.Trim("{}".ToCharArray()), "D");
				}
			}
		}
		
		// Get the project context for the given solution context
		public abstract MSBuildProjectContext GetMatchingProjectContext
		(
            TargetType SolutionTargetType,
            TargetConfiguration SolutionConfiguration,
            BuildTargetPlatform SolutionPlatform,
            PlatformProjectGeneratorCollection PlatformProjectGenerators
		);

		// Checks to see if the specified solution platform and configuration is able to map to this project
		public static bool IsValidProjectPlatformAndConfiguration
        (
			ProjectTarget ProjectTargetToCheck,
            BuildTargetPlatform Platform,
            TargetConfiguration Configuration
            //, PlatformProjectGeneratorCollection PlatformProjectGenerators
        )
		{
			if (!ProjectFileGenerator.bIncludeTestAndShippingConfigs)
			{
				if(Configuration == TargetConfiguration.Test || 
				   Configuration == TargetConfiguration.Shipping)
				{
					return false;
				}
			}

			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(Platform, true);
			if (BuildPlatform == null)
			{
				return false;
			}

			if (BuildPlatform.HasRequiredSDKsInstalled() != SDKStatus.Valid)
			{
				return false;
			}

			List<TargetConfiguration> SupportedConfigurations = new List<TargetConfiguration>();
			List<BuildTargetPlatform>      SupportedPlatforms      = new List<BuildTargetPlatform>();

			if (ProjectTargetToCheck.TargetRules != null)
			{
				SupportedPlatforms.AddRange(ProjectTargetToCheck.SupportedPlatforms);
				SupportedConfigurations.AddRange(ProjectTargetToCheck.TargetRules.GetSupportedConfigurations());
			}

			// Add all of the extra platforms/configurations for this target
			{
				foreach (BuildTargetPlatform ExtraPlatform in ProjectTargetToCheck.ExtraSupportedPlatforms)
				{
					if (!SupportedPlatforms.Contains(ExtraPlatform))
					{
						SupportedPlatforms.Add(ExtraPlatform);
					}
				}
				foreach (TargetConfiguration ExtraConfig in ProjectTargetToCheck.ExtraSupportedConfigurations)
				{
					if (!SupportedConfigurations.Contains(ExtraConfig))
					{
						SupportedConfigurations.Add(ExtraConfig);
					}
				}
			}

			// Only build for supported platforms
			if (SupportedPlatforms.Contains(Platform) == false)
			{
				return false;
			}

			// Only build for supported configurations
			if (SupportedConfigurations.Contains(Configuration) == false)
			{
				return false;
			}

			return true;
		}

		// GUID for this Visual C++ project file
		public Guid ProjectGUID { get; protected set; }
	}

    internal sealed class VCProjectFile : MSBuildProjectFile
	{
		//FileReference OnlyGameProject;
		private readonly Dictionary<DirectoryReference, string> ModuleDirToForceIncludePaths = new Dictionary<DirectoryReference, string>();
		private readonly VCProjectFileFormat ProjectFileFormat;
		private readonly string AdditionalArguments;
		private readonly string ExcludedIncludePaths;
		private readonly bool bUseFastPDB;
		private readonly bool bUsePerFileIntellisense;
		private readonly bool bUsePrecompiled;
		private readonly bool bEditorDependsOnShaderCompileWorker;
		private readonly bool bBuildLiveCodingConsole;

		// This is the platform name that Visual Studio is always guaranteed to support.
		// We'll use this as a platform for any project configurations where
		// our actual platform is not supported by the installed version of Visual Studio (e.g, "iOS")
		// public static string DefaultPlatformName => "Win32";

        // This is the GUID that Visual Studio uses to identify a C++ project file in the solution
        // public override string ProjectTypeGUID => "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";

        public VCProjectFile
		(
            FileReference InFilePath,
            // FileReference InOnlyGameProject,
            VCProjectFileFormat InProjectFileFormat,
            bool bUseFastPDB, // If true, adds the -FastPDB argument to build command lines
			bool bUsePerFileIntellisense, // If true, generates per-file intellisense data
			bool bUsePrecompiled, // Whether to add the -UsePrecompiled argumemnt when building targets
			bool bEditorDependsOnShaderCompileWorker,
            bool bBuildLiveCodingConsole,
            string AdditionalArguments,
            string ExcludedIncludePaths // Include paths to exclude in the interest of reducing Visual Studio memory use.
		)
			: base(InFilePath)
		{
			//OnlyGameProject = InOnlyGameProject;
			ProjectFileFormat                        = InProjectFileFormat;
			this.bUseFastPDB                         = bUseFastPDB;
			this.bUsePerFileIntellisense             = bUsePerFileIntellisense;
			this.bUsePrecompiled                     = bUsePrecompiled;
			this.bEditorDependsOnShaderCompileWorker = bEditorDependsOnShaderCompileWorker;
			this.bBuildLiveCodingConsole             = bBuildLiveCodingConsole;
			this.AdditionalArguments                 = AdditionalArguments;
			this.ExcludedIncludePaths                = ExcludedIncludePaths;
		}

		// Given a target platform and configuration, generates a platform and configuration name string to use in Visual Studio projects.
		// Unlike with solution configurations, Visual Studio project configurations only support certain types of platforms, so we'll
		// generate a configuration name that has the platform "built in", and use a default platform type
		private void MakeProjectPlatformAndConfigurationNames
		(
            BuildTargetPlatform               Platform,
            TargetConfiguration          Configuration,
            TargetType                         TargetConfigurationName,
            PlatformProjectGeneratorCollection PlatformProjectGenerators,
            out string                         PlatformNameToUseVS,
            out string                         ConfigurationNameToUseVS
		)
		{
			PlatformProjectGenerator PlatformProjectGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, bInAllowFailure: true);

			// Check to see if this platform is supported directly by Visual Studio projects.
			bool HasActualVSPlatform = (PlatformProjectGenerator != null) && 
				                        PlatformProjectGenerator.HasVisualStudioSupport(Platform, Configuration, ProjectFileFormat);

			if (HasActualVSPlatform)
			{
				ConfigurationNameToUseVS = Configuration.ToString();
				PlatformNameToUseVS = PlatformProjectGenerator.GetVisualStudioPlatformName(Platform, Configuration);
			}
			else
			{
				// Visual Studio doesn't natively support this platform, so we fake it by mapping it to
				// a project configuration that has the platform name in that configuration as a suffix,
				// and then using "Win32" as the actual VS platform name
				ConfigurationNameToUseVS = Platform.ToString() + "_" + Configuration.ToString();
				PlatformNameToUseVS = Tag.Platform.Win64;
			}

			if(TargetConfigurationName != TargetType.Game)
			{
				ConfigurationNameToUseVS += "_" + TargetConfigurationName.ToString();
			}
		}

		// Get the project context for the given solution context
		public override MSBuildProjectContext GetMatchingProjectContext
		(
            TargetType                         SolutionTarget,
            TargetConfiguration          SolutionConfiguration,
            BuildTargetPlatform               SolutionPlatform,
            PlatformProjectGeneratorCollection PlatformProjectGenerators
		)
		{
			// Stub projects always build in the same configuration
			if(IsStubProject)
			{
				return new MSBuildProjectContext(Tag.Project.StubProjectConfiguration, Tag.Platform.Win64);
			}

			// Have to match every solution configuration combination to a project configuration (or use the invalid one)
			string ProjectConfigurationName = "InvalidYet";

			// Get the default platform. If there were not valid platforms for this project, just use one that will always be available in VS.
			string ProjectPlatformName = InvalidConfigPlatformNames.First();

			// Whether the configuration should be built automatically as part of the solution
			bool bBuildByDefault = false;

			// Whether this configuration should deploy by default (requires bBuildByDefault)
			bool bDeployByDefault = false;

			// Programs are built in editor configurations (since the editor is like a desktop program too) and game configurations (since we omit the "game" qualification in the configuration name).
			bool IsProgramProject = ProjectTargets[0].TargetRules != null && 
				                    ProjectTargets[0].TargetRules.Type == TargetType.Program;

			if(!IsProgramProject                  || 
				SolutionTarget == TargetType.Game || 
				SolutionTarget == TargetType.Editor)
			{
				// Get the target type we expect to find for this project
				TargetType TargetConfigurationName = SolutionTarget;
				if (IsProgramProject)
				{
					TargetConfigurationName = TargetType.Program;
				}

				// Now, we want to find a target in this project that maps to the current solution config combination.  Only up to one target should
				// and every solution config combination should map to at least one target in one project (otherwise we shouldn't have added it!).
				List<ProjectTarget> MatchingProjectTargets = new List<ProjectTarget>();
				foreach (ProjectTarget ProjectTarget in ProjectTargets)
				{
					if(VCProjectFile.IsValidProjectPlatformAndConfiguration(ProjectTarget, SolutionPlatform, SolutionConfiguration/*, PlatformProjectGenerators*/))
					{
						if (ProjectTarget.TargetRules != null)
						{
							if (TargetConfigurationName == ProjectTarget.TargetRules.Type)
							{
								MatchingProjectTargets.Add(ProjectTarget);
							}
						}
						else
						{
							if (ShouldBuildForAllSolutionTargets || TargetConfigurationName == TargetType.Game)
							{
								MatchingProjectTargets.Add(ProjectTarget);
							}
						}
					}
				}

				// Always allow SCW and Lighmass to build in editor configurations
				if (MatchingProjectTargets.Count == 0     && 
					SolutionTarget   == TargetType.Editor && 
					SolutionPlatform == BuildTargetPlatform.Win64)
				{
					foreach(ProjectTarget ProjectTarget in ProjectTargets)
					{
						string TargetName = ProjectTargets[0].TargetRules.Name;
						if(TargetName == Tag.Project.ShaderCompileWorker || 
						   TargetName == Tag.Project.LightMass)
						{
							MatchingProjectTargets.Add(ProjectTarget);
							break;
						}
					}
				}

				// Make sure there's only one matching project target
				if(1 < MatchingProjectTargets.Count)
				{
					throw new BuildException("Not expecting more than one target for project {0} to match solution configuration {1} {2} {3}", ProjectFilePath, SolutionTarget, SolutionConfiguration, SolutionPlatform);
				}

				// If we found a matching project, get matching configuration
				if(MatchingProjectTargets.Count == 1)
				{
					// Get the matching target
					ProjectTarget MatchingProjectTarget = MatchingProjectTargets[0];

					// If the project wants to always build in "Development", regardless of what the solution configuration is set to, then we'll do that here.
					TargetConfiguration ProjectConfiguration = SolutionConfiguration;
					if (MatchingProjectTarget.ForceDevelopmentConfiguration && SolutionTarget != TargetType.Game)
					{
						ProjectConfiguration = TargetConfiguration.Development;
					}

					// Get the matching project configuration
					BuildTargetPlatform ProjectPlatform = SolutionPlatform;
					if (IsStubProject)
					{
						if (ProjectConfiguration != TargetConfiguration.Unknown)
						{
							throw new BuildException("Stub project was expecting platform and configuration type to be set to Unknown");
						}
						ProjectConfigurationName = Tag.Project.StubProjectConfiguration;
						ProjectPlatformName      = Tag.Platform.Win64;
					}
					else
					{
						MakeProjectPlatformAndConfigurationNames(ProjectPlatform, ProjectConfiguration, TargetConfigurationName, PlatformProjectGenerators, out ProjectPlatformName, out ProjectConfigurationName);
					}

					// Set whether this project configuration should be built when the user initiates "build solution"
					if (ShouldBuildByDefaultForSolutionTargets)
					{
						// Some targets are "dummy targets"; they only exist to show user friendly errors in VS. Weed them out here, and don't set them to build by default.
						List<BuildTargetPlatform> SupportedPlatforms = null;

						if (MatchingProjectTarget.TargetRules != null)
						{
							SupportedPlatforms = new List<BuildTargetPlatform>();
							SupportedPlatforms.AddRange(MatchingProjectTarget.SupportedPlatforms);
						}
						if (SupportedPlatforms == null || 
							SupportedPlatforms.Contains(SolutionPlatform))
						{
							bBuildByDefault = true;

							PlatformProjectGenerator ProjGen = PlatformProjectGenerators.GetPlatformProjectGenerator(SolutionPlatform, true);
							if (MatchingProjectTarget.ProjectDeploys ||
								((ProjGen != null) && (ProjGen.GetVisualStudioDeploymentEnabled(ProjectPlatform, ProjectConfiguration) == true)))
							{
								bDeployByDefault = true;
							}
						}
					}
				}
			}

			return new MSBuildProjectContext(ProjectConfigurationName, ProjectPlatformName){ bBuildByDefault = bBuildByDefault, bDeployByDefault = bDeployByDefault };
		}

		internal sealed class ProjectConfigAndTargetCombination
		{
			public BuildTargetPlatform?     Platform;
			public TargetConfiguration Configuration;
			public string                    ProjectPlatformName;
			public string                    ProjectConfigurationName;
			public ProjectTarget             ProjectTarget;

			public ProjectConfigAndTargetCombination(BuildTargetPlatform? InPlatform, TargetConfiguration InConfiguration, string InProjectPlatformName, string InProjectConfigurationName, ProjectTarget InProjectTarget)
			{
				Platform                 = InPlatform;
				Configuration            = InConfiguration;
				ProjectPlatformName      = InProjectPlatformName;
				ProjectConfigurationName = InProjectConfigurationName;
				ProjectTarget            = InProjectTarget;
			}

            public string ProjectConfigurationAndPlatformName => ProjectPlatformName ?? (ProjectConfigurationName + "|" + ProjectPlatformName);

            public override string ToString() => String.Format("{0} {1} {2}", ProjectTarget, Platform, Configuration);
        }

		public override void AddPreprocessorDefintionsAndIncludePaths(BuildModuleCPP Module, CppCompileEnvironment CompileEnvironment)
		{
			// API_DEINFE (CompileEnviroment::ForceIncludeFIles)
			// CompileEnvironment에 있는 Definitions도 작성
			base.AddPreprocessorDefintionsAndIncludePaths(Module, CompileEnvironment);

			if (bUsePerFileIntellisense)
			{
				foreach (DirectoryReference ModuleDirectory in Module.ModuleDirectories)
				{
					List<string> ForceIncludePaths = new List<string>(CompileEnvironment.ForceIncludeFiles.Select(x => GetInsertedPathVariables(x.FileDirectory)));

					if (CompileEnvironment.PCHIncludeFilename != null)
					{
						ForceIncludePaths.Add(GetInsertedPathVariables(CompileEnvironment.PCHIncludeFilename));
					}
					// {D:\UERelease\Engine\Source\Runtime\Launch}인 모듈의 ForceIncludePath는 
					// "$(SolutionDir)Engine\\Intermediate\\Build\\Win64\\Editor\\Development\\Launch\\Definitions.h" 이다.
					ModuleDirToForceIncludePaths[Module.ModuleDirectory] = String.Join(";", ForceIncludePaths);
				}
			}
		}

        private static string GetInsertedPathVariables(FileReference Location)
		{
			if (Location.IsUnderDirectory(ProjectFileGenerator.MasterProjectPath))
			{
				return Tag.CppProjectContents.SolutionDir + Location.MakeRelativeTo(ProjectFileGenerator.MasterProjectPath);
			}
			else
			{
				return Location.FullName;
			}
		}

		WindowsCompiler GetCompilerForIntellisense()
		{
			switch(ProjectFileFormat)
			{
				case VCProjectFileFormat.VisualStudio2019:
					return WindowsCompiler.VisualStudio2019;
				case VCProjectFileFormat.VisualStudio2017:
					return WindowsCompiler.VisualStudio2017;
				default:
					return WindowsCompiler.VisualStudio2015_DEPRECATED;
			}
		}

		// Gets highest C++ version which is used in this project
		public CppStandardVersion GetIntelliSenseCppVersion()
		{
			if (IntelliSenseCppVersion != CppStandardVersion.Default)
			{
				return IntelliSenseCppVersion;
			}

			CppStandardVersion Version = CppStandardVersion.Default;
			foreach (ProjectConfigAndTargetCombination Combination in ProjectConfigAndTargetCombinations)
			{
				if (Combination.ProjectTarget != null 
					&& Combination.ProjectTarget.TargetRules != null 
					&& Version < Combination.ProjectTarget.TargetRules.CppStandard)
				{
					Version = Combination.ProjectTarget.TargetRules.CppStandard;
				}
			}

			return Version;
		}
		
		// Gets compiler switch for specifying in AdditionalOptions in .vcxproj file for specific C++ version
		private static string GetIntelliSenseSwitchForCppVersion(CppStandardVersion Version)
		{
			switch (Version)
			{
				case CppStandardVersion.Default:
				case CppStandardVersion.Cpp14:
					return Tag.CppProjectContents.StdCpp14;
				case CppStandardVersion.Cpp17:
					return Tag.CppProjectContents.StdCpp17;
				case CppStandardVersion.Latest:
					return Tag.CppProjectContents.StdCppLatest;
				default:
					throw new ArgumentOutOfRangeException(nameof(Version), Version, "Please update switch above with new C++ version");
			}
		}

		HashSet<string> InvalidConfigPlatformNames;
		List<ProjectConfigAndTargetCombination> ProjectConfigAndTargetCombinations;

		// ProjectConfigAndTargetCombinations
		// [0] = {BenchmarkTool.Target Win32 Debug}
		private void BuildProjectConfigAndTargetCombinations
		(
			List<BuildTargetPlatform>         InPlatforms, 
			List<TargetConfiguration>    InConfigurations, 
			PlatformProjectGeneratorCollection PlatformProjectGenerators
			)
		{
			//no need to do this more than once
			if(ProjectConfigAndTargetCombinations == null)
			{
				// Build up a list of platforms and configurations this project will support.
				// In this list, Unknown simply means that we should use the default "stub" project platform and configuration name.

				// If this is a "stub" project, then only add a single configuration to the project
				ProjectConfigAndTargetCombinations = new List<ProjectConfigAndTargetCombination>();
				if (IsStubProject)
				{
					ProjectConfigAndTargetCombination StubCombination 
						= new ProjectConfigAndTargetCombination
					    (
							BuildTargetPlatform.Parse(Tag.Platform.Win64), 
							TargetConfiguration.Unknown, 
							Tag.Platform.Win64, 
							Tag.Project.StubProjectConfiguration, 
							null
						);

					ProjectConfigAndTargetCombinations.Add(StubCombination);
				}
				else
				{
					// Figure out all the desired configurations
					foreach (TargetConfiguration Configuration in InConfigurations)
					{
						//@todo.Rocket: Put this in a commonly accessible place?
						if (InstalledPlatformInfo.IsValidConfiguration(Configuration, EProjectType.Code) == false)
						{
							continue;
						}

						foreach (BuildTargetPlatform Platform in InPlatforms)
						{
							if (InstalledPlatformInfo.IsValidPlatform(Platform, EProjectType.Code) == false)
							{
								continue;
							}

							BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(Platform, true);

							if ((BuildPlatform != null) && 
								(BuildPlatform.HasRequiredSDKsInstalled() == SDKStatus.Valid))
							{
								// Now go through all of the target types for this project
								if (ProjectTargets.Count == 0)
								{
									throw new BuildException("Expecting at least one ProjectTarget to be associated with project '{0}' in the TargetProjects list ", ProjectFilePath);
								}

								foreach (ProjectTarget ProjectTarget in ProjectTargets)
								{
									if (IsValidProjectPlatformAndConfiguration(ProjectTarget, Platform, Configuration/*, PlatformProjectGenerators*/))
									{
                                        MakeProjectPlatformAndConfigurationNames
										(
                                            Platform,
                                            Configuration,
                                            ProjectTarget.TargetRules.Type,
                                            PlatformProjectGenerators,
                                            out string ProjectPlatformName,
                                            out string ProjectConfigurationName
										);

                                        ProjectConfigAndTargetCombination Combination 
											= new ProjectConfigAndTargetCombination
										    (
                                                Platform,
                                                Configuration,
                                                ProjectPlatformName,
                                                ProjectConfigurationName,
                                                ProjectTarget
											);

										ProjectConfigAndTargetCombinations.Add(Combination);
									}
								}
							}
						}
					}
				}

				// Create a list of platforms for the "invalid" configuration. We always require at least one of these.
				if(ProjectConfigAndTargetCombinations.Count == 0)
				{
					InvalidConfigPlatformNames = new HashSet<string>{ Tag.Platform.Win64 };
				}
				else
				{
					InvalidConfigPlatformNames = new HashSet<string>(ProjectConfigAndTargetCombinations.Select(x => x.ProjectPlatformName));
				}
			}
		}

		// If found writes a debug project file to disk
		public override List<Tuple<ProjectFile, string>> WriteDebugProjectFiles(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			//string ProjectName = ProjectFilePath.GetFileNameWithoutExtension();

			List<BuildTargetPlatform> ProjectPlatforms = new List<BuildTargetPlatform>();
			List<Tuple<ProjectFile, string>> ProjectFiles = new List<Tuple<ProjectFile, string>>();

			BuildProjectConfigAndTargetCombinations(InPlatforms, InConfigurations, PlatformProjectGenerators);


			foreach (ProjectConfigAndTargetCombination Combination in ProjectConfigAndTargetCombinations)
			{
				if (Combination.Platform != null && !ProjectPlatforms.Contains(Combination.Platform.Value))
				{
					ProjectPlatforms.Add(Combination.Platform.Value);
				}
			}

			//write out any additional project files
			if (!IsStubProject && ProjectTargets.Any(x => x.TargetRules != null && x.TargetRules.Type != TargetType.Program))
			{
				foreach (BuildTargetPlatform Platform in ProjectPlatforms)
				{
					PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, true);
					if (ProjGenerator != null)
					{
						//write out additional prop file
						ProjGenerator.WriteAdditionalPropFile(); // Do nothing yet.

						//write out additional project user files
						ProjGenerator.WriteAdditionalProjUserFile(this); // Do nothing yet.

						//write out additional project files
						Tuple<ProjectFile, string> DebugProjectInfo = ProjGenerator.WriteAdditionalProjFile(this);
						if(DebugProjectInfo != null)
						{
							ProjectFiles.Add(DebugProjectInfo);
						}
					}
				}
			}

			return ProjectFiles;
		}

		private string[] FilteredList = null;

		bool IncludePathIsFilteredOut(DirectoryReference IncludePath)
		{			
			// Turn the filter string into an array, remove whitespace, and normalize any path statements the first time
			// we are asked to check a path.
			if (FilteredList == null)
			{
				IEnumerable<string> CleanPaths = ExcludedIncludePaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(P => P.Trim())
					.Select(P => P.Replace('/', Path.DirectorySeparatorChar));

				FilteredList = CleanPaths.ToArray();
			}

			if (FilteredList.Length > 0)
			{
				foreach (string Entry in FilteredList)
				{
					if (IncludePath.FullName.Contains(Entry))
					{
						return true;
					}
				}
			}

			return false;
		}

        // Append a list of include paths to a property list
        private void AppendIncludePaths
		(
            StringBuilder                                          BuilderForProperty,
            DirectoryReference                                     ContainingSrcFileDir,
            Dictionary<DirectoryReference, IncludePathsCollection> BaseDirToIncludePaths,
            HashSet<DirectoryReference>                            IgnorePaths
		)
		{
			for (DirectoryReference CurrentDir = ContainingSrcFileDir; CurrentDir != null; CurrentDir = CurrentDir.ParentDirectory)
			{
                if (BaseDirToIncludePaths.TryGetValue(CurrentDir, out IncludePathsCollection Collection))
                {
                    foreach (DirectoryReference IncludePath in Collection.AbsolutePaths)
                    {
                        if (!IgnorePaths.Contains(IncludePath) && !IncludePathIsFilteredOut(IncludePath))
                        {
                            BuilderForProperty.Append(NormalizeProjectPath(IncludePath.FullName));
                            BuilderForProperty.Append(';');
                        }
                    }
                    break;
                }
            }
		}

		// Implements Project interface
		public override bool WriteProjectFile
		(
            List<BuildTargetPlatform>         InPlatforms,
            List<TargetConfiguration>    InConfigurations,
            PlatformProjectGeneratorCollection PlatformProjectGenerators
		)
		{
			// 여기 무조건 공부 계속공부
			// Debugger.Break();

			string ProjectName = ProjectFilePath.GetFileNameWithoutExtension();

			bool bSuccess = true;

			// Merge as many include paths as possible into the shared list
			HashSet<DirectoryReference> SharedIncludeSearchPathsSet = new HashSet<DirectoryReference>();

			// Build up the new include search path string
			StringBuilder SharedIncludeSearchPaths = new StringBuilder();

			// 주 Loop
			// 0. BaseDirToUserIncludePaths
			// 1. InvalidConfigPlatformNames
			// 2. InvalidConfigPlatformNames
			// 3. ProjectConfigAndTargetCombinations
			{
				// Find out how many source files there are in each directory
				Dictionary<DirectoryReference, int> CPPSourceDirToCount = new Dictionary<DirectoryReference, int>();

				foreach (SourceFile SourceFile in SourceFiles)
				{
					if(SourceFile.Reference.HasExtension(Tag.Ext.CppSource))
					{
						DirectoryReference SourceDir = SourceFile.Reference.Directory;

                        CPPSourceDirToCount.TryGetValue(SourceDir, out int Count);
                        CPPSourceDirToCount[SourceDir] = Count + 1;
					}
				}

				// Figure out the most common include paths
				Dictionary<DirectoryReference, int> IncludePathToCount = new Dictionary<DirectoryReference, int>();
				foreach (KeyValuePair<DirectoryReference, int> Pair in CPPSourceDirToCount)
				{
					for (DirectoryReference CurrentDir = Pair.Key; CurrentDir != null; CurrentDir = CurrentDir.ParentDirectory)
					{
                        if (BaseDirToUserIncludePaths.TryGetValue(CurrentDir, out IncludePathsCollection IncludePaths))
                        {
                            foreach (DirectoryReference IncludePath in IncludePaths.AbsolutePaths)
                            {
                                IncludePathToCount.TryGetValue(IncludePath, out int Count);
                                IncludePathToCount[IncludePath] = Count + Pair.Value;
                            }
                            break;
                        }
                    }
				}

				// Append the most common include paths to the search list.
				const int MaxSharedIncludePathsLength = 24 * 1024;
				foreach (DirectoryReference IncludePath in IncludePathToCount.OrderByDescending(x => x.Value).Select(x => x.Key))
				{
					string RelativePath = NormalizeProjectPath(IncludePath);
					if (SharedIncludeSearchPaths.Length + RelativePath.Length < MaxSharedIncludePathsLength)
					{

						if (!IncludePathIsFilteredOut(IncludePath))
						{
							SharedIncludeSearchPathsSet.Add(IncludePath);
							SharedIncludeSearchPaths.AppendFormat("{0};", RelativePath);
						}						
					}
					else
					{
						break;
					}
				}

				// Add all the default system include paths
				if (InPlatforms.Contains(BuildTargetPlatform.Win64))
				{
					SharedIncludeSearchPaths.Append(VCToolChain.GetVCIncludePaths(BuildTargetPlatform.Win64, GetCompilerForIntellisense(), null) + ";");
				}
				else if (InPlatforms.Contains(BuildTargetPlatform.Win32))
				{
					SharedIncludeSearchPaths.Append(VCToolChain.GetVCIncludePaths(BuildTargetPlatform.Win32, GetCompilerForIntellisense(), null) + ";");
				}
				else if (InPlatforms.Contains(BuildTargetPlatform.HoloLens))
				{
					SharedIncludeSearchPaths.Append(HoloLensToolChain.GetVCIncludePaths(BuildTargetPlatform.HoloLens) + ";");
				}
			}

			StringBuilder VCPreprocessorDefinitions = new StringBuilder();

			foreach (string CurDef in IntelliSensePreprocessorDefinitions)
			{
				if (0 < VCPreprocessorDefinitions.Length)
				{
					VCPreprocessorDefinitions.Append(';');
				}
				VCPreprocessorDefinitions.Append(CurDef);
			}

			// Setup VC project file content
			StringBuilder VCXProjFileContent        = new StringBuilder(); // *.vcxproj
			StringBuilder VCXProjFiltersFileContent = new StringBuilder(); // *.vcxproj.filters
			StringBuilder VCXProjUserFileContent    = new StringBuilder(); // *.vcxproj.user

			// Visual Studio doesn't require a *.vcxproj.filters file to even exist alongside the project
			// unless it actually has something of substance in it.
			// We'll avoid saving it out unless we need to.
			bool FiltersFileIsNeeded = false;

			// Project file header
			VCXProjFileContent.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			VCXProjFileContent.AppendLine("<Project DefaultTargets=\"Build\" ToolsVersion=\"{0}\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">", 
				VCProjectFileGenerator.GetMSBuildToolsVersionString(ProjectFileFormat)); // vs2019 -> "15.0"

			bool bGenerateUserFileContent = PlatformProjectGenerators.PlatformRequiresVSUserFileGeneration(InPlatforms, InConfigurations);
			if (bGenerateUserFileContent)
			{
				VCXProjUserFileContent.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
				VCXProjUserFileContent.AppendLine("<Project ToolsVersion=\"{0}\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">", 
					VCProjectFileGenerator.GetMSBuildToolsVersionString(ProjectFileFormat));
			}

			// Put into
			// VCProjectFile::List<ProjectConfigAndTargetCombination> ProjectConfigAndTargetCombinations;
			BuildProjectConfigAndTargetCombinations(InPlatforms, InConfigurations, PlatformProjectGenerators);

			VCXProjFileContent.AppendLine("  <ItemGroup Label=\"ProjectConfigurations\">");

			// Make a list of the platforms and configs as project-format names
			List<BuildTargetPlatform> ProjectPlatforms = new List<BuildTargetPlatform>();
			var ProjectPlatformNameAndPlatforms           = new List<Tuple<string, BuildTargetPlatform>>(); // ProjectPlatformName, Platform
			var ProjectConfigurationNameAndConfigurations = new List<Tuple<string, TargetConfiguration>>(); // ProjectConfigurationName, Configuration
			
			foreach (ProjectConfigAndTargetCombination Combination in ProjectConfigAndTargetCombinations)
			{
				if (Combination.Platform == null)
				{
					continue;
				}
				if (!ProjectPlatforms.Contains(Combination.Platform.Value))
				{
					ProjectPlatforms.Add(Combination.Platform.Value);
				}
				if (!ProjectPlatformNameAndPlatforms.Any(ProjectPlatformNameAndPlatformTuple => ProjectPlatformNameAndPlatformTuple.Item1 == Combination.ProjectPlatformName))
				{
					ProjectPlatformNameAndPlatforms.Add(Tuple.Create(Combination.ProjectPlatformName, Combination.Platform.Value));
				}
				if (!ProjectConfigurationNameAndConfigurations.Any(ProjectConfigurationNameAndConfigurationTuple => ProjectConfigurationNameAndConfigurationTuple.Item1 == Combination.ProjectConfigurationName))
				{
					ProjectConfigurationNameAndConfigurations.Add(Tuple.Create(Combination.ProjectConfigurationName, Combination.Configuration));
				}
			}

			// Add the "invalid" configuration for each platform. We use this when the solution configuration does not match any project configuration.
			foreach(string InvalidConfigPlatformName in InvalidConfigPlatformNames)
			{
				VCXProjFileContent.AppendLine("    <ProjectConfiguration Include=\"Invalid|{0}\">", InvalidConfigPlatformName);
				VCXProjFileContent.AppendLine("      <Configuration>Invalid</Configuration>");
				VCXProjFileContent.AppendLine("      <Platform>{0}</Platform>", InvalidConfigPlatformName);
				VCXProjFileContent.AppendLine("    </ProjectConfiguration>");
			}

			// Output ALL the project's config-platform permutations (project files MUST do this)
			foreach (Tuple<string, TargetConfiguration> ConfigurationTuple in ProjectConfigurationNameAndConfigurations)
			{
				string ProjectConfigurationName = ConfigurationTuple.Item1;
				foreach (Tuple<string, BuildTargetPlatform> PlatformTuple in ProjectPlatformNameAndPlatforms)
				{
					string ProjectPlatformName = PlatformTuple.Item1;
					VCXProjFileContent.AppendLine("    <ProjectConfiguration Include=\"{0}|{1}\">", ProjectConfigurationName, ProjectPlatformName);
					VCXProjFileContent.AppendLine("      <Configuration>{0}</Configuration>", ProjectConfigurationName);
					VCXProjFileContent.AppendLine("      <Platform>{0}</Platform>", ProjectPlatformName);
					VCXProjFileContent.AppendLine("    </ProjectConfiguration>");
				}
			}

			VCXProjFileContent.AppendLine("  </ItemGroup>");

			VCXProjFiltersFileContent.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
			VCXProjFiltersFileContent.AppendLine("<Project ToolsVersion=\"{0}\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">", 
				VCProjectFileGenerator.GetMSBuildToolsVersionString(ProjectFileFormat));

			// Platform specific PropertyGroups, etc.
			if (!IsStubProject)
			{
				foreach (BuildTargetPlatform Platform in ProjectPlatforms)
				{
					PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, true);
					if (ProjGenerator != null && ProjGenerator.HasVisualStudioSupport(Platform, TargetConfiguration.Development, ProjectFileFormat))
					{
						// Only Adroid ProjGenerator do anything.
						ProjGenerator.GetAdditionalVisualStudioPropertyGroups(Platform, ProjectFileFormat, VCXProjFileContent);
					}
				}
			}

			// Project globals (project GUID, project type, SCC bindings, etc)
			{
				// VCXProjFileContent.AppendFormat("");
				VCXProjFileContent.AppendLine("  <PropertyGroup Label=\"Globals\">");
				VCXProjFileContent.AppendLine("    <ProjectGuid>{0}</ProjectGuid>", ProjectGUID.ToString("B").ToUpperInvariant());
				VCXProjFileContent.AppendLine("    <Keyword>MakeFileProj</Keyword>"); // which of the Windows-specific dependencies you are going to use.
				VCXProjFileContent.AppendLine("    <RootNamespace>{0}</RootNamespace>", ProjectName);

				VCProjectFileGenerator.AppendPlatformToolsetProperty(VCXProjFileContent, ProjectFileFormat);
				VCXProjFileContent.AppendLine("    <MinimumVisualStudioVersion>{0}</MinimumVisualStudioVersion>", VCProjectFileGenerator.GetMSBuildToolsVersionString(ProjectFileFormat));
				VCXProjFileContent.AppendLine("    <TargetRuntime>Native</TargetRuntime>");
				VCXProjFileContent.AppendLine("  </PropertyGroup>");
			}

			// look for additional import lines for all platforms for non stub projects
			if (!IsStubProject)
			{
				foreach (BuildTargetPlatform Platform in ProjectPlatforms)
				{
					PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, true);
					if (ProjGenerator != null && ProjGenerator.HasVisualStudioSupport(Platform, TargetConfiguration.Development, ProjectFileFormat))
					{
						// Only Adroid ProjGenerator do anything.
						ProjGenerator.GetVisualStudioGlobalProperties(Platform, VCXProjFileContent);
					}
				}
			}

			if (!IsStubProject)
			{
				// TODO: Restrict this to only the Lumin platform targets, routing via GetVisualStudioGlobalProperties().
				// Currently hacking here because returning true from HasVisualStudioSupport() for lumin causes bunch of faiures in VS.
				VCXProjFileContent.AppendLine("  <ItemGroup>");
				VCXProjFileContent.AppendLine("    <ProjectCapability Include=\"MLProject\" />");
				VCXProjFileContent.AppendLine("    <PropertyPageSchema Include=\"$(LOCALAPPDATA)\\Microsoft\\VisualStudio\\MagicLeap\\debugger.xaml\" />");
				VCXProjFileContent.AppendLine("  </ItemGroup>");
			}

			// Write each project configuration PreDefaultProps section
			foreach (Tuple<string, TargetConfiguration> ConfigurationTuple in ProjectConfigurationNameAndConfigurations)
			{
				string ProjectConfigurationName = ConfigurationTuple.Item1;
				TargetConfiguration TargetConfiguration = ConfigurationTuple.Item2;
				foreach (Tuple<string, BuildTargetPlatform> PlatformTuple in ProjectPlatformNameAndPlatforms)
				{
					string               ProjectPlatformName = PlatformTuple.Item1;
					BuildTargetPlatform TargetPlatform      = PlatformTuple.Item2;

					WritePreDefaultPropsConfiguration
					(
						TargetPlatform, 
						TargetConfiguration, 
						ProjectPlatformName, 
						ProjectConfigurationName, 
						PlatformProjectGenerators, 
						VCXProjFileContent
					);
				}
			}

			VCXProjFileContent.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.Default.props\" />");

			// Write the invalid configuration data
			foreach(string InvalidConfigPlatformName in InvalidConfigPlatformNames)
			{
				VCXProjFileContent.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='Invalid|{0}'\" Label=\"Configuration\">", InvalidConfigPlatformName);
				VCXProjFileContent.AppendLine("    <ConfigurationType>Makefile</ConfigurationType>");
				VCXProjFileContent.AppendLine("  </PropertyGroup>");
			}

			// Write each project configuration PreDefaultProps section
			foreach (Tuple<string, TargetConfiguration> ConfigurationTuple in ProjectConfigurationNameAndConfigurations)
			{
				string ProjectConfigurationName = ConfigurationTuple.Item1;
				TargetConfiguration TargetConfiguration = ConfigurationTuple.Item2;
				foreach (Tuple<string, BuildTargetPlatform> PlatformTuple in ProjectPlatformNameAndPlatforms)
				{
					string ProjectPlatformName = PlatformTuple.Item1;
					BuildTargetPlatform TargetPlatform = PlatformTuple.Item2;
					WritePostDefaultPropsConfiguration(TargetPlatform, TargetConfiguration, ProjectPlatformName, ProjectConfigurationName, PlatformProjectGenerators, VCXProjFileContent);
				}
			}

			VCXProjFileContent.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.props\" />");
			VCXProjFileContent.AppendLine("  <ImportGroup Label=\"ExtensionSettings\" />");
			VCXProjFileContent.AppendLine("  <PropertyGroup Label=\"UserMacros\" />");

			// Write the invalid configuration
			foreach(string InvalidConfigPlatformName in InvalidConfigPlatformNames)
			{
				const string InvalidMessage = "echo The selected platform/configuration is not valid for this target.";

				string ProjectRelativeUnusedDirectory = NormalizeProjectPath(DirectoryReference.Combine(BuildTool.EngineDirectory, "Intermediate", "Build", "Unused"));

				VCXProjFileContent.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)|$(Platform)'=='Invalid|{0}'\">", InvalidConfigPlatformName);
				VCXProjFileContent.AppendLine("    <NMakeBuildCommandLine>{0}</NMakeBuildCommandLine>", InvalidMessage);
				VCXProjFileContent.AppendLine("    <NMakeReBuildCommandLine>{0}</NMakeReBuildCommandLine>", InvalidMessage);
				VCXProjFileContent.AppendLine("    <NMakeCleanCommandLine>{0}</NMakeCleanCommandLine>", InvalidMessage);
				VCXProjFileContent.AppendLine("    <NMakeOutput>Invalid Output</NMakeOutput>", InvalidMessage);
				VCXProjFileContent.AppendLine("    <OutDir>{0}{1}</OutDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
				VCXProjFileContent.AppendLine("    <IntDir>{0}{1}</IntDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
				VCXProjFileContent.AppendLine("  </PropertyGroup>");
			}

			// Write each project configuration
			// Platform x Target
			foreach (ProjectConfigAndTargetCombination Combination in ProjectConfigAndTargetCombinations)
			{
				WriteConfiguration(ProjectName, Combination, VCXProjFileContent, PlatformProjectGenerators, bGenerateUserFileContent ? VCXProjUserFileContent : null);
			}

			// Write IntelliSense info
			{
				// @todo projectfiles:
				// Currently we are storing defines/include paths for ALL configurations rather than using ConditionString and storing
				// this data uniquely for each target configuration.  IntelliSense may behave better if we did that, but it will result in a LOT more
				// data being stored into the project file, and might make the IDE perform worse when switching configurations!
				VCXProjFileContent.AppendLine("  <PropertyGroup>");
				VCXProjFileContent.AppendLine("    <NMakePreprocessorDefinitions>$(NMakePreprocessorDefinitions){0}</NMakePreprocessorDefinitions>", 
					0 < VCPreprocessorDefinitions.Length? (";" + VCPreprocessorDefinitions) : "");
				// NOTE: Setting the IncludePath property rather than NMakeIncludeSearchPath results in significantly less
				// memory usage, because NMakeIncludeSearchPath metadata is duplicated to each output item. Functionality should be identical for
				// intellisense results.
				VCXProjFileContent.AppendLine("    <IncludePath>$(IncludePath){0}</IncludePath>", 
					0 < SharedIncludeSearchPaths.Length? (";" + SharedIncludeSearchPaths) : "");
				VCXProjFileContent.AppendLine("    <NMakeForcedIncludes>$(NMakeForcedIncludes)</NMakeForcedIncludes>");
				VCXProjFileContent.AppendLine("    <NMakeAssemblySearchPath>$(NMakeAssemblySearchPath)</NMakeAssemblySearchPath>");
				VCXProjFileContent.AppendLine("    <AdditionalOptions>{0}</AdditionalOptions>",
					GetIntelliSenseSwitchForCppVersion(GetIntelliSenseCppVersion()));
				VCXProjFileContent.AppendLine("  </PropertyGroup>");
			}

			// Source folders and files
			{
				List<AliasedFile> LocalAliasedFiles = new List<AliasedFile>(AliasedFiles);

				foreach (SourceFile CurFile in SourceFiles)
				{
					// We want all source file and directory paths in the project files to be relative to the project file's location on the disk.
					// Convert the path to be relative to the project file directory
					string ProjectRelativeSourceFile = CurFile.Reference.MakeRelativeTo(ProjectFilePath.Directory);

					// By default, files will appear relative to the project file in the solution.
					// This is kind of the normal Visual  Studio way to do things,
					// but because our generated project files are emitted to intermediate folders,
					// if we always did this it would yield really ugly paths int he solution explorer
					string FilterRelativeSourceDirectory;
					if (CurFile.BaseFolder == null)
					{
						FilterRelativeSourceDirectory = ProjectRelativeSourceFile;
					}
					else
					{
						FilterRelativeSourceDirectory = CurFile.Reference.MakeRelativeTo(CurFile.BaseFolder);
					}

					// Manually remove the filename for the filter. We run through this code path a lot, so just do it manually.
					int LastSeparatorIdx = FilterRelativeSourceDirectory.LastIndexOf(Path.DirectorySeparatorChar);
					if (LastSeparatorIdx == -1)
					{
						FilterRelativeSourceDirectory = "";
					}
					else
					{
						FilterRelativeSourceDirectory = FilterRelativeSourceDirectory.Substring(0, LastSeparatorIdx);
					}

					LocalAliasedFiles.Add(new AliasedFile(CurFile.Reference, ProjectRelativeSourceFile, FilterRelativeSourceDirectory));
				}

				VCXProjFiltersFileContent.AppendLine("  <ItemGroup>");

				VCXProjFileContent.AppendLine("  <ItemGroup>");

				// Add all file directories to the filters file as solution filters
				HashSet<string> FilterDirectories = new HashSet<string>();
				//UEBuildPlatform BuildPlatform = UEBuildPlatform.GetBuildPlatform(BuildHostPlatform.Current.Platform);

				Dictionary<DirectoryReference, string> DirectoryToIncludeSearchPaths = new Dictionary<DirectoryReference, string>();
				Dictionary<DirectoryReference, string> DirectoryToForceIncludePaths = new Dictionary<DirectoryReference, string>();
				
				foreach (AliasedFile AliasedFile in LocalAliasedFiles)
				{
					// No need to add the root directory relative to the project (it would just be an empty string!)
					// If Exists Filter(Folder)Name in cppProjectFilter
					if (!String.IsNullOrWhiteSpace(AliasedFile.ProjectPath))
					{
						FiltersFileIsNeeded = EnsureFilterPathExists(AliasedFile.ProjectPath, VCXProjFiltersFileContent, FilterDirectories);
					}

					string VCFileType = GetVCFileType(AliasedFile.FileSystemPath);
					if (VCFileType != "ClCompile")
					{
						if (!IncludePathIsFilteredOut(new DirectoryReference(AliasedFile.FileSystemPath)))
						{
							VCXProjFileContent.AppendLine("    <{0} Include=\"{1}\"/>", VCFileType, SecurityElement.Escape(AliasedFile.FileSystemPath));
						}
					}
					else
					{
						DirectoryReference Directory = AliasedFile.Location.Directory;

                        // Find the force-included headers
                        if (!DirectoryToForceIncludePaths.TryGetValue(Directory, out string ForceIncludePaths))
                        {
                            for (DirectoryReference ParentDir = Directory; ParentDir != null; ParentDir = ParentDir.ParentDirectory)
                            {
                                if (ModuleDirToForceIncludePaths.TryGetValue(ParentDir, out ForceIncludePaths))
                                {
                                    break;
                                }
                            }

                            // Filter is a little more graceful to do it where this info is built
							// but easier to follow if we filter things our right before they're written.
                            if (ForceIncludePaths.HasValue())
                            {
                                IEnumerable<string> PathList = ForceIncludePaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                                ForceIncludePaths = string.Join(";", PathList.Where(P => !IncludePathIsFilteredOut(new DirectoryReference(P))));
                            }

                            DirectoryToForceIncludePaths[Directory] = ForceIncludePaths;
                        }

                        // Find the include search paths
                        if (!DirectoryToIncludeSearchPaths.TryGetValue(Directory, out string IncludeSearchPaths))
                        {
                            StringBuilder Builder = new StringBuilder();
                            AppendIncludePaths(Builder, Directory, BaseDirToUserIncludePaths, SharedIncludeSearchPathsSet);
                            AppendIncludePaths(Builder, Directory, BaseDirToSystemIncludePaths, SharedIncludeSearchPathsSet);
                            IncludeSearchPaths = Builder.ToString();

                            DirectoryToIncludeSearchPaths.Add(Directory, IncludeSearchPaths);
                        }

                        VCXProjFileContent.AppendLine("    <{0} Include=\"{1}\">", VCFileType, SecurityElement.Escape(AliasedFile.FileSystemPath));
						VCXProjFileContent.AppendLine("      <AdditionalIncludeDirectories>$(NMakeIncludeSearchPath);{0}</AdditionalIncludeDirectories>", IncludeSearchPaths);
						VCXProjFileContent.AppendLine("      <ForcedIncludeFiles>$(NMakeForcedIncludes);{0}</ForcedIncludeFiles>", ForceIncludePaths);
						VCXProjFileContent.AppendLine("    </{0}>", VCFileType);
					}

					// Is Project Filter Files?
					if (!String.IsNullOrWhiteSpace(AliasedFile.ProjectPath))
					{
						VCXProjFiltersFileContent.AppendLine("    <{0} Include=\"{1}\">", VCFileType, SecurityElement.Escape(AliasedFile.FileSystemPath));
						VCXProjFiltersFileContent.AppendLine("      <Filter>{0}</Filter>", StringUtils.CleanDirectorySeparators(SecurityElement.Escape(AliasedFile.ProjectPath)));
						VCXProjFiltersFileContent.AppendLine("    </{0}>", VCFileType);

						FiltersFileIsNeeded = true;
					}
					else
					{
						// No need to specify the root directory relative to the project (it would just be an empty string!)
						VCXProjFiltersFileContent.AppendLine("    <{0} Include=\"{1}\" />", VCFileType, SecurityElement.Escape(AliasedFile.FileSystemPath));
					}
				}

				VCXProjFileContent.AppendLine("  </ItemGroup>");

				VCXProjFiltersFileContent.AppendLine("  </ItemGroup>");
			}

			// For Installed engine builds, include engine source in the source search paths if it exists.
			// We never build it locally, so the debugger can't find it.
			if (BuildTool.IsEngineInstalled() && !IsStubProject)
			{
				VCXProjFileContent.AppendLine("  <PropertyGroup>");
				VCXProjFileContent.Append("    <SourcePath>");
				foreach (string DirectoryName in Directory.EnumerateDirectories(BuildTool.EngineSourceDirectory.FullName, "*", SearchOption.AllDirectories))
				{
					if (Directory.EnumerateFiles(DirectoryName, Tag.Ext.CppSource).Any())
					{
						VCXProjFileContent.Append(DirectoryName);
						VCXProjFileContent.Append(";");
					}
				}
				VCXProjFileContent.AppendLine("</SourcePath>");
				VCXProjFileContent.AppendLine("  </PropertyGroup>");
			}

			string OutputManifestString = "";
			if (!IsStubProject)
			{
				foreach (BuildTargetPlatform Platform in ProjectPlatforms)
				{
					PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, true);
					if (ProjGenerator != null && ProjGenerator.HasVisualStudioSupport(Platform, TargetConfiguration.Development, ProjectFileFormat))
					{
						// @todo projectfiles: Serious hacks here because we are trying to emit one-time platform-specific sections that need information
						//    about a target type, but the project file may contain many types of targets!  Some of this logic will need to move into
						//    the per-target configuration writing code.
						TargetType HackTargetType = TargetType.Game;
						FileReference HackTargetFilePath = null;
						foreach (ProjectConfigAndTargetCombination Combination in ProjectConfigAndTargetCombinations)
						{
							if (Combination.Platform == Platform &&
								Combination.ProjectTarget.TargetRules != null &&
								Combination.ProjectTarget.TargetRules.Type == HackTargetType)
							{
								HackTargetFilePath = Combination.ProjectTarget.TargetFilePath;// ProjectConfigAndTargetCombinations[0].ProjectTarget.TargetFilePath;
								break;
							}
						}

						if (HackTargetFilePath != null)
						{
							OutputManifestString += ProjGenerator.GetVisualStudioOutputManifestSection(Platform, HackTargetType, HackTargetFilePath, ProjectFilePath, ProjectFileFormat);
						}
					}
				}
			}

			VCXProjFileContent.Append(OutputManifestString); // output manifest must come before the Cpp.targets file.
			VCXProjFileContent.AppendLine("  <ItemDefinitionGroup>");
			VCXProjFileContent.AppendLine("  </ItemDefinitionGroup>");
			VCXProjFileContent.AppendLine("  <Import Project=\"$(VCTargetsPath)\\Microsoft.Cpp.targets\" />");
			// Make sure CleanDependsOn is defined empty so the CppClean task isn't run when cleaning targets (use makefile instead)
			VCXProjFileContent.AppendLine("  <PropertyGroup>");
			VCXProjFileContent.AppendLine("    <CleanDependsOn> $(CleanDependsOn); </CleanDependsOn>");
			VCXProjFileContent.AppendLine("    <CppCleanDependsOn></CppCleanDependsOn>");
			VCXProjFileContent.AppendLine("  </PropertyGroup>");
			if (!IsStubProject)
			{
				foreach (BuildTargetPlatform Platform in ProjectPlatforms)
				{
					PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(Platform, true);
					if (ProjGenerator != null && ProjGenerator.HasVisualStudioSupport(Platform, TargetConfiguration.Development, ProjectFileFormat))
					{
						ProjGenerator.GetVisualStudioTargetOverrides(Platform, ProjectFileFormat, VCXProjFileContent);
					}
				}
			}
			VCXProjFileContent.AppendLine("  <ImportGroup Label=\"ExtensionTargets\">");
			VCXProjFileContent.AppendLine("  </ImportGroup>");
			VCXProjFileContent.AppendLine("</Project>");

			VCXProjFiltersFileContent.AppendLine("</Project>");

			if (bGenerateUserFileContent)
			{
				VCXProjUserFileContent.AppendLine("</Project>");
			}

			// *.vcxproj, *.vsproj 변경 방지위해 주석화.
			// Debugger.Break();
			return bSuccess;
			/*
			// Save the project file
			if (bSuccess)
			{
				bSuccess = ProjectFileGenerator.WriteFileIfChanged(ProjectFilePath.FullName, VCProjectFileContent.ToString());
			}


			// Save the filters file
			if (bSuccess)
			{
				// Create a path to the project file's filters file
				string VCFiltersFilePath = ProjectFilePath.FullName + ".filters";
				if (FiltersFileIsNeeded)
				{
					bSuccess = ProjectFileGenerator.WriteFileIfChanged(VCFiltersFilePath, VCFiltersFileContent.ToString());
				}
				else
				{
					Log.TraceVerbose("Deleting Visual C++ filters file which is no longer needed: " + VCFiltersFilePath);

					// Delete the filters file, if one exists.  We no longer need it
					try
					{
						File.Delete(VCFiltersFilePath);
					}
					catch (Exception)
					{
						Log.TraceInformation("Error deleting filters file (file may not be writable): " + VCFiltersFilePath);
					}
				}
			}

			// Save the user file, if required
			if (VCUserFileContent.Length > 0)
			{
				// Create a path to the project file's user file
				string VCUserFilePath = ProjectFilePath.FullName + ".user";
				// Never overwrite the existing user path as it will cause them to lose their settings
				if (File.Exists(VCUserFilePath) == false)
				{
					bSuccess = ProjectFileGenerator.WriteFileIfChanged(VCUserFilePath, VCUserFileContent.ToString());
				}
			}
			*/
		}

		private static bool EnsureFilterPathExists(string FilterRelativeSourceDirectory, StringBuilder VCFiltersFileContent, HashSet<string> FilterDirectories)
		{
			// We only want each directory to appear once in the filters file
			string PathRemaining = StringUtils.CleanDirectorySeparators(FilterRelativeSourceDirectory);
			bool FiltersFileIsNeeded = false;
			if (!FilterDirectories.Contains(PathRemaining))
			{
				// Make sure all subdirectories leading up to this directory each have their own filter, too!
				List<string> AllDirectoriesInPath = new List<string>();
				string PathSoFar = "";
				for (; ; )
				{
					if (PathRemaining.Length > 0)
					{
						int SlashIndex = PathRemaining.IndexOf(Path.DirectorySeparatorChar);
						string SplitDirectory;
						if (SlashIndex != -1)
						{
							SplitDirectory = PathRemaining.Substring(0, SlashIndex);
							PathRemaining = PathRemaining.Substring(SplitDirectory.Length + 1);
						}
						else
						{
							SplitDirectory = PathRemaining;
							PathRemaining = "";
						}
						if (!String.IsNullOrEmpty(PathSoFar))
						{
							PathSoFar += Path.DirectorySeparatorChar;
						}
						PathSoFar += SplitDirectory;

						AllDirectoriesInPath.Add(PathSoFar);
					}
					else
					{
						break;
					}
				}

				foreach (string LeadingDirectory in AllDirectoriesInPath)
				{
					if (!FilterDirectories.Contains(LeadingDirectory))
					{
						FilterDirectories.Add(LeadingDirectory);

						// Generate a unique GUID for this folder
						// NOTE: When saving generated project files, we ignore differences in GUIDs if every other part of the file
						//       matches identically with the pre-existing file
						string FilterGUID = Guid.NewGuid().ToString("B").ToUpperInvariant();

						VCFiltersFileContent.AppendLine("    <Filter Include=\"{0}\">", SecurityElement.Escape(LeadingDirectory));
						VCFiltersFileContent.AppendLine("      <UniqueIdentifier>{0}</UniqueIdentifier>", FilterGUID);
						VCFiltersFileContent.AppendLine("    </Filter>");

						FiltersFileIsNeeded = true;
					}
				}
			}

			return FiltersFileIsNeeded;
		}

		// Returns the VCFileType element name based on the file path.
		private string GetVCFileType(string Path)
		{
			// What type of file is this?
			if (Path.EndsWith(Tag.Ext.Header, StringComparison.InvariantCultureIgnoreCase) ||
				Path.EndsWith(Tag.Ext.Inline, StringComparison.InvariantCultureIgnoreCase))
			{
				return "ClInclude";
			}
			else if (Path.EndsWith(Tag.Ext.CppSource, StringComparison.InvariantCultureIgnoreCase))
			{
				return "ClCompile";
			}
			else if (Path.EndsWith(Tag.Ext.RC, StringComparison.InvariantCultureIgnoreCase))
			{
				return "ResourceCompile";
			}
			else if (Path.EndsWith(Tag.Ext.Manifest, StringComparison.InvariantCultureIgnoreCase))
			{
				return "Manifest";
			}
			else
			{
				return "None";
			}
		}

		// Anonymous function that writes pre-Default.props configuration data
		private void WritePreDefaultPropsConfiguration(BuildTargetPlatform TargetPlatform, TargetConfiguration TargetConfiguration, string ProjectPlatformName, string ProjectConfigurationName, PlatformProjectGeneratorCollection PlatformProjectGenerators, StringBuilder VCProjectFileContent)
		{
			PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(TargetPlatform, true);
			if (ProjGenerator == null)
			{
				return;
			}

			string ProjectConfigurationAndPlatformName = ProjectConfigurationName + "|" + ProjectPlatformName;
			string ConditionString = "Condition=\"'$(Configuration)|$(Platform)'=='" + ProjectConfigurationAndPlatformName + "'\"";

			if(ProjGenerator != null)
			{
				StringBuilder PlatformToolsetString = new StringBuilder();
				ProjGenerator.GetVisualStudioPreDefaultString(TargetPlatform, TargetConfiguration, PlatformToolsetString);

				if (0 < PlatformToolsetString.Length)
				{
					VCProjectFileContent.AppendLine("  <PropertyGroup " + ConditionString + " Label=\"Configuration\">", ConditionString);
					VCProjectFileContent.Append(PlatformToolsetString);
					VCProjectFileContent.AppendLine("  </PropertyGroup>");
				}
			}
		}

		// Anonymous function that writes post-Default.props configuration data
		private void WritePostDefaultPropsConfiguration(BuildTargetPlatform TargetPlatform, TargetConfiguration TargetConfiguration, string ProjectPlatformName, string ProjectConfigurationName, PlatformProjectGeneratorCollection PlatformProjectGenerators, StringBuilder VCProjectFileContent)
		{
			PlatformProjectGenerator ProjGenerator = PlatformProjectGenerators.GetPlatformProjectGenerator(TargetPlatform, true);

			string ProjectConfigurationAndPlatformName = ProjectConfigurationName + "|" + ProjectPlatformName;
			string ConditionString = "Condition=\"'$(Configuration)|$(Platform)'=='" + ProjectConfigurationAndPlatformName + "'\"";

			StringBuilder PlatformToolsetString = new StringBuilder();
			if (ProjGenerator != null)
			{
				ProjGenerator.GetVisualStudioPlatformToolsetString(TargetPlatform, TargetConfiguration, ProjectFileFormat, PlatformToolsetString);
			}

			string PlatformConfigurationType = (ProjGenerator == null) ? "Makefile" : ProjGenerator.GetVisualStudioPlatformConfigurationType(TargetPlatform, ProjectFileFormat);
			VCProjectFileContent.AppendLine("  <PropertyGroup {0} Label=\"Configuration\">", ConditionString);
			VCProjectFileContent.AppendLine("    <ConfigurationType>{0}</ConfigurationType>", PlatformConfigurationType);

			if (PlatformToolsetString.Length == 0)
			{
				VCProjectFileGenerator.AppendPlatformToolsetProperty(VCProjectFileContent, ProjectFileFormat);
			}
			else
			{
				VCProjectFileContent.Append(PlatformToolsetString);
			}

			VCProjectFileContent.AppendLine("  </PropertyGroup>");
		}

		// Anonymous function that writes project configuration data
		private void WriteConfiguration(string ProjectName, ProjectConfigAndTargetCombination Combination, StringBuilder VCProjectFileContent, PlatformProjectGeneratorCollection PlatformProjectGenerators, StringBuilder VCUserFileContent)
		{
			TargetConfiguration Configuration = Combination.Configuration;

			PlatformProjectGenerator ProjGenerator = Combination.Platform != null ? PlatformProjectGenerators.GetPlatformProjectGenerator(Combination.Platform.Value, true) : null;

			string UProjectPath = "";
			if (IsForeignProject)
			{
				UProjectPath = String.Format("\"{0}\"", GetInsertedPathVariables(Combination.ProjectTarget.ProjectFilePath));
			}

			string ConditionString = "Condition=\"'$(Configuration)|$(Platform)'=='" + Combination.ProjectConfigurationAndPlatformName + "'\"";

			{
				VCProjectFileContent.AppendLine("  <ImportGroup {0} Label=\"PropertySheets\">", ConditionString);
				VCProjectFileContent.AppendLine("    <Import Project=\"$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props\" Condition=\"exists('$(UserRootDir)\\Microsoft.Cpp.$(Platform).user.props')\" Label=\"LocalAppDataPlatform\" />");
				if(ProjGenerator != null)
				{
					ProjGenerator.GetVisualStudioImportGroupProperties(Combination.Platform.Value, VCProjectFileContent);
				}
				VCProjectFileContent.AppendLine("  </ImportGroup>");

				DirectoryReference ProjectDirectory = ProjectFilePath.Directory;

				if (IsStubProject)
				{
					string ProjectRelativeUnusedDirectory = NormalizeProjectPath(DirectoryReference.Combine(BuildTool.EngineDirectory, "Intermediate", "Build", "Unused"));

					VCProjectFileContent.AppendLine("  <PropertyGroup {0}>", ConditionString);
					VCProjectFileContent.AppendLine("    <OutDir>{0}{1}</OutDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
					VCProjectFileContent.AppendLine("    <IntDir>{0}{1}</IntDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
					VCProjectFileContent.AppendLine("    <NMakeBuildCommandLine>@rem Nothing to do.</NMakeBuildCommandLine>");
					VCProjectFileContent.AppendLine("    <NMakeReBuildCommandLine>@rem Nothing to do.</NMakeReBuildCommandLine>");
					VCProjectFileContent.AppendLine("    <NMakeCleanCommandLine>@rem Nothing to do.</NMakeCleanCommandLine>");
					VCProjectFileContent.AppendLine("    <NMakeOutput/>");
					VCProjectFileContent.AppendLine("  </PropertyGroup>");
				}
				else if (BuildTool.IsEngineInstalled() && Combination.ProjectTarget != null && Combination.ProjectTarget.TargetRules != null && 
					(Combination.Platform == null || !Combination.ProjectTarget.SupportedPlatforms.Contains(Combination.Platform.Value)))
				{
					string ProjectRelativeUnusedDirectory = NormalizeProjectPath(DirectoryReference.Combine(BuildTool.EngineDirectory, "Intermediate", "Build", "Unused"));

					string TargetName = Combination.ProjectTarget.TargetFilePath.GetFileNameWithoutAnyExtensions();
					string ValidPlatforms = String.Join(", ", Combination.ProjectTarget.SupportedPlatforms.Select(x => x.ToString()));

					VCProjectFileContent.AppendLine("  <PropertyGroup {0}>", ConditionString);
					VCProjectFileContent.AppendLine("    <OutDir>{0}{1}</OutDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
					VCProjectFileContent.AppendLine("    <IntDir>{0}{1}</IntDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
					VCProjectFileContent.AppendLine("    <NMakeBuildCommandLine>@echo {0} is not a supported platform for {1}. Valid platforms are {2}.</NMakeBuildCommandLine>", Combination.Platform, TargetName, ValidPlatforms);
					VCProjectFileContent.AppendLine("    <NMakeReBuildCommandLine>@echo {0} is not a supported platform for {1}. Valid platforms are {2}.</NMakeReBuildCommandLine>", Combination.Platform, TargetName, ValidPlatforms);
					VCProjectFileContent.AppendLine("    <NMakeCleanCommandLine>@echo {0} is not a supported platform for {1}. Valid platforms are {2}.</NMakeCleanCommandLine>", Combination.Platform, TargetName, ValidPlatforms);
					VCProjectFileContent.AppendLine("    <NMakeOutput/>");
					VCProjectFileContent.AppendLine("  </PropertyGroup>");
				}
				else
				{
					BuildTargetPlatform Platform = Combination.Platform.Value;
					TargetRules TargetRulesObject = Combination.ProjectTarget.TargetRules;
					FileReference TargetFilePath = Combination.ProjectTarget.TargetFilePath;
					string TargetName                 = TargetFilePath.GetFileNameWithoutAnyExtensions();
					string BuildToolPlatformName      = Platform.ToString();
					string BuildToolConfigurationName = Configuration.ToString();

					// Setup output path
					BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(Platform);

					// Figure out if this is a monolithic build
					bool bShouldCompileMonolithic = BuildPlatform.ShouldCompileMonolithicBinary(Platform);
					if(!bShouldCompileMonolithic)
					{
						bShouldCompileMonolithic = (Combination.ProjectTarget.CreateRulesDelegate(Platform, Configuration).LinkType == TargetLinkType.Monolithic);
					}

					// Get the output directory
					DirectoryReference RootDirectory = BuildTool.EngineDirectory;
					if (TargetRulesObject.Type != TargetType.Program && (bShouldCompileMonolithic || TargetRulesObject.BuildEnvironment == TargetBuildEnvironment.Unique))
					{
						if(Combination.ProjectTarget.ProjectFilePath != null)
						{
							RootDirectory = Combination.ProjectTarget.ProjectFilePath.Directory;
						}
					}

					if (TargetRulesObject.Type == TargetType.Program && Combination.ProjectTarget.ProjectFilePath != null)
					{
						RootDirectory = Combination.ProjectTarget.ProjectFilePath.Directory;
					}

					// Get the output directory
					DirectoryReference OutputDirectory = DirectoryReference.Combine(RootDirectory, Tag.Directory.Binaries, BuildToolPlatformName);

					if (TargetRulesObject.ExeBinariesSubFolder.HasValue())
					{
						OutputDirectory = DirectoryReference.Combine(OutputDirectory, TargetRulesObject.ExeBinariesSubFolder);
					}

					// Get the executable name (minus any platform or config suffixes)
					string BaseExeName = TargetName;
					if (!bShouldCompileMonolithic && TargetRulesObject.Type != TargetType.Program && TargetRulesObject.BuildEnvironment != TargetBuildEnvironment.Unique)
					{
						BaseExeName = "MyEngine" + TargetRulesObject.Type.ToString();
					}

					// Make the output file path
					FileReference NMakePath = FileReference.Combine(OutputDirectory, BaseExeName);
					if (Configuration != TargetRulesObject.UndecoratedConfiguration)
					{
						NMakePath += "-" + BuildToolPlatformName + "-" + BuildToolConfigurationName;
					}
					NMakePath += TargetRulesObject.Architecture;
					NMakePath += BuildPlatform.GetBinaryExtension(BuildBinaryType.Executable);

					VCProjectFileContent.AppendLine("  <PropertyGroup {0}>", ConditionString);

					StringBuilder PathsStringBuilder = new StringBuilder();
					if(ProjGenerator != null)
					{
						ProjGenerator.GetVisualStudioPathsEntries(Platform, Configuration, TargetRulesObject.Type, TargetFilePath, ProjectFilePath, NMakePath, ProjectFileFormat, PathsStringBuilder);
					}

					string PathStrings = PathsStringBuilder.ToString();
					if (string.IsNullOrEmpty(PathStrings) || (PathStrings.Contains("<IntDir>") == false))
					{
						string ProjectRelativeUnusedDirectory = "$(ProjectDir)..\\Build\\Unused";
						VCProjectFileContent.Append(PathStrings);

						VCProjectFileContent.AppendLine("    <OutDir>{0}{1}</OutDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
						VCProjectFileContent.AppendLine("    <IntDir>{0}{1}</IntDir>", ProjectRelativeUnusedDirectory, Path.DirectorySeparatorChar);
					}
					else
					{
						VCProjectFileContent.Append(PathStrings);
					}

					StringBuilder BuildArguments = new StringBuilder();

					BuildArguments.AppendFormat("{0} {1} {2}", TargetName, BuildToolPlatformName, BuildToolConfigurationName);
					if (IsForeignProject)
					{
						BuildArguments.Append(" " + Tag.GlobalArgument.Project + UProjectPath);
					}

					List<string> ExtraTargets = new List<string>();
					if (!bUsePrecompiled)
					{
						if (TargetRulesObject.Type == TargetType.Editor 
							&& bEditorDependsOnShaderCompileWorker 
							&& !BuildTool.IsEngineInstalled())
						{
							ExtraTargets.Add(Tag.Module.Engine.ShaderCompileWorker + " " + Tag.Platform.Win64 + " " + Tag.Configuration.Development);
						}
						if (TargetRulesObject.bWithLiveCoding 
							&& bBuildLiveCodingConsole 
							&& !BuildTool.IsEngineInstalled() 
							&& TargetRulesObject.Name != Tag.Module.Engine.LiveCodingConsole)
						{
							ExtraTargets.Add(Tag.Module.Engine.LiveCodingConsole + " " + Tag.Platform.Win64 + " " + (TargetRulesObject.bUseDebugLiveCodingConsole?  Tag.Configuration.Debug : Tag.Configuration.Development));
						}
					}

					if(0 < ExtraTargets.Count)
					{
						BuildArguments.Replace("\"", "\\\"");
						BuildArguments.Insert(0, Tag.GlobalArgument.Target + "\"");
						BuildArguments.Append("\"");
						foreach(string ExtraTarget in ExtraTargets)
						{
							BuildArguments.Append(" " + Tag.GlobalArgument.Target + "\" " + ExtraTarget + " " + Tag.GlobalArgument.Quiet + "\"");
						}
					}

					if (bUsePrecompiled)
					{
						BuildArguments.Append(" " + Tag.GlobalArgument.UsePrecompiled);
					}

					// Always wait for the mutex between UBT invocations, so that building the whole solution doesn't fail.
					BuildArguments.Append(" " + Tag.GlobalArgument.WaitMutex);

					// Always include a flag to format log messages for MSBuild
					BuildArguments.Append(" " + Tag.GlobalArgument.FromMsBuild);

					if (bUseFastPDB)
					{
						// Pass Fast PDB option to make use of Visual Studio's /DEBUG:FASTLINK option
						BuildArguments.Append(" " + Tag.GlobalArgument.FastPDB);
					}

					DirectoryReference BatchFilesDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.BatchFiles);

					if(AdditionalArguments != null)
					{
						BuildArguments.AppendFormat(" {0}", AdditionalArguments);
					}

					VCProjectFileContent.AppendLine("    <NMakeBuildCommandLine>{0} {1}</NMakeBuildCommandLine>", 
						EscapePath(NormalizeProjectPath(FileReference.Combine(BatchFilesDirectory, "Build.bat"))), BuildArguments.ToString());
					VCProjectFileContent.AppendLine("    <NMakeReBuildCommandLine>{0} {1}</NMakeReBuildCommandLine>", 
						EscapePath(NormalizeProjectPath(FileReference.Combine(BatchFilesDirectory, "Rebuild.bat"))), BuildArguments.ToString());
					VCProjectFileContent.AppendLine("    <NMakeCleanCommandLine>{0} {1}</NMakeCleanCommandLine>", 
						EscapePath(NormalizeProjectPath(FileReference.Combine(BatchFilesDirectory, "Clean.bat"))), BuildArguments.ToString());
					VCProjectFileContent.AppendLine("    <NMakeOutput>{0}</NMakeOutput>", NormalizeProjectPath(NMakePath.FullName));

					if (TargetRulesObject.CppStandard >= CppStandardVersion.Latest)
					{
						VCProjectFileContent.AppendLine("    <AdditionalOptions>/std:c++latest</AdditionalOptions>");
					}
					else if (TargetRulesObject.CppStandard >= CppStandardVersion.Cpp17)
					{
						VCProjectFileContent.AppendLine("    <AdditionalOptions>/std:c++17</AdditionalOptions>");
					}
					else if (TargetRulesObject.CppStandard >= CppStandardVersion.Cpp14)
					{
						VCProjectFileContent.AppendLine("    <AdditionalOptions>/std:c++14</AdditionalOptions>");
					}

					if (TargetRulesObject.Type == TargetType.Game || TargetRulesObject.Type == TargetType.Client || TargetRulesObject.Type == TargetType.Server)
					{
						// Allow platforms to add any special properties they require... like aumid override for Xbox One
						PlatformProjectGenerators.GenerateGamePlatformSpecificProperties
						(
                            Platform,
                            Configuration,
                            TargetRulesObject.Type,
                            VCProjectFileContent,
                            RootDirectory,
                            TargetFilePath
						);
					}

					VCProjectFileContent.AppendLine("  </PropertyGroup>");

					if(ProjGenerator != null)
					{
						VCProjectFileContent.Append
						(
							ProjGenerator.GetVisualStudioLayoutDirSection
							(
                                Platform,
                                Configuration,
                                ConditionString,
                                Combination.ProjectTarget.TargetRules.Type,
                                Combination.ProjectTarget.TargetFilePath,
                                ProjectFilePath,
                                NMakePath,
                                ProjectFileFormat
							)
						);
					}
				}

				if (VCUserFileContent != null 
					&& Combination.ProjectTarget != null)
				{
					TargetRules TargetRulesObject = Combination.ProjectTarget.TargetRules;

					if (Combination.Platform.Value.IsInGroup(BuildPlatformGroup.Windows) 
						|| (Combination.Platform == BuildTargetPlatform.HoloLens))
					{
						VCUserFileContent.AppendLine("  <PropertyGroup {0}>", ConditionString);
						if (TargetRulesObject.Type != TargetType.Game)
						{
							string DebugOptions = "";

							if (IsForeignProject)
							{
								DebugOptions += UProjectPath;
								DebugOptions += " " + Tag.GlobalArgument.SkipCompile;
							}
							else if (TargetRulesObject.Type == TargetType.Editor && ProjectName != "MyEngine")
							{
								DebugOptions += ProjectName;
							}

							VCUserFileContent.AppendLine("    <LocalDebuggerCommandArguments>{0}</LocalDebuggerCommandArguments>", DebugOptions);
						}
						VCUserFileContent.AppendLine("    <DebuggerFlavor>{0}</DebuggerFlavor>", Combination.Platform == BuildTargetPlatform.HoloLens ? "AppHostLocalDebugger " : "WindowsLocalDebugger");
						VCUserFileContent.AppendLine("  </PropertyGroup>");
					}

					if(ProjGenerator != null)
					{
						VCUserFileContent.Append(ProjGenerator.GetVisualStudioUserFileStrings(Combination.Platform.Value, Configuration, ConditionString, TargetRulesObject, Combination.ProjectTarget.TargetFilePath, ProjectFilePath));
					}
				}
			}
		}
	}

	// Using at AutomationTool.
	// A Visual C# project.
	class VCSharpProjectFile : MSBuildProjectFile
	{	
		// This is the GUID that Visual Studio uses to identify a C# project file in the solution
		public override string ProjectTypeGUID
		{
			get { return "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"; }
		}

		public HashSet<string> Platforms = new HashSet<string>(); // Platforms that this project supports		
		public HashSet<string> Configurations = new HashSet<string>(); // Configurations that this project supports		

		// Constructs a new project file object
		// <param name="InitFilePath">The path to the project file on disk</param>
		public VCSharpProjectFile(FileReference InitFilePath)
			: base(InitFilePath)
		{
			try
			{
				XmlDocument Document = new XmlDocument();
				Document.Load(InitFilePath.FullName);

				// Check the root element is the right type
				if (Document.DocumentElement.Name != Tag.XML.Element.Project)
				{
					throw new BuildException("Unexpected root element '{0}' in project file", Document.DocumentElement.Name);
				}

				// Parse all the configurations and platforms
				// Parse the basic structure of the document, updating properties and recursing into other referenced projects as we go
				foreach (XmlElement Element in Document.DocumentElement.ChildNodes.OfType<XmlElement>())
				{
					if(Element.Name == Tag.XML.Element.PropertyGroup)
					{
						string Condition = Element.GetAttribute(Tag.XML.Attribute.Condition);
						if(Condition.HasValue())
						{
							Match Match = Regex.Match(Condition, "^\\s*'\\$\\(Configuration\\)\\|\\$\\(Platform\\)'\\s*==\\s*'(.+)\\|(.+)'\\s*$");
							if(Match.Success && Match.Groups.Count == 3)
							{
								Configurations.Add(Match.Groups[1].Value);
								Platforms.Add(Match.Groups[2].Value);
							}
							else
							{
								Log.TraceWarning("Unable to parse configuration/platform from condition '{0}': {1}", InitFilePath, Condition);
							}
						}
					}
				}
			}
			catch(Exception Ex)
			{
				Log.TraceWarning("Unable to parse {0}: {1}", InitFilePath, Ex.ToString());
			}
		}

		// Extract information from the csproj file based on the supplied configuration
		public CsProjectInfo GetProjectInfo(TargetConfiguration InConfiguration)
		{
			if (CachedProjectInfo.ContainsKey(InConfiguration))
			{
				return CachedProjectInfo[InConfiguration];
			}

            Dictionary<string, string> Properties = new Dictionary<string, string>
            {
                { "Platform", "AnyCPU" },
                { "Configuration", InConfiguration.ToString() }
            };

            if (CsProjectInfo.TryRead(ProjectFilePath, Properties, out CsProjectInfo Info))
			{
				CachedProjectInfo.Add(InConfiguration, Info);
			}

			return Info;
		}

		// Determine if this project is a .NET Core project
		public bool IsDotNETCoreProject()
		{
			CsProjectInfo Info = GetProjectInfo(TargetConfiguration.Debug);
			return Info.IsDotNETCoreProject();
		}

		// Get the project context for the given solution context
		public override MSBuildProjectContext GetMatchingProjectContext
		(
            TargetType SolutionTarget,
            TargetConfiguration SolutionConfiguration,
            BuildTargetPlatform SolutionPlatform,
            PlatformProjectGeneratorCollection PlatformProjectGenerators
		)
		{
			// Find the matching platform name
			string ProjectPlatformName;
			if(SolutionPlatform == BuildTargetPlatform.Win32 && Platforms.Contains("x86"))
			{
				ProjectPlatformName = "x86";
			}
			else if(Platforms.Contains("x64"))
			{
				ProjectPlatformName = "x64";
			}
			else
			{
				ProjectPlatformName = "Any CPU";
			}

			// Find the matching configuration
			string ProjectConfigurationName;
			if(Configurations.Contains(SolutionConfiguration.ToString()))
			{
				ProjectConfigurationName = SolutionConfiguration.ToString();
			}
			else if(Configurations.Contains("Development"))
			{
				ProjectConfigurationName = "Development";
			}
			else
			{
				ProjectConfigurationName = "Release";
			}

			// Figure out whether to build it by default
			bool bBuildByDefault = ShouldBuildByDefaultForSolutionTargets;
			if(SolutionTarget == TargetType.Game || SolutionTarget == TargetType.Editor)
			{
				bBuildByDefault = true;
			}

			// Create the context
			return new MSBuildProjectContext(ProjectConfigurationName, ProjectPlatformName){ bBuildByDefault = bBuildByDefault };
		}

		// Basic csproj file support. Generates C# library project with one build config.
		public override bool WriteProjectFile(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			throw new BuildException("Support for writing C# projects from BuildTool has been removed.");
		}

		// Cache of parsed info about this project
		protected readonly Dictionary<TargetConfiguration, CsProjectInfo> CachedProjectInfo = new Dictionary<TargetConfiguration, CsProjectInfo>();
	}
#pragma warning restore IDE0079 // Remove unnecessary suppression
}
