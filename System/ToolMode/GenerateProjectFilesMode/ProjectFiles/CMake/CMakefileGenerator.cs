using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using BuildToolUtilities;

namespace BuildTool
{
	// Represents a folder within the master project (e.g. Visual Studio solution)
	class CMakefileFolder : MasterProjectFolder
	{
		public CMakefileFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
			: base(InitOwnerProjectFileGenerator, InitFolderName)
		{
		}
	}

	class CMakefileProjectFile : ProjectFile
	{
		public CMakefileProjectFile(FileReference InitFilePath)
			: base(InitFilePath)
		{
		}
	}
	
	// CMakefile project file generator implementation
	class CMakefileGenerator : ProjectFileGenerator
	{
		public CMakefileGenerator(FileReference InOnlyGameProject)
			: base(InOnlyGameProject)
		{
		}

		// Determines whether or not we should generate code completion data whenever possible.
		public override bool GetbGenerateIntelliSenseData() => true;

		// This determines if engine files are included in the source lists.
		public bool IsProjectBuild => !string.IsNullOrEmpty(GameProjectName);

		// The file extension for this project file.
		public override string ProjectFileExtension => ".txt";
		public string ProjectFileName => "CMakeLists" + ProjectFileExtension;
		public string CMakeExtension => ".cmake";
		public string CMakeIncludesFileName => "cmake-includes" + CMakeExtension;
		public string CMakeEngineConfigsFileName => "cmake-config-engine" + CMakeExtension;
		public string CMakeProjectConfigsFileName => "cmake-config-project" + CMakeExtension;

		// The CMake file used to store the additional build configuration files (CSharp) for the engine.
		public string CMakeEngineCSFileName => "cmake-csharp-engine" + CMakeExtension;

		// The CMake file used to store the additional configuration files (CSharp) for the project.
		public string CMakeProjectCSFileName => "cmake-csharp-project" + CMakeExtension;

		// The CMake file used to store the additional shader files (usf/ush) for the engine.
		public string CMakeEngineShadersFileName => "cmake-shaders-engine" + CMakeExtension;

		// The CMake file used to store the additional shader files (usf/ush) for the project.
		public string CMakeProjectShadersFileName => "cmake-shaders-project" + CMakeExtension;

		// The CMake file used to store the list of engine headers.
		public string CMakeEngineHeadersFileName => "cmake-headers" + CMakeExtension;
		
		// The CMake file used to store the list of engine headers.
		public string CMakeProjectHeadersFileName => "cmake-headers-project" + CMakeExtension;

		// The CMake file used to store the list of sources for the engine.
		public string CMakeEngineSourcesFileName => "cmake-sources-engine" + CMakeExtension;
		
		// The CMake file used to store the list of sources for the project.
		public string CMakeProjectSourcesFileName => "cmake-sources-project" + CMakeExtension;

		// The CMake file used to store the list of definitions for the project.
		public string CMakeDefinitionsFileName => "cmake-definitions" + CMakeExtension;

		// Writes the master project file (e.g. Visual Studio Solution file)
		protected override bool WriteMasterProjectFile(ProjectFile BuildToolProject, PlatformProjectGeneratorCollection RegisteredPlatformProjectGenerators) 
			=> true;

		private void AppendCleanedPathToList(StringBuilder EngineFiles, StringBuilder ProjectFiles, String SourceFileRelativeToRoot, String FullName, String GameProjectPath, String InEngineRootPath, String GameRootPath)
		{
			if (!SourceFileRelativeToRoot.StartsWith("..") && !Path.IsPathRooted(SourceFileRelativeToRoot))
			{
				EngineFiles.Append("\t\"" + InEngineRootPath + "/Engine/" + StringUtils.CleanDirectorySeparators(SourceFileRelativeToRoot, '/') + "\"\n");
			}
			else
			{
				if (String.IsNullOrEmpty(GameProjectName))
				{
					EngineFiles.Append("\t\"" + StringUtils.CleanDirectorySeparators(SourceFileRelativeToRoot, '/').Substring(3) + "\"\n");
				}
				else
				{
					string RelativeGameSourcePath = Utils.MakePathRelativeTo(FullName, GameProjectPath);
					ProjectFiles.Append("\t\"" + GameRootPath + "/" + StringUtils.CleanDirectorySeparators(RelativeGameSourcePath, '/') + "\"\n");
				}
			}
		}

