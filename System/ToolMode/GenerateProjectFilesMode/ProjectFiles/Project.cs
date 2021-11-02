using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// A single target within a project.  A project may have any number of targets within it, which are basically compilable projects
	// in themselves that the project wraps up.
	class ProjectTarget
	{
		public FileReference TargetFilePath;        // The target rules file path on disk, if we have one
		public FileReference ProjectFilePath;       // The project file path on disk
		public FileReference ProjectFilePath; // Path to the .uproject file on disk

		// Optional target rules for this target.
		// If the target came from a *.Target.cs file on disk, then it will have one of these.
		// For targets that are synthetic (like BuildTool or other manually added project files) we won't have a rules object for those.
		public TargetRules TargetRules;

		// Platforms supported by the target
		public BuildTargetPlatform[] SupportedPlatforms;

		// Extra supported build platforms.  Normally the target rules determines these, but for synthetic targets we'll add them here.
		public List<BuildTargetPlatform> ExtraSupportedPlatforms = new List<BuildTargetPlatform>();

		// Extra supported build configurations.  Normally the target rules determines these, but for synthetic targets we'll add them here.
		public List<TargetConfiguration> ExtraSupportedConfigurations = new List<TargetConfiguration>();

		// If true, forces Development configuration regardless of which configuration is set as the Solution Configuration
		public bool ForceDevelopmentConfiguration = false;

		// Whether the project requires 'Deploy' option set (VC projects)
		public bool ProjectDeploys = false;

		// Delegate for creating a rules instance for a given platform/configuration
		public Func<BuildTargetPlatform, TargetConfiguration, TargetRules> CreateRulesDelegate = null;

		public string Name => TargetFilePath.GetFileNameWithoutAnyExtensions();

		public override string ToString() => TargetFilePath.GetFileNameWithoutExtension();
	} // End ProjectTarget

	// Class that stores info about aliased file.
	[DebuggerDisplay("Location : {Location.FullName}\n ProjectPath : {ProjectPath}")]
	struct AliasedFile
	{
		public AliasedFile(FileReference Location, string FileSystemPath, string ProjectPath)
		{
			this.Location = Location;
			this.FileSystemPath = FileSystemPath;
			this.ProjectPath = ProjectPath;
		}
		public readonly FileReference Location; // Full location on disk, AbolutePath.
		public readonly string FileSystemPath;  // File system path, RelativePath, Using by CppProjectFilterFile for ItemName
		public readonly string ProjectPath;     // Project path, Using by CppProjectFilterFile for FilterName.
	}

	abstract class ProjectFile
	{
		// Represents a single source file (or other type of file) in a project
		public class SourceFile
		{
			// <param name="InReference">Path to the source file on disk</param>
			// <param name="InBaseFolder">The directory on this the path within the project will be relative to</param>
			public SourceFile(FileReference InReference, DirectoryReference InBaseFolder)
			{
				Reference = InReference;
				BaseFolder = InBaseFolder;
			}

			public SourceFile()
			{
			}
			
			public FileReference Reference { get; private set; } // File path to file on disk

			// Optional directory that overrides where files in this project are relative to when displayed in the IDE.
			// If null, will default to the project's BaseFolder.
			public DirectoryReference BaseFolder { get; private set; }

			public override string ToString() => Reference.ToString(); // Define ToString() so the debugger can show the name in watch windows
		}

		protected ProjectFile(FileReference InProjectFilePath)
		{
			ProjectFilePath = InProjectFilePath;
			ShouldBuildByDefaultForSolutionTargets = true;
			IntelliSenseCppVersion = CppStandardVersion.Default;
		}

		public FileReference ProjectFilePath { get; protected set; }

		public DirectoryReference BaseDir { get; set; }

		public bool IsGeneratedProject { get; set; } // (as opposed to an imported project)

		// Stub projects function as dumb containers for source files and are never actually "built" by the master project.
		// Stub projects are always "generated" projects.
		public bool IsStubProject { get; set; }

		// Returns true if this is a foreign project,
		// and requires UBT to be passed the path to the .uproject file on the command line.
		public bool IsForeignProject { get; set; }

		public FileReference PluginFilePath { get; set; } // For mod projects, contains the path to the plugin file

		public bool ShouldBuildForAllSolutionTargets { get; set; }

		// Whether this project should be built by default. Can still be built from the IDE through the context menu.
		public bool ShouldBuildByDefaultForSolutionTargets { get; set; }
		
		// C++ version which is used in this project.
		public CppStandardVersion IntelliSenseCppVersion { get; protected set; }

		// All of the targets in this project.  All non-stub projects must have at least one target.
		public readonly List<ProjectTarget> ProjectTargets = new List<ProjectTarget>();

		// Legacy accessor for user search paths
		public List<string> IntelliSenseIncludeSearchPaths => UserIncludePaths.RelativePaths;

		public List<string> IntelliSenseSystemIncludeSearchPaths => SystemIncludePaths.RelativePaths;

		public readonly List<string> IntelliSensePreprocessorDefinitions = new List<string>();

		readonly HashSet<string> UniqueTestPreprocessorDefinitions = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

		public readonly List<ProjectFile> DependsOnProjects = new List<ProjectFile>();

		private readonly Dictionary<FileReference, SourceFile> SourceFileMap = new Dictionary<FileReference, SourceFile>();

		public readonly List<SourceFile> SourceFiles = new List<SourceFile>();

		// Collection of include paths
		public class IncludePathsCollection
		{
			public List<string>                RelativePaths = new List<string>();
			public HashSet<DirectoryReference> AbsolutePaths = new HashSet<DirectoryReference>();
		}

		// Merged list of include paths for the project
		private readonly IncludePathsCollection UserIncludePaths   = new IncludePathsCollection();
		private readonly IncludePathsCollection SystemIncludePaths = new IncludePathsCollection();

		// Map of base directory to User/System Include paths
		protected Dictionary<DirectoryReference, IncludePathsCollection> BaseDirToUserIncludePaths = new Dictionary<DirectoryReference, IncludePathsCollection>();
		protected Dictionary<DirectoryReference, IncludePathsCollection> BaseDirToSystemIncludePaths = new Dictionary<DirectoryReference, IncludePathsCollection>();

		// Adds a list of files to this project, ignoring dupes
		// <param name="FilesToAdd">Files to add</param>
		// <param name="BaseFolder">The directory the path within the project will be relative to</param>
		public void AddFilesToProject(List<FileReference> FilesToAdd, DirectoryReference BaseFolder)
		{
			foreach (FileReference CurFile in FilesToAdd)
			{
				AddFileToProject(CurFile, BaseFolder);
			}
		}

		// Aliased (i.e. files is custom filter tree) in this project
		public readonly List<AliasedFile> AliasedFiles = new List<AliasedFile>();

		// Adds aliased file to the project.
		// <param name="File">Aliased file.</param>
		public void AddAliasedFileToProject(AliasedFile File) => AliasedFiles.Add(File);

		// Adds a file to this project, ignoring dupes
		// <param name="FilePath">Path to the file on disk</param>
		// <param name="BaseFolder">The directory the path within the project will be relative to</param>
		public void AddFileToProject(FileReference FilePath, DirectoryReference BaseFolder)
		{
			// Don't add duplicates
			if (SourceFileMap.TryGetValue(FilePath, out SourceFile ExistingFile))
			{
				if (ExistingFile.BaseFolder != BaseFolder)
				{
					throw new BuildException("Trying to add file '" + FilePath + "' to project '" + ProjectFilePath + "' when the file already exists, but with a different relative base folder '" + BaseFolder + "' is different than the current file's '" + ExistingFile.BaseFolder + "'!");
				}
			}
			else
			{
				SourceFile File = AllocSourceFile(FilePath, BaseFolder);
				if (File != null)
				{
					SourceFileMap[FilePath] = File;
					SourceFiles.Add(File);
				}
			}
		}

		// Splits the definition text into macro name and value (if any).
		private void SplitDefinitionAndValue(string DefinitionString, out String DefinitionNameKey, out String DefinitionValue)
		{
			int EqualsIndex = DefinitionString.IndexOf('=');
			if (0 <= EqualsIndex)
			{
				DefinitionNameKey = DefinitionString.Substring(0, EqualsIndex);
				DefinitionValue   = DefinitionString.Substring(EqualsIndex + 1);
			}
			else
			{
				DefinitionNameKey = DefinitionString;
				DefinitionValue   = "";
			}
		}

		// Old Version : AddModuleAndPaths
		// Adds information about a module to this project file
		public virtual void AddPreprocessorDefintionsAndIncludePaths(BuildModuleCPP CPPBuildModuleToAdd, CppCompileEnvironment CompileEnvironment)
		{
			// CompileEnvironment.Definitions을
			// 중복없이 ProjectFile::IntelliSensePreprocessorDefinitions에 저장
			AddIntelliSensePreprocessorDefinitions(CompileEnvironment.Definitions);

			AddIntelliSenseIncludePaths(SystemIncludePaths, CompileEnvironment.SystemIncludePaths);
			AddIntelliSenseIncludePaths(UserIncludePaths, CompileEnvironment.UserIncludePaths);

			foreach (DirectoryReference BaseDir in CPPBuildModuleToAdd.ModuleDirectories)
			{
				AddIntelliSenseIncludePaths(BaseDir, BaseDirToSystemIncludePaths, CompileEnvironment.SystemIncludePaths);
				AddIntelliSenseIncludePaths(BaseDir, BaseDirToUserIncludePaths,   CompileEnvironment.UserIncludePaths);
			}

			SetIntelliSenseCppVersion(CPPBuildModuleToAdd.ModuleRule.CppStandard);
		}

		// Adds all of the specified preprocessor definitions to this VCProject's list of preprocessor definitions for all modules in the project
#pragma warning disable IDE0059 // Unnecessary assignment of a value
		private void AddIntelliSensePreprocessorDefinitions(List<string> NewPreprocessorDefinitionsToAdd)
		{
			foreach (string NewPreprocessorDefinition in NewPreprocessorDefinitionsToAdd)
			{
				// Don't add definitions and value combinations that have already been added for this project
				string DefinitionsString = NewPreprocessorDefinition;

				if (UniqueTestPreprocessorDefinitions.Add(DefinitionsString))
				{
					SplitDefinitionAndValue(DefinitionsString, out string Definition, out string DefinitionValue);

					if (Definition.EndsWith("_API", StringComparison.Ordinal))
					{
                        DefinitionsString = Definition + "=";
					}

					// Go ahead and check to see if the definition already exists, but the value is different
					bool AlreadyExists = false;

					for (int DefineIndex = 0; DefineIndex < IntelliSensePreprocessorDefinitions.Count; ++DefineIndex)
					{
						SplitDefinitionAndValue(IntelliSensePreprocessorDefinitions[DefineIndex], out string ExistingDefinition, out string ExistingDefinitionValue);

						if (ExistingDefinition == Definition)
						{
							// Already exists, but the value is changing.
							// We don't bother clobbering values for existing defines for this project.
							AlreadyExists = true;
							break;
						}
					}

					if (AlreadyExists == false)
					{
						IntelliSensePreprocessorDefinitions.Add(DefinitionsString);
					}
				}
			}
		}
#pragma warning restore IDE0059 // Unnecessary assignment of a value

		// Adds all of the specified include paths to this VCProject's list of include paths for all modules in the project
		// <param name="BaseDir">The base directory to which the include paths apply</param>
		// <param name="BaseDirToCollection">Map of base directory to include paths</param>
		// <param name="NewIncludePaths">List of include paths to add</param>
		private void AddIntelliSenseIncludePaths(DirectoryReference BaseDir, Dictionary<DirectoryReference, IncludePathsCollection> BaseDirToCollection, IEnumerable<DirectoryReference> NewIncludePaths)
		{
			if (!BaseDirToCollection.TryGetValue(BaseDir, out IncludePathsCollection ModuleUserPaths))
			{
				ModuleUserPaths = new IncludePathsCollection();
				BaseDirToCollection.Add(BaseDir, ModuleUserPaths);
			}

			AddIntelliSenseIncludePaths(ModuleUserPaths, NewIncludePaths);
		}
		
		// Adds all of the specified include paths to this VCProject's list of include paths for all modules in the project
		// <param name="Collection">The collection to add to</param>
		// <param name="NewIncludePaths">List of include paths to add</param>
		private void AddIntelliSenseIncludePaths(IncludePathsCollection OutIncludePaths, IEnumerable<DirectoryReference> NewIncludePaths)
		{
			foreach (DirectoryReference CurPath in NewIncludePaths)
			{
				if(OutIncludePaths.AbsolutePaths.Add(CurPath))
				{
					// Incoming include paths are relative to the solution directory, but we need these paths to be
					// relative to the project file's directory
					string PathRelativeToProjectFile = NormalizeProjectPath(CurPath);
					OutIncludePaths.RelativePaths.Add(PathRelativeToProjectFile);
				}
			}
		}

		// Sets highest C++ version which is used in this project
		// <param name="CppVersion">Version</param>
		private void SetIntelliSenseCppVersion(CppStandardVersion CppVersion)
		{
			if (CppVersion != CppStandardVersion.Default)
			{
				if (IntelliSenseCppVersion < CppVersion)
				{
					IntelliSenseCppVersion = CppVersion;
				}
			}
		}

		// Add the given project to the DepondsOn project list.
		public void AddDependsOnProject(ProjectFile InDependentOnProjectFile)
		{
			// Make sure that it doesn't exist already
			bool AlreadyExists = false;
			foreach (ProjectFile ExistingDependentOn in DependsOnProjects)
			{
				if (ExistingDependentOn == InDependentOnProjectFile)
				{
					AlreadyExists = true;
					break;
				}
			}

			if (AlreadyExists == false)
			{
				DependsOnProjects.Add(InDependentOnProjectFile);
			}
		}
		
		public virtual bool WriteProjectFile(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			// GOTO : BuildTool\ProjectFiles\VisualStudio\VCProject.cs Line : 776
			throw new BuildException("BuildTool cannot automatically generate this project type because WriteProjectFile() was not overridden.");
		}

		public virtual List<Tuple<ProjectFile, string>> WriteDebugProjectFiles(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations, PlatformProjectGeneratorCollection PlatformProjectGenerators) => null;

		public virtual void LoadGUIDFromExistingProject()
		{
		}
		
		public virtual SourceFile AllocSourceFile(FileReference InitFilePath, DirectoryReference InitProjectSubFolder = null)
		{
			return new SourceFile(InitFilePath, InitProjectSubFolder);
		}
		
		// Takes the given path and tries to rebase it relative to the project or solution directory variables.
		public static string NormalizeProjectPath(string InputPath)
		{
			// If the path is rooted in an environment variable, leave it be.
			if (InputPath.StartsWith("$("))
			{
				return InputPath;
			}
			else if(InputPath.EndsWith("\\") || InputPath.EndsWith("/"))
			{
				return NormalizeProjectPath(new DirectoryReference(InputPath));
			}
			else
			{
				return NormalizeProjectPath(new FileReference(InputPath));
			}
		}

		// Takes the given path and tries to rebase it relative to the project.
		public static string NormalizeProjectPath(FileSystemReference InputPath)
		{
			// Try to make it relative to the solution directory.
			if (InputPath.IsUnderDirectory(ProjectFileGenerator.MasterProjectPath))
			{
				return InputPath.MakeRelativeTo(ProjectFileGenerator.IntermediateProjectFilesPath);
			}
			else
			{
				return InputPath.FullName;
			}
		}

		// Takes the given path, normalizes it, and quotes it if necessary.
		public string EscapePath(string InputPath)
		{
			string Result = InputPath;
			if (Result.Contains(' '))
			{
				Result = "\"" + Result + "\"";
			}
			return Result;
		}

		// Visualizer for the debugger
		public override string ToString() => ProjectFilePath.ToString();
	}
}