		private bool WriteCMakeLists()
        {
            string EngineRootPath = StringUtils.CleanDirectorySeparators(BuildTool.RootDirectory.FullName, '/');

            string CMakeGameRootPath    = "";
			string GameProjectPath      = "";
			string CMakeGameProjectFile = "";

			string HostArchitecture;
			string SetCompiler = "";

			if (IsProjectBuild)
			{
				GameProjectPath      = OnlyGameProject.Directory.FullName;
				CMakeGameRootPath    = StringUtils.CleanDirectorySeparators(OnlyGameProject.Directory.FullName, '/');
                CMakeGameProjectFile = StringUtils.CleanDirectorySeparators(OnlyGameProject.FullName, '/');
			}

			// Additional CMake file definitions
			string EngineHeadersFilePath  = FileReference.Combine(IntermediateProjectFilesPath, CMakeEngineHeadersFileName).ToNormalizedPath();
			string ProjectHeadersFilePath = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectHeadersFileName).ToNormalizedPath();
			string EngineSourcesFilePath  = FileReference.Combine(IntermediateProjectFilesPath, CMakeEngineSourcesFileName).ToNormalizedPath();
			string ProjectSourcesFilePath = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectSourcesFileName).ToNormalizedPath();
			// string ProjectFilePath = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectSourcesFileName).ToNormalizedPath();
			string IncludeFilePath        = FileReference.Combine(IntermediateProjectFilesPath, CMakeIncludesFileName).ToNormalizedPath();
			string EngineConfigsFilePath  = FileReference.Combine(IntermediateProjectFilesPath, CMakeEngineConfigsFileName).ToNormalizedPath();
			string ProjectConfigsFilePath = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectConfigsFileName).ToNormalizedPath();
			string EngineCSFilePath       = FileReference.Combine(IntermediateProjectFilesPath, CMakeEngineCSFileName).ToNormalizedPath();
			string ProjectCSFilePath      = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectCSFileName).ToNormalizedPath();
			string EngineShadersFilePath  = FileReference.Combine(IntermediateProjectFilesPath, CMakeEngineShadersFileName).ToNormalizedPath();
			string ProjectShadersFilePath = FileReference.Combine(IntermediateProjectFilesPath, CMakeProjectShadersFileName).ToNormalizedPath();
			string DefinitionsFilePath    = FileReference.Combine(IntermediateProjectFilesPath, CMakeDefinitionsFileName).ToNormalizedPath();

			StringBuilder CMakefileContent = new StringBuilder();

			CMakefileContent.Append(
				"# Makefile generated by CMakefileGenerator.cs (v1.2)\n" +
				"# *DO NOT EDIT*\n\n" +
				"cmake_minimum_required (VERSION 2.6)\n" +
				"project (MyEngine)\n\n" + 			
				"# CMake Flags\n" +
				"set(CMAKE_CXX_STANDARD 14)\n" + // Need to keep this updated
				"set(CMAKE_CXX_USE_RESPONSE_FILE_FOR_OBJECTS 1 CACHE BOOL \"\" FORCE)\n" +
				"set(CMAKE_CXX_USE_RESPONSE_FILE_FOR_INCLUDES 1 CACHE BOOL \"\" FORCE)\n\n" +
				SetCompiler +
				"# Standard Includes\n" +
				"include(\"" + IncludeFilePath + "\")\n" +
				"include(\"" + DefinitionsFilePath + "\")\n" +
				"include(\"" + EngineHeadersFilePath + "\")\n" +
				"include(\"" + ProjectHeadersFilePath + "\")\n" +
				"include(\"" + EngineSourcesFilePath + "\")\n" +
				"include(\"" + ProjectSourcesFilePath + "\")\n" +
				"include(\"" + EngineCSFilePath + "\")\n" +
				"include(\"" + ProjectCSFilePath + "\")\n\n"
			);

			List<string> IncludeDirectories = new List<string>();
			List<string> PreprocessorDefinitions = new List<string>();

			foreach (ProjectFile CurProject in GeneratedProjectFiles)
			{
				foreach (string IncludeSearchPath in CurProject.IntelliSenseIncludeSearchPaths)
				{
					string IncludeDirectory = GetIncludeDirectory(IncludeSearchPath, Path.GetDirectoryName(CurProject.ProjectFilePath.FullName));
					if (IncludeDirectory != null && !IncludeDirectories.Contains(IncludeDirectory))
					{
						if (IncludeDirectory.Contains(BuildTool.RootDirectory.FullName))
						{
							IncludeDirectories.Add(IncludeDirectory.Replace(BuildTool.RootDirectory.FullName, EngineRootPath));
						}
						else
						{
							// If the path isn't rooted, then it is relative to the game root
							if (!Path.IsPathRooted(IncludeDirectory))
							{
								IncludeDirectories.Add(CMakeGameRootPath + "/" + IncludeDirectory);
							}
							else
							{
								// This is a rooted path like /usr/local/sometool/include
								IncludeDirectories.Add(IncludeDirectory);
							}
						}
					}
				}

				Debugger.Break();

				foreach (string PreProcessorDefinition in CurProject.IntelliSensePreprocessorDefinitions)
				{
					string Definition         = PreProcessorDefinition.Replace("TEXT(\"", "").Replace("\")", "").Replace("()=", "=");
					string AlternateDefinition = Definition.Contains("=0") ? Definition.Replace("=0", "=1") : Definition.Replace("=1", "=0");

					if (Definition.Equals("WITH_EDITORONLY_DATA=0"))
					{
						Definition = AlternateDefinition;
					}

					if (!PreprocessorDefinitions.Contains(Definition) &&
						!PreprocessorDefinitions.Contains(AlternateDefinition) &&
						!Definition.StartsWith("UE_ENGINE_DIRECTORY") &&
						!Definition.StartsWith("ORIGINAL_FILE_NAME"))
					{
						PreprocessorDefinitions.Add(Definition);
					}
				}
			}

			// Create Engine/Project specific lists
			StringBuilder CMakeEngineSourceFilesList  = new StringBuilder("set(ENGINE_SOURCE_FILES \n");
			StringBuilder CMakeProjectSourceFilesList = new StringBuilder("set(PROJECT_SOURCE_FILES \n");
			StringBuilder CMakeEngineHeaderFilesList  = new StringBuilder("set(ENGINE_HEADER_FILES \n");
			StringBuilder CMakeProjectHeaderFilesList = new StringBuilder("set(PROJECT_HEADER_FILES \n");
			StringBuilder CMakeEngineCSFilesList      = new StringBuilder("set(ENGINE_CSHARP_FILES \n");
			StringBuilder CMakeProjectCSFilesList     = new StringBuilder("set(PROJECT_CSHARP_FILES \n");
			StringBuilder CMakeEngineConfigFilesList  = new StringBuilder("set(ENGINE_CONFIG_FILES \n");
			StringBuilder CMakeProjectConfigFilesList = new StringBuilder("set(PROJECT_CONFIG_FILES \n");
			StringBuilder CMakeEngineShaderFilesList  = new StringBuilder("set(ENGINE_SHADER_FILES \n");
			StringBuilder CMakeProjectShaderFilesList = new StringBuilder("set(PROJECT_SHADER_FILES \n");

			StringBuilder IncludeDirectoriesList      = new StringBuilder("include_directories( \n");
			StringBuilder PreprocessorDefinitionsList = new StringBuilder("add_definitions( \n");

			// Create SourceFiles, HeaderFiles, and ConfigFiles sections.
			List<FileReference> AllModuleFiles = DiscoverModules(FindGameProjects());
			foreach (FileReference CurModuleFile in AllModuleFiles)
			{
				List<FileReference> FoundFiles = SourceFileSearch.FindModuleSourceFiles(CurModuleFile);
				foreach (FileReference CurSourceFile in FoundFiles)
				{
					string SourceFileRelativeToRoot = CurSourceFile.MakeRelativeTo(BuildTool.EngineDirectory);

					// Exclude files/folders on a per-platform basis.
					if (!IsPathExcludedOnPlatform(SourceFileRelativeToRoot))
					{
						if (SourceFileRelativeToRoot.EndsWith(".cpp"))
						{							
							AppendCleanedPathToList(CMakeEngineSourceFilesList, CMakeProjectSourceFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
						}
						else if (SourceFileRelativeToRoot.EndsWith(".h"))
						{
							AppendCleanedPathToList(CMakeEngineHeaderFilesList, CMakeProjectHeaderFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
						}
						else if (SourceFileRelativeToRoot.EndsWith(".cs"))
						{
							AppendCleanedPathToList(CMakeEngineCSFilesList, CMakeProjectCSFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
						}
						else if (SourceFileRelativeToRoot.EndsWith(".usf") || SourceFileRelativeToRoot.EndsWith(".ush"))
						{
							AppendCleanedPathToList(CMakeEngineShaderFilesList, CMakeProjectShaderFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
						}
						else if (SourceFileRelativeToRoot.EndsWith(".ini"))
						{
							AppendCleanedPathToList(CMakeEngineConfigFilesList, CMakeProjectConfigFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
						}
					}
				}
			}

			foreach (string IncludeDirectory in IncludeDirectories)
			{
				IncludeDirectoriesList.Append("\t\"" + StringUtils.CleanDirectorySeparators(IncludeDirectory, '/') + "\"\n");
			}

			foreach (string PreprocessorDefinition in PreprocessorDefinitions)
			{
				int EqPos = PreprocessorDefinition.IndexOf("=");
				if (EqPos >= 0)
				{
					string Key = PreprocessorDefinition.Substring(0, EqPos);
					string Value = PreprocessorDefinition.Substring(EqPos).Replace("\"", "\\\"");
					PreprocessorDefinitionsList.Append("\t\"-D" + Key + Value + "\"\n");
				}
				else
				{
					PreprocessorDefinitionsList.Append("\t\"-D" + PreprocessorDefinition + "\"\n");
				}
			}

			// Add Engine/Shaders files (game are added via modules)
			List<FileReference> EngineShaderFiles = SourceFileSearch.FindFiles(DirectoryReference.Combine(BuildTool.EngineDirectory, "Shaders"));
			foreach (FileReference CurSourceFile in EngineShaderFiles)
			{
				string SourceFileRelativeToRoot = CurSourceFile.MakeRelativeTo(BuildTool.EngineDirectory);
				if (SourceFileRelativeToRoot.EndsWith(".usf") || SourceFileRelativeToRoot.EndsWith(".ush"))
				{
					AppendCleanedPathToList(CMakeEngineShaderFilesList, CMakeProjectShaderFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
				}
			}

			// Add Engine/Config ini files (game are added via modules)
			List<FileReference> EngineConfigFiles = SourceFileSearch.FindFiles(DirectoryReference.Combine(BuildTool.EngineDirectory, "Config"));
			foreach (FileReference CurSourceFile in EngineConfigFiles)
			{
				string SourceFileRelativeToRoot = CurSourceFile.MakeRelativeTo(BuildTool.EngineDirectory);
				if (SourceFileRelativeToRoot.EndsWith(".ini"))
				{
					AppendCleanedPathToList(CMakeEngineConfigFilesList, CMakeProjectConfigFilesList, SourceFileRelativeToRoot, CurSourceFile.FullName, GameProjectPath, EngineRootPath, CMakeGameRootPath);
				}
			}

			const string CMakeSectionEnd = " )\n\n";

			// Add section end to section strings;
			CMakeEngineSourceFilesList.Append(CMakeSectionEnd);
			CMakeEngineHeaderFilesList.Append(CMakeSectionEnd);
			CMakeEngineCSFilesList    .Append(CMakeSectionEnd);
			CMakeEngineConfigFilesList.Append(CMakeSectionEnd);
			CMakeEngineShaderFilesList.Append(CMakeSectionEnd);

			CMakeProjectSourceFilesList.Append(CMakeSectionEnd);
			CMakeProjectHeaderFilesList.Append(CMakeSectionEnd);
			CMakeProjectCSFilesList    .Append(CMakeSectionEnd);
			CMakeProjectConfigFilesList.Append(CMakeSectionEnd);
			CMakeProjectShaderFilesList.Append(CMakeSectionEnd);
			
			IncludeDirectoriesList.Append(CMakeSectionEnd);
			PreprocessorDefinitionsList.Append(CMakeSectionEnd);

			if (bIncludeShaderSourceInProject)
			{	
				CMakefileContent.Append("# Optional Shader Include\n");
				if (!IsProjectBuild || bIncludeEngineSourceInSolution)
				{
					CMakefileContent.Append("include(\"" + EngineShadersFilePath + "\")\n");
					CMakefileContent.Append("set_source_files_properties(${ENGINE_SHADER_FILES} PROPERTIES HEADER_FILE_ONLY TRUE)\n");	
				}
				CMakefileContent.Append("include(\"" + ProjectShadersFilePath + "\")\n");
                CMakefileContent.Append("set_source_files_properties(${PROJECT_SHADER_FILES} PROPERTIES HEADER_FILE_ONLY TRUE)\n");
                CMakefileContent.Append("source_group(\"Shader Files\" REGULAR_EXPRESSION .*.usf)\n\n");
			}

			if (bIncludeConfigFiles)
			{
				CMakefileContent.Append("# Optional Config Include\n");
				if (!IsProjectBuild || bIncludeEngineSourceInSolution)
				{
					CMakefileContent.Append("include(\"" + EngineConfigsFilePath + "\")\n");
					CMakefileContent.Append("set_source_files_properties(${ENGINE_CONFIG_FILES} PROPERTIES HEADER_FILE_ONLY TRUE)\n");
				}
				CMakefileContent.Append("include(\"" + ProjectConfigsFilePath + "\")\n");
				CMakefileContent.Append("set_source_files_properties(${PROJECT_CONFIG_FILES} PROPERTIES HEADER_FILE_ONLY TRUE)\n");
				CMakefileContent.Append("source_group(\"Config Files\" REGULAR_EXPRESSION .*.ini)\n\n");
			}

			string CMakeProjectCmdArg = "";
            string AddArguements = "";

            if (bGeneratingGameProjectFiles)
			{
                AddArguements += " -game";
            }
			// Should the builder output progress ticks
			if (ProgressWriter.bWriteMarkup)
			{
				AddArguements += " -progress";	
			}

			string BuildCommand;

			// Build커맨드로 Build.bat 실행.
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				HostArchitecture = "Win64";
				BuildCommand = "call \"" + EngineRootPath + "/Engine/Build/BatchFiles/Build.bat\"";
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				HostArchitecture = "Mac";
				BuildCommand = "cd \"" + EngineRootPath + "\" && bash \"" + EngineRootPath + "/Engine/Build/BatchFiles/" + HostArchitecture + "/Build.sh\"";
				bIncludeIOSTargets = true;
				bIncludeTVOSTargets = true;
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
			{
				HostArchitecture = "Linux";
				BuildCommand = "cd \"" + EngineRootPath + "\" && bash \"" + EngineRootPath + "/Engine/Build/BatchFiles/" + HostArchitecture + "/Build.sh\"";

				string CompilerPath = LinuxCommon.WhichClang();
				if (CompilerPath == null)
				{
					/*CompilerPath = */LinuxCommon.WhichGcc();
				}

				// SetCompiler = "set(CMAKE_CXX_COMPILER " + CompilerPath + ")\n\n";
			}
			else
			{
				Debugger.Break();
				throw new BuildException("ERROR: CMakefileGenerator does not support this platform");
			}

			foreach (ProjectFile Project in GeneratedProjectFiles)
			{
				foreach (ProjectTarget TargetFile in Project.ProjectTargets)
				{
					if (TargetFile.TargetFilePath == null)
					{
						continue;
					}

					string TargetName = TargetFile.TargetFilePath.GetFileNameWithoutAnyExtensions();       // Remove both ".cs" and ".

					foreach (TargetConfiguration CurConfiguration in Enum.GetValues(typeof(TargetConfiguration)))
					{
						if (CurConfiguration != TargetConfiguration.Unknown && 
							CurConfiguration != TargetConfiguration.Development)
						{
							if (InstalledPlatformInfo.IsValidConfiguration(CurConfiguration, EProjectType.Code) && 
								!IsTargetExcluded(TargetName, BuildHostPlatform.Current.Platform, CurConfiguration))
							{
								if (TargetName == GameProjectName || 
									TargetName == (GameProjectName + "Editor"))
								{
									CMakeProjectCmdArg = "\"-project="+ CMakeGameProjectFile + "\"";
								}

								string ConfName = Enum.GetName(typeof(TargetConfiguration), CurConfiguration);
								CMakefileContent.Append(String.Format("add_custom_target({0}-{3}-{1} {5} {0} {3} {1} {2}{4} -buildscw VERBATIM)\n", TargetName, ConfName, CMakeProjectCmdArg, HostArchitecture, AddArguements, BuildCommand));

								// Add iOS and TVOS targets if valid
								if (bIncludeIOSTargets && !IsTargetExcluded(TargetName, BuildTargetPlatform.IOS, CurConfiguration))
								{
    								CMakefileContent.Append(String.Format("add_custom_target({0}-{3}-{1} {5} {0} {3} {1} {2}{4} VERBATIM)\n", TargetName, ConfName, CMakeProjectCmdArg, BuildTargetPlatform.IOS, AddArguements, BuildCommand));
								}
								if (bIncludeTVOSTargets && !IsTargetExcluded(TargetName, BuildTargetPlatform.TVOS, CurConfiguration))
								{
    								CMakefileContent.Append(String.Format("add_custom_target({0}-{3}-{1} {5} {0} {3} {1} {2}{4} VERBATIM)\n", TargetName, ConfName, CMakeProjectCmdArg, BuildTargetPlatform.TVOS, AddArguements, BuildCommand));
								}
							}
						}
					}
                    if (!IsTargetExcluded(TargetName, BuildHostPlatform.Current.Platform, TargetConfiguration.Development))
                    {
                        if (TargetName == GameProjectName || 
							TargetName == (GameProjectName + "Editor"))
                        {
                            CMakeProjectCmdArg = "\"-project=" + CMakeGameProjectFile + "\"";
                        }

                        CMakefileContent.Append(String.Format("add_custom_target({0} {4} {0} {2} Development {1}{3} -buildscw VERBATIM)\n\n", TargetName, CMakeProjectCmdArg, HostArchitecture, AddArguements, BuildCommand));

                        // Add iOS and TVOS targets if valid
                        if (bIncludeIOSTargets && !IsTargetExcluded(TargetName, BuildTargetPlatform.IOS, TargetConfiguration.Development))
                        {
                           CMakefileContent.Append(String.Format("add_custom_target({0}-{3} {5} {0} {3} {1} {2}{4} VERBATIM)\n", TargetName, TargetConfiguration.Development, CMakeProjectCmdArg, BuildTargetPlatform.IOS, AddArguements, BuildCommand));
                        }
                        if (bIncludeTVOSTargets && !IsTargetExcluded(TargetName, BuildTargetPlatform.TVOS, TargetConfiguration.Development))
                        {
                            CMakefileContent.Append(String.Format("add_custom_target({0}-{3} {5} {0} {3} {1} {2}{4} VERBATIM)\n", TargetName, TargetConfiguration.Development, CMakeProjectCmdArg, BuildTargetPlatform.TVOS, AddArguements, BuildCommand));
                        }
                   }
				}		
			}

			// Create Build Template
			if (IsProjectBuild && !bIncludeEngineSourceInSolution)
			{
				CMakefileContent.AppendLine("add_executable(FakeTarget ${PROJECT_HEADER_FILES} ${PROJECT_SOURCE_FILES} ${PROJECT_CSHARP_FILES} ${PROJECT_SHADER_FILES} ${PROJECT_CONFIG_FILES})");
			}
			else
			{
				CMakefileContent.AppendLine("add_executable(FakeTarget ${ENGINE_HEADER_FILES} ${ENGINE_SOURCE_FILES} ${ENGINE_CSHARP_FILES} ${ENGINE_SHADER_FILES} ${ENGINE_CONFIG_FILES} ${PROJECT_HEADER_FILES} ${PROJECT_SOURCE_FILES} ${PROJECT_CSHARP_FILES} ${PROJECT_SHADER_FILES} ${PROJECT_CONFIG_FILES})");
			}

			string FullFileName = Path.Combine(MasterProjectPath.FullName, ProjectFileName);

			// Write out CMake files
			bool bWriteMakeList       = WriteFileIfChanged(FullFileName,           CMakefileContent.ToString());
			bool bWriteEngineHeaders  = WriteFileIfChanged(EngineHeadersFilePath,  CMakeEngineHeaderFilesList.ToString());
			bool bWriteProjectHeaders = WriteFileIfChanged(ProjectHeadersFilePath, CMakeProjectHeaderFilesList.ToString());
			bool bWriteEngineSources  = WriteFileIfChanged(EngineSourcesFilePath,  CMakeEngineSourceFilesList.ToString());
			bool bWriteProjectSources = WriteFileIfChanged(ProjectSourcesFilePath, CMakeProjectSourceFilesList.ToString());
			bool bWriteIncludes       = WriteFileIfChanged(IncludeFilePath,        IncludeDirectoriesList.ToString());
			bool bWriteDefinitions    = WriteFileIfChanged(DefinitionsFilePath,    PreprocessorDefinitionsList.ToString());
			bool bWriteEngineConfigs  = WriteFileIfChanged(EngineConfigsFilePath,  CMakeEngineConfigFilesList.ToString());
			bool bWriteProjectConfigs = WriteFileIfChanged(ProjectConfigsFilePath, CMakeProjectConfigFilesList.ToString());
			bool bWriteEngineShaders  = WriteFileIfChanged(EngineShadersFilePath,  CMakeEngineShaderFilesList.ToString());
			bool bWriteProjectShaders = WriteFileIfChanged(ProjectShadersFilePath, CMakeProjectShaderFilesList.ToString());
			bool bWriteEngineCS       = WriteFileIfChanged(EngineCSFilePath,       CMakeEngineCSFilesList.ToString());
			bool bWriteProjectCS      = WriteFileIfChanged(ProjectCSFilePath,      CMakeProjectCSFilesList.ToString());			

			// Return success flag if all files were written out successfully
			return  bWriteMakeList       &&
				    bWriteEngineHeaders  && 
					bWriteProjectHeaders &&
					bWriteEngineSources  && 
					bWriteProjectSources &&
					bWriteEngineConfigs  && 
					bWriteProjectConfigs &&
					bWriteEngineCS       && 
					bWriteProjectCS      &&
					bWriteEngineShaders  && 
					bWriteProjectShaders &&
					bWriteIncludes       && 
					bWriteDefinitions;
		}

		private static bool IsPathExcludedOnPlatform(string SourceFileRelativeToRoot)
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
			{
				return IsPathExcludedOnLinux(SourceFileRelativeToRoot);
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				return IsPathExcludedOnMac(SourceFileRelativeToRoot);
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				return IsPathExcludedOnWindows(SourceFileRelativeToRoot);
			}
			else
			{
				return false;
			}
		}

		private static bool IsPathExcludedOnLinux(string SourceFileRelativeToRoot)
		{
			// minimal filtering as it is helpful to be able to look up symbols from other platforms
			return SourceFileRelativeToRoot.Contains("Source/ThirdParty/");
		}

		private static bool IsPathExcludedOnMac(string SourceFileRelativeToRoot)
		{
			return SourceFileRelativeToRoot.Contains("Source/ThirdParty/")             ||
				   SourceFileRelativeToRoot.Contains("/Windows/")                      ||
				   SourceFileRelativeToRoot.Contains("/Linux/")                        ||
				   SourceFileRelativeToRoot.Contains("/VisualStudioSourceCodeAccess/") ||
				   SourceFileRelativeToRoot.Contains("/WmfMedia/")                     ||
				   SourceFileRelativeToRoot.Contains("/WindowsDeviceProfileSelector/") ||
				   SourceFileRelativeToRoot.Contains("/WindowsMoviePlayer/")           ||
				   SourceFileRelativeToRoot.Contains("/WinRT/");
		}

		private static bool IsPathExcludedOnWindows(string SourceFileRelativeToRoot)
		{
			// minimal filtering as it is helpful to be able to look up symbols from other platforms
			return SourceFileRelativeToRoot.Contains("Source\\ThirdParty\\");
		}

		private bool IsTargetExcluded(string TargetName, BuildTargetPlatform TargetPlatform, TargetConfiguration TargetConfig)
		{
			if (TargetPlatform == BuildTargetPlatform.IOS || 
				TargetPlatform == BuildTargetPlatform.TVOS)
			{
				if ((TargetName.StartsWith("DefaultGame")                          || 
					(TargetName.StartsWith(GameProjectName) && IsProjectBuild) || 
					 TargetName.StartsWith("QAGame")) && !TargetName.StartsWith("QAGameEditor"))
				{
				    return false;
				}
				return true;
            }

			// Only do this level of filtering if we are trying to speed things up tremendously
			if (bCmakeMinimalTargets)
			{
				// Editor or game builds get all target configs
				// The game project editor or game get all configs
				if ((TargetName.StartsWith("Editor") && !TargetName.StartsWith("EditorServices")) ||
					TargetName.StartsWith("DefaultGame")                                                    ||
					(TargetName.StartsWith(GameProjectName) && IsProjectBuild))
				{
					return false;
				}
				// SCW & CRC are minimally included as just development builds
				else if (TargetConfig == TargetConfiguration.Development &&
					    (TargetName.StartsWith("ShaderCompileWorker") || TargetName.StartsWith("CrashReportClient")))
				{
					return false;
				}
				else if ((TargetName.StartsWith("QAGameEditor") && !TargetName.StartsWith("QAGameEditorServices")) || 
					      TargetName.StartsWith("QAGame"))
				{
				    return false;
				}
				return true;
			}
			return false;
		}

		// Adds the include directory to the list, after converting it to relative to Engine dir root
		private static string GetIncludeDirectory(string IncludeDir, string ProjectDir)
		{
			string FullProjectPath = Path.GetFullPath(MasterProjectPath.FullName);
			string FullPath;

			// Check for paths outside of both the engine and the project
			if (Path.IsPathRooted(IncludeDir)           &&
				!IncludeDir.StartsWith(FullProjectPath) &&
				!IncludeDir.StartsWith(BuildTool.RootDirectory.FullName))
			{
				// Full path to a folder outside of project
				FullPath = IncludeDir;
			}
			else
			{
				FullPath = Path.GetFullPath(Path.Combine(ProjectDir, IncludeDir));
				if (!FullPath.StartsWith(BuildTool.RootDirectory.FullName))
				{
					FullPath = Utils.MakePathRelativeTo(FullPath, FullProjectPath);
				}
				FullPath = FullPath.TrimEnd('/');
			}
			return FullPath;
		}

		#region ProjectFileGenerator implementation

		protected override bool WriteProjectFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			return WriteCMakeLists();
		}

		public override MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
		{
			return new CMakefileFolder(InitOwnerProjectFileGenerator, InitFolderName);
		}

		
		// This will filter out numerous targets to speed up cmake processing
		
		protected bool bCmakeMinimalTargets = false;

		
		// Whether to include iOS targets or not
		
		protected bool bIncludeIOSTargets = false;

		
		// Whether to include TVOS targets or not
		
		protected bool bIncludeTVOSTargets = false;

		protected override void ConfigureProjectFileGeneration(String[] Arguments, ref bool IncludeAllPlatforms)
		{
			base.ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);
			// Check for minimal build targets to speed up cmake processing
			foreach (string CurArgument in Arguments)
			{
				switch (CurArgument.ToUpperInvariant())
				{
					case "-CMAKEMINIMALTARGETS":
						// To speed things up
						bIncludeDocumentation = false;
						bIncludeShaderSourceInProject = true;
						bIncludeTemplateFiles = false;
						bIncludeConfigFiles = true;
						// We want to filter out sets of targets to speed up builds via cmake
						bCmakeMinimalTargets = true;
						break;
				}
			}
		}

		
		// Allocates a generator-specific project file object
		
		// <param name="InitFilePath">Path to the project file</param>
		// <returns>The newly allocated project file object</returns>
		protected override ProjectFile AllocateProjectFile(FileReference InitFilePath)
		{
			return new CMakefileProjectFile(InitFilePath);
		}

		public override void CleanProjectFiles(DirectoryReference InMasterProjectDirectory, string InMasterProjectName, DirectoryReference InIntermediateProjectFilesDirectory)
		{
			// Remove Project File
			FileReference MasterProjectFile = FileReference.Combine(InMasterProjectDirectory, ProjectFileName);
			if (FileReference.Exists(MasterProjectFile))
			{
				FileReference.Delete(MasterProjectFile);
			}

			// Remove Headers Files
			FileReference EngineHeadersFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeEngineHeadersFileName);
			if (FileReference.Exists(EngineHeadersFile))
			{
				FileReference.Delete(EngineHeadersFile);
			}
			FileReference ProjectHeadersFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeProjectHeadersFileName);
			if (FileReference.Exists(ProjectHeadersFile))
			{
				FileReference.Delete(ProjectHeadersFile);
			}

			// Remove Sources Files
			FileReference EngineSourcesFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeEngineSourcesFileName);
			if (FileReference.Exists(EngineSourcesFile))
			{
				FileReference.Delete(EngineSourcesFile);
			}
			FileReference ProjectSourcesFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeProjectSourcesFileName);
			if (FileReference.Exists(ProjectSourcesFile))
			{
				FileReference.Delete(ProjectSourcesFile);
			}

			// Remove Includes File
			FileReference IncludeFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeIncludesFileName);
			if (FileReference.Exists(IncludeFile))
			{
				FileReference.Delete(IncludeFile);
			}
			
			// Remove CSharp Files
			FileReference EngineCSFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeEngineCSFileName);
			if (FileReference.Exists(EngineCSFile))
			{
				FileReference.Delete(EngineCSFile);
			}
			FileReference ProjectCSFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeProjectCSFileName);
			if (FileReference.Exists(ProjectCSFile))
			{
				FileReference.Delete(ProjectCSFile);
			}

			// Remove Config Files
			FileReference EngineConfigFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeEngineConfigsFileName);
			if (FileReference.Exists(EngineConfigFile))
			{
				FileReference.Delete(EngineConfigFile);
			}
			FileReference ProjectConfigsFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeProjectConfigsFileName);
			if (FileReference.Exists(ProjectConfigsFile))
			{
				FileReference.Delete(ProjectConfigsFile);
			}

			// Remove Config Files
			FileReference EngineShadersFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeEngineShadersFileName);
			if (FileReference.Exists(EngineShadersFile))
			{
				FileReference.Delete(EngineShadersFile);
			}
			FileReference ProjectShadersFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeProjectShadersFileName);
			if (FileReference.Exists(ProjectShadersFile))
			{
				FileReference.Delete(ProjectShadersFile);
			}

			// Remove Definitions File
			FileReference DefinitionsFile = FileReference.Combine(InIntermediateProjectFilesDirectory, CMakeDefinitionsFileName);
			if (FileReference.Exists(DefinitionsFile))
			{
				FileReference.Delete(DefinitionsFile);
			}
		}

		#endregion ProjectFileGenerator implementation
	}
}
