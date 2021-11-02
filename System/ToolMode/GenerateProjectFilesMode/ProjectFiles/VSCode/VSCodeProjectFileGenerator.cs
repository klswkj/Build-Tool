using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using BuildToolUtilities;

namespace BuildTool
{
	internal sealed class VSCodeProjectFolder : MasterProjectFolder
	{
		public VSCodeProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
			: base(InitOwnerProjectFileGenerator, InitFolderName)
		{
		}
	}

	internal sealed class VSCodeProject : ProjectFile
	{
		public VSCodeProject(FileReference InitFilePath)
			: base(InitFilePath)
		{
		}

		public override bool WriteProjectFile(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations, PlatformProjectGeneratorCollection PlatformProjectGenerators) 
		=> true;
	}

	internal sealed class VSCodeProjectFileGenerator : ProjectFileGenerator
	{
		private DirectoryReference   VSCodeDir;
		private BuildTargetPlatform HostPlatform;

		private bool               bForeignProject;
		private DirectoryReference ProjectRoot;

		readonly private bool      bBuildingForDotNetCore;

		readonly private string    FrameworkExecutableExtension; // = bBuildingForDotNetCore ? ".dll" : ".exe";
		readonly private string    FrameworkLibraryExtension = ".dll";

		// Includes all files in the generated workspace.
		[XMLConfigFile(Name = "IncludeAllFiles")]
		readonly private bool IncludeAllFiles = false;

		private enum EPathType
		{
			Absolute,
			Relative,
		}

		private enum EQuoteType
		{
			Single,	// can be ignored on platforms that don't need it (windows atm)
			Double,
		}

		private string CommonMakePathString(FileSystemReference InRef, EPathType InPathType, DirectoryReference InRelativeRoot)
		{
			if (InRelativeRoot == null)
			{
				InRelativeRoot = ProjectRoot;
			}

			string Processed = InRef.ToString();
			
			switch (InPathType)
			{
				case EPathType.Relative:
				{
					if (InRef.IsUnderDirectory(InRelativeRoot))
					{
						Processed = InRef.MakeRelativeTo(InRelativeRoot).ToString();
					}

					break;
				}

				default:
				{
					break;
				}
			}

			if (HostPlatform == BuildTargetPlatform.Win64)
			{
				Processed = Processed.Replace("\\", "\\\\");
				Processed = Processed.Replace("/", "\\\\");
			}
			else
			{
				Processed = Processed.Replace('\\', '/');
			}

			return Processed;
		}

		private string MakeQuotedPathString(FileSystemReference InRef, EPathType InPathType, DirectoryReference InRelativeRoot = null, EQuoteType InQuoteType = EQuoteType.Double)
		{
			string Processed = CommonMakePathString(InRef, InPathType, InRelativeRoot);

			if (Processed.Contains(" "))
			{
				if (HostPlatform == BuildTargetPlatform.Win64 && InQuoteType == EQuoteType.Double)
				{
					Processed = "\\\"" + Processed + "\\\"";
				}
				else
				{
					Processed = "'" + Processed + "'";
				}
 			}

			return Processed;
		}

		private string MakeUnquotedPathString(FileSystemReference InRef, EPathType InPathType, DirectoryReference InRelativeRoot = null)
		{
			return CommonMakePathString(InRef, InPathType, InRelativeRoot);
		}

		private string MakePathString(FileSystemReference InRef, bool bInAbsolute = false, bool bForceSkipQuotes = false)
		{
			if (bForceSkipQuotes)
			{
				return MakeUnquotedPathString(InRef, bInAbsolute ? EPathType.Absolute : EPathType.Relative, ProjectRoot);
			}
			else
			{
				return MakeQuotedPathString(InRef, bInAbsolute ? EPathType.Absolute : EPathType.Relative, ProjectRoot);
			}
		}

		public VSCodeProjectFileGenerator(FileReference InOnlyGameProject)
			: base(InOnlyGameProject)
		{
			bBuildingForDotNetCore = Environment.CommandLine.Contains("-dotnetcore");
			FrameworkExecutableExtension = bBuildingForDotNetCore ? ".dll" : ".exe";
		}

		private sealed class VSCodeJsonFile
		{
			private readonly List<string> Lines = new List<string>();
			private string TabString = "";

			public VSCodeJsonFile()
			{
			}

			public void BeginRootObject()
			{
				BeginObject();
			}

			public void EndRootObject()
			{
				EndObject();
				if (0 < TabString.Length)
				{
					throw new BuildException("Called EndRootObject before all objects and arrays have been closed");
				}
			}

			public void BeginObject(string Name = null)
			{
				string Prefix = (Name == null) ? string.Empty : Quoted(Name) + ": ";
				Lines.Add(TabString + Prefix + "{");
				TabString += "\t";
			}

			public void EndObject()
			{
				Lines[Lines.Count - 1] = Lines[Lines.Count - 1].TrimEnd(',');
				TabString = TabString.Remove(TabString.Length - 1);
				Lines.Add(TabString + "},");
			}

			public void BeginArray(string Name = null)
			{
				string Prefix = (Name == null)? "" : Quoted(Name) + ": ";
				Lines.Add(TabString + Prefix + "[");
				TabString += "\t";
			}

			public void EndArray()
			{
				Lines[Lines.Count - 1] = Lines[Lines.Count - 1].TrimEnd(',');
				TabString = TabString.Remove(TabString.Length - 1);
				Lines.Add(TabString + "],");
			}

			public void AddField(string Name, bool Value)
			{
				Lines.Add(TabString + Quoted(Name) + ": " + Value.ToString().ToLower() + ",");
			}

			public void AddField(string Name, string Value)
			{
				Lines.Add(TabString + Quoted(Name) + ": " + Quoted(Value) + ",");
			}

			public void AddUnnamedField(string Value)
			{
				Lines.Add(TabString + Quoted(Value) + ",");
			}

			public void Write(FileReference File)
			{
				Lines[Lines.Count - 1] = Lines[Lines.Count - 1].TrimEnd(',');
				FileReference.WriteAllLines(File, Lines.ToArray());
			}

			private string Quoted(string Value)
			{
				return "\"" + Value + "\"";
			}
		}

		public override string ProjectFileExtension => ".vscode";

		public override bool GetbGenerateIntelliSenseData() => true;

		protected override ProjectFile AllocateProjectFile(FileReference InitFilePath)
		{
			return new VSCodeProject(InitFilePath);
		}

		public override MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
		{
			return new VSCodeProjectFolder(InitOwnerProjectFileGenerator, InitFolderName);
		} 

		protected override bool WriteMasterProjectFile(ProjectFile BuildToolProject, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			VSCodeDir = DirectoryReference.Combine(MasterProjectPath, ".vscode");
			DirectoryReference.CreateDirectory(VSCodeDir);

			ProjectRoot     = BuildTool.RootDirectory;
			HostPlatform    = BuildHostPlatform.Current.Platform;
			bForeignProject = !VSCodeDir.IsUnderDirectory(ProjectRoot);

			List<ProjectFile> Projects;

			if (bForeignProject)
			{
				Projects = new List<ProjectFile>();

				foreach (ProjectFile Project in AllProjectFiles)
				{
					if (GameProjectName == Project.ProjectFilePath.GetFileNameWithoutAnyExtensions())
					{
						Projects.Add(Project);
						break;
					}
				}
			}
			else
			{
				Projects = new List<ProjectFile>(AllProjectFiles);
			}

			Projects.Sort((A, B) => { return A.ProjectFilePath.GetFileName().CompareTo(B.ProjectFilePath.GetFileName()); });

			VSCodeProjectData ProjectData = GatherProjectData(Projects);

			WriteTasksFile(ProjectData);
			WriteLaunchFile(ProjectData);
			WriteWorkspaceSettingsFile(Projects);
			WriteCppPropertiesFile(VSCodeDir, false, ProjectData);

			WriteWorkspaceFile();
			//WriteProjectDataFile(ProjectData);

			if (bForeignProject && bIncludeEngineSourceInSolution)
			{
				// for installed builds we need to write the cpp properties file under the installed engine as well for intellisense to work
				DirectoryReference EngineVSCodeDirectory = DirectoryReference.Combine(BuildTool.RootDirectory, ".vscode");
				WriteCppPropertiesFile(EngineVSCodeDirectory, true, ProjectData);
			}

			return true;
		}

		private class VSCodeProjectData
		{
			public enum EOutputType
			{
				Library,
				Exe,
				WinExe, // some projects have this so we need to read it, but it will be converted across to Exe so no code should handle it!
			}

			public class VSCodeBuildProduct
			{
				public FileReference OutputFile { get; set; }
				public FileReference UProjectFile { get; set; }
				public TargetConfiguration Config { get; set; }
				public BuildTargetPlatform Platform { get; set; }
				public EOutputType OutputType { get; set; }
				public CsProjectInfo CSharpInfo { get; set; }

				public override string ToString() => Platform.ToString() + " " + Config.ToString();

			}

			public class VSCodeTarget
			{
				public string     Name;
				public TargetType Type;

				public List<VSCodeBuildProduct> BuildProducts = new List<VSCodeBuildProduct>();
				public List<string>             Defines       = new List<string>();

				public VSCodeTarget(VSCodeProject InParentProject, string InName, TargetType InType)
				{
					Name = InName;
					Type = InType;
					InParentProject.Targets.Add(this);
				}

				public override string ToString() => Name.ToString() + " " + Type.ToString();
			}

			public class VSCodeProject
			{
				public string      Name;
				public ProjectFile SourceProject;
				public List<VSCodeTarget> Targets = new List<VSCodeTarget>();
				public List<string> IncludePaths;

				public override string ToString() => Name;
			}

			public List<VSCodeProject> NativeProjects = new List<VSCodeProject>();
			public List<VSCodeProject> CSharpProjects = new List<VSCodeProject>();
			public List<VSCodeProject> AllProjects    = new List<VSCodeProject>();
			public List<string> CombinedIncludePaths  = new List<string>();
			// 이거 위주로 추적
			public List<string> CombinedPreprocessorDefinitions = new List<string>();
		}

		private VSCodeProjectData GatherProjectData(List<ProjectFile> InProjects)
		{
			VSCodeProjectData ProjectData = new VSCodeProjectData();

			foreach (ProjectFile Project in InProjects)
			{
				// Create new project record
				VSCodeProjectData.VSCodeProject NewProject = new VSCodeProjectData.VSCodeProject
				{
					Name          = Project.ProjectFilePath.GetFileNameWithoutExtension(),
					SourceProject = Project
				};

				ProjectData.AllProjects.Add(NewProject);

				// Add into the correct easy-access list
				if (Project is VSCodeProject)
				{
					foreach (ProjectTarget Target in Project.ProjectTargets)
					{
						Array Configs = Enum.GetValues(typeof(TargetConfiguration));
						List<BuildTargetPlatform> Platforms = new List<BuildTargetPlatform>(Target.TargetRules.GetSupportedPlatforms());

						// NewProject변수 안의 컨테이너에 NewTarget저장 코딩 x같이하네...
						VSCodeProjectData.VSCodeTarget NewTarget = new VSCodeProjectData.VSCodeTarget(NewProject, Target.TargetRules.Name, Target.TargetRules.Type);

						if (HostPlatform != BuildTargetPlatform.Win64)
						{
							Platforms.Remove(BuildTargetPlatform.Win64);
							Platforms.Remove(BuildTargetPlatform.Win32);
						}

						foreach (BuildTargetPlatform ItrPlatform in Platforms)
						{
                            BuildPlatform BuildPlatform = global::BuildTool.BuildPlatform.GetBuildPlatform(ItrPlatform, true);

							if (SupportedPlatforms.Contains(ItrPlatform) && 
								(BuildPlatform != null) && 
								(BuildPlatform.HasRequiredSDKsInstalled() == SDKStatus.Valid))
							{
								foreach (TargetConfiguration ItrConfig in Configs)
								{
									if (MSBuildProjectFile.IsValidProjectPlatformAndConfiguration(Target, ItrPlatform, ItrConfig/*, PlatformProjectGenerators*/))
									{
										NewTarget.BuildProducts.Add(
										new VSCodeProjectData.VSCodeBuildProduct
										{
											Platform     = ItrPlatform,
											Config       = ItrConfig,
											UProjectFile = Target.ProjectFilePath,
											OutputType   = VSCodeProjectData.EOutputType.Exe,
											OutputFile   = GetExecutableFilename(Project, Target, ItrPlatform, ItrConfig),
											CSharpInfo   = null
										});
									}
								}
							}
						}

						NewTarget.Defines.AddRange(Target.TargetRules.GlobalDefinitions);
					}

					NewProject.IncludePaths = new List<string>();
					
					List<string> RawIncludes = new List<string>(Project.IntelliSenseIncludeSearchPaths);
					RawIncludes.AddRange(Project.IntelliSenseSystemIncludeSearchPaths);

					if (HostPlatform == BuildTargetPlatform.Win64)
					{
						RawIncludes.AddRange(VCToolChain.GetVCIncludePaths(BuildTargetPlatform.Win64, WindowsPlatform.GetDefaultCompiler(null), null).Trim(';').Split(';'));
					}
					else
					{
						RawIncludes.Add("/usr/include");
						RawIncludes.Add("/usr/local/include");
					}

					foreach (string IncludePath in RawIncludes)
					{
						DirectoryReference AbsPath = DirectoryReference.Combine(Project.ProjectFilePath.Directory, IncludePath);
						if (DirectoryReference.Exists(AbsPath))
						{
							string Processed = MakeUnquotedPathString(AbsPath, EPathType.Absolute);

							if (!NewProject.IncludePaths.Contains(Processed))
							{
								NewProject.IncludePaths.Add(Processed);
							}

							if (!ProjectData.CombinedIncludePaths.Contains(Processed))
							{
								ProjectData.CombinedIncludePaths.Add(Processed);
							}
							
						}
					}

					foreach (string Definition in Project.IntelliSensePreprocessorDefinitions)
					{
						string Processed = Definition.Replace("\"", "\\\"");
						// removing trailing spaces on preprocessor definitions as these can be added as empty defines confusing vscode
						Processed = Processed.TrimEnd(' ');
						if (!ProjectData.CombinedPreprocessorDefinitions.Contains(Processed))
						{
							ProjectData.CombinedPreprocessorDefinitions.Add(Processed);
						}
					}

					ProjectData.NativeProjects.Add(NewProject);
				}
				else
				{
					VCSharpProjectFile VCSharpProject = Project as VCSharpProjectFile;

					if (VCSharpProject.IsDotNETCoreProject() == bBuildingForDotNetCore)
					{
						string ProjectName = Project.ProjectFilePath.GetFileNameWithoutExtension();

						VSCodeProjectData.VSCodeTarget Target = new VSCodeProjectData.VSCodeTarget(NewProject, ProjectName, TargetType.Program);

						TargetConfiguration[] Configs = { TargetConfiguration.Debug, TargetConfiguration.Development };

						foreach (TargetConfiguration Config in Configs)
						{
							CsProjectInfo Info = VCSharpProject.GetProjectInfo(Config);

							if (!Info.IsDotNETCoreProject() && Info.Properties.ContainsKey("OutputPath"))
							{
								VSCodeProjectData.EOutputType OutputType;
								if (Info.Properties.TryGetValue("OutputType", out string OutputTypeName))
								{
									OutputType = (VSCodeProjectData.EOutputType)Enum.Parse(typeof(VSCodeProjectData.EOutputType), OutputTypeName);
								}
								else
								{
									OutputType = VSCodeProjectData.EOutputType.Library;
								}

								if (OutputType == VSCodeProjectData.EOutputType.WinExe)
								{
									OutputType = VSCodeProjectData.EOutputType.Exe;
								}

								FileReference OutputFile = null;
								HashSet<FileReference> ProjectBuildProducts = new HashSet<FileReference>();
								Info.FindCompiledBuildProducts(DirectoryReference.Combine(VCSharpProject.ProjectFilePath.Directory, Info.Properties["OutputPath"]), ProjectBuildProducts);
								foreach (FileReference ProjectBuildProduct in ProjectBuildProducts)
								{
									if ((OutputType == VSCodeProjectData.EOutputType.Exe && ProjectBuildProduct.GetExtension() == FrameworkExecutableExtension) ||
										(OutputType == VSCodeProjectData.EOutputType.Library && ProjectBuildProduct.GetExtension() == FrameworkLibraryExtension))
									{
										OutputFile = ProjectBuildProduct;
										break;
									}
								}

								if (OutputFile != null)
								{
									Target.BuildProducts.Add(
									new VSCodeProjectData.VSCodeBuildProduct
									{
										Platform   = HostPlatform,
										Config     = Config,
										OutputFile = OutputFile,
										OutputType = OutputType,
										CSharpInfo = Info
									});
								}
							}
						}

						ProjectData.CSharpProjects.Add(NewProject);
					}
				}
			}

			return ProjectData;
		}

#pragma warning disable IDE0051 // Remove unused private members
		private void WriteProjectDataFile(VSCodeProjectData ProjectData)
		{
			VSCodeJsonFile OutFile = new VSCodeJsonFile();

			OutFile.BeginRootObject();
			OutFile.BeginArray("Projects");

			foreach (VSCodeProjectData.VSCodeProject Project in ProjectData.AllProjects)
			{
				foreach (VSCodeProjectData.VSCodeTarget Target in Project.Targets)
				{
					OutFile.BeginObject();
					{
						OutFile.AddField("Name", Project.Name);
						OutFile.AddField("Type", Target.Type.ToString());

						if (Project.SourceProject is VCSharpProjectFile)
						{
							OutFile.AddField("ProjectFile", MakePathString(Project.SourceProject.ProjectFilePath));
						}
						else
						{
							OutFile.BeginArray("IncludePaths");
							{
								foreach (string IncludePath in Project.IncludePaths)
								{
									OutFile.AddUnnamedField(IncludePath);
								}
							}
							OutFile.EndArray();
						}

						OutFile.BeginArray("Defines");
						{
							foreach (string Define in Target.Defines)
							{
								OutFile.AddUnnamedField(Define);
							}
						}
						OutFile.EndArray();

						OutFile.BeginArray("BuildProducts");
						{
							foreach (VSCodeProjectData.VSCodeBuildProduct BuildProduct in Target.BuildProducts)
							{
								OutFile.BeginObject();
								{
									OutFile.AddField("Platform", BuildProduct.Platform.ToString());
									OutFile.AddField("Config", BuildProduct.Config.ToString());
									OutFile.AddField("OutputFile", MakePathString(BuildProduct.OutputFile));
									OutFile.AddField("OutputType", BuildProduct.OutputType.ToString());
								}
								OutFile.EndObject();
							}
						}
						OutFile.EndArray();
					}
					OutFile.EndObject();
				}
			}

			OutFile.EndArray();
			OutFile.EndRootObject();
			OutFile.Write(FileReference.Combine(VSCodeDir, "VSCodeBuildTool.json"));
		}
#pragma warning restore IDE0051 // Remove unused private members

		private void WriteCppPropertiesFile(DirectoryReference OutputDirectory, bool RewriteIncludePaths, VSCodeProjectData Projects)
		{
			DirectoryReference.CreateDirectory(OutputDirectory);

			VSCodeJsonFile OutFile = new VSCodeJsonFile();

			OutFile.BeginRootObject();
			{
				OutFile.BeginArray("configurations");
				{
					OutFile.BeginObject();
					{
						OutFile.AddField("name", "Engine");

						OutFile.BeginArray("includePath");
						{
							if (!RewriteIncludePaths)
							{
								foreach (var Path in Projects.CombinedIncludePaths)
								{
									OutFile.AddUnnamedField(Path);
								}
							}
							else
							{
								OutFile.AddUnnamedField("${workspaceFolder}/**");
							}
						}
						OutFile.EndArray();

						if (HostPlatform == BuildTargetPlatform.Win64)
						{
							OutFile.AddField("intelliSenseMode", "msvc-x64");
						}
						else
						{
							OutFile.AddField("intelliSenseMode", "clang-x64");
						}

						if (HostPlatform == BuildTargetPlatform.Mac)
						{
							OutFile.BeginArray("macFrameworkPath");
							{
								OutFile.AddUnnamedField("/System/Library/Frameworks");
								OutFile.AddUnnamedField("/Library/Frameworks");
							}
							OutFile.EndArray();
						}

						OutFile.BeginArray("defines");
						{
							foreach (string Definition in Projects.CombinedPreprocessorDefinitions)
							{
								OutFile.AddUnnamedField(Definition);
							}
						}
						OutFile.EndArray();
					}
					OutFile.EndObject();
				}
				OutFile.EndArray();
			}

			OutFile.EndRootObject();
			OutFile.Write(FileReference.Combine(OutputDirectory, "c_cpp_properties.json"));
		}

		private void WriteNativeTask(VSCodeProjectData.VSCodeProject InProject, VSCodeJsonFile OutFile)
		{
			string[] Commands = { "Build", "Rebuild", "Clean" };

			foreach (VSCodeProjectData.VSCodeTarget Target in InProject.Targets)
			{
				foreach (VSCodeProjectData.VSCodeBuildProduct BuildProduct in Target.BuildProducts)
				{
					foreach (string BaseCommand in Commands)
					{
						string Command       = BaseCommand == "Rebuild" ? "Build" : BaseCommand;
						string TaskName      = String.Format("{0} {1} {2} {3}", Target.Name, BuildProduct.Platform.ToString(), BuildProduct.Config, BaseCommand);
						string CleanTaskName = String.Format("{0} {1} {2} {3}", Target.Name, BuildProduct.Platform.ToString(), BuildProduct.Config, "Clean");
						
						//List<string> ExtraParams = new List<string>();

						OutFile.BeginObject();

						{
							OutFile.AddField("label", TaskName);
							OutFile.AddField("group", "build");

							string CleanParam = Command == "Clean" ? "-clean" : null;

							if (bBuildingForDotNetCore)
							{
								OutFile.AddField("command", "dotnet");
							}
							else
							{
								if (HostPlatform == BuildTargetPlatform.Win64)
								{
									OutFile.AddField("command", MakePathString(FileReference.Combine(ProjectRoot, "Engine", "Build", "BatchFiles", Command + ".bat")));
									CleanParam = null;
								}
								else
								{
									OutFile.AddField("command", MakePathString(FileReference.Combine(ProjectRoot, "Engine", "Build", "BatchFiles", HostPlatform.ToString(), "Build.sh")));

									if (Command == "Clean")
									{
										CleanParam = "-clean";
									}
								}
							}

							OutFile.BeginArray("args");
							{
								if (bBuildingForDotNetCore)
								{
									OutFile.AddUnnamedField(MakeUnquotedPathString(FileReference.Combine
										(ProjectRoot, "Engine", "Binaries", "DotNET", "BuildTool_NETCore.dll"), EPathType.Relative));
								}

								OutFile.AddUnnamedField(Target.Name);
								OutFile.AddUnnamedField(BuildProduct.Platform.ToString());
								OutFile.AddUnnamedField(BuildProduct.Config.ToString());
								if (bForeignProject)
								{
									OutFile.AddUnnamedField(MakeUnquotedPathString(BuildProduct.UProjectFile, EPathType.Relative, null));
								}
								OutFile.AddUnnamedField("-waitmutex");

								if (!string.IsNullOrEmpty(CleanParam))
								{
									OutFile.AddUnnamedField(CleanParam);
								}
							}
							OutFile.EndArray();

							OutFile.AddField("problemMatcher", "$msCompile");

							if (!bForeignProject || BaseCommand == "Rebuild")
							{
								OutFile.BeginArray("dependsOn");
								{
									if (!bForeignProject)
									{
										if (Command == "Build" && Target.Type == TargetType.Editor)
										{
											OutFile.AddUnnamedField("ShaderCompileWorker " + HostPlatform.ToString() + " Development Build");
										}
										else
										{
											OutFile.AddUnnamedField("BuildTool " + HostPlatform.ToString() + " Development Build");
										}
									}

									if (BaseCommand == "Rebuild")
									{
										OutFile.AddUnnamedField(CleanTaskName);
									}
								}
								OutFile.EndArray();
							}

							OutFile.AddField("type", "shell");

							OutFile.BeginObject("options");
							{
								OutFile.AddField("cwd", MakeUnquotedPathString(ProjectRoot, EPathType.Absolute));
							}
							OutFile.EndObject();
						}
						OutFile.EndObject();
					}
				}
			}
		}

		private void WriteCSharpTask(VSCodeProjectData.VSCodeProject InProject, VSCodeJsonFile OutFile)
		{
			VCSharpProjectFile ProjectFile = InProject.SourceProject as VCSharpProjectFile;
			bool bIsDotNetCore = ProjectFile.IsDotNETCoreProject();
			string[] Commands = { "Build", "Clean" };

			foreach (VSCodeProjectData.VSCodeTarget Target in InProject.Targets)
			{
				foreach (VSCodeProjectData.VSCodeBuildProduct BuildProduct in Target.BuildProducts)
				{
					foreach (string Command in Commands)
					{
						string TaskName = String.Format("{0} {1} {2} {3}", Target.Name, BuildProduct.Platform, BuildProduct.Config, Command);

						OutFile.BeginObject();
						{
							OutFile.AddField("label", TaskName);
							OutFile.AddField("group", "build");
							if (bIsDotNetCore)
							{
								OutFile.AddField("command", "dotnet");
							}
							else
							{
								if (Utils.IsRunningOnMono)
								{
									OutFile.AddField("command", MakePathString(FileReference.Combine(ProjectRoot, "Engine", "Build", "BatchFiles", HostPlatform.ToString(), "RunXBuild.sh")));
								}
								else
								{
									OutFile.AddField("command", MakePathString(FileReference.Combine(ProjectRoot, "Engine", "Build", "BatchFiles", "MSBuild.bat")));
								}
							}
							OutFile.BeginArray("args");
							{
								if (bIsDotNetCore)
								{
									OutFile.AddUnnamedField(Command.ToLower());
								}
								else
								{
									OutFile.AddUnnamedField("/t:" + Command.ToLower());
								}
								
								DirectoryReference BuildRoot = HostPlatform == BuildTargetPlatform.Win64 ? ProjectRoot : DirectoryReference.Combine(ProjectRoot, "Engine");
								OutFile.AddUnnamedField(MakeUnquotedPathString(InProject.SourceProject.ProjectFilePath, EPathType.Relative, BuildRoot));

								OutFile.AddUnnamedField("/p:GenerateFullPaths=true");
								if (HostPlatform == BuildTargetPlatform.Win64)
								{
									OutFile.AddUnnamedField("/p:DebugType=portable");
								}
								OutFile.AddUnnamedField("/verbosity:minimal");

								if (bIsDotNetCore)
								{
									OutFile.AddUnnamedField("--configuration");
									OutFile.AddUnnamedField(BuildProduct.Config.ToString());
									OutFile.AddUnnamedField("--output");
									OutFile.AddUnnamedField(MakePathString(BuildProduct.OutputFile.Directory));
								}
								else
								{
									OutFile.AddUnnamedField("/p:Configuration=" + BuildProduct.Config.ToString());
								}
							}
							OutFile.EndArray();
						}
						OutFile.AddField("problemMatcher", "$msCompile");

						if (!bBuildingForDotNetCore)
						{
							OutFile.AddField("type", "shell");
						}

						OutFile.BeginObject("options");
						{
							OutFile.AddField("cwd", MakeUnquotedPathString(ProjectRoot, EPathType.Absolute));
						}

						OutFile.EndObject();
						OutFile.EndObject();
					}
				}
			}
		}

		private void WriteTasksFile(VSCodeProjectData ProjectData)
		{
			VSCodeJsonFile OutFile = new VSCodeJsonFile();

			OutFile.BeginRootObject();
			{
				OutFile.AddField("version", "2.0.0");

				OutFile.BeginArray("tasks");
				{
					foreach (VSCodeProjectData.VSCodeProject NativeProject in ProjectData.NativeProjects)
					{
						WriteNativeTask(NativeProject, OutFile);
					}

					foreach (VSCodeProjectData.VSCodeProject CSharpProject in ProjectData.CSharpProjects)
					{
						WriteCSharpTask(CSharpProject, OutFile);
					}

					OutFile.EndArray();
				}
			}
			OutFile.EndRootObject();

			OutFile.Write(FileReference.Combine(VSCodeDir, "tasks.json"));
		}
		
		public string EscapePath(string InputPath)
		{
			string Result = InputPath;
			if (Result.Contains(" "))
			{
				Result = "\"" + Result + "\"";
			}
			return Result;
		}

		private FileReference GetExecutableFilename(ProjectFile Project, ProjectTarget Target, BuildTargetPlatform Platform, TargetConfiguration Configuration)
		{
			TargetRules   TargetRulesObject = Target.TargetRules;
			FileReference TargetFilePath    = Target.TargetFilePath;

			string TargetName      = TargetFilePath == null ? Project.ProjectFilePath.GetFileNameWithoutExtension() : TargetFilePath.GetFileNameWithoutAnyExtensions();
			string UBTPlatformName = Platform.ToString();

			//string UBTConfigurationName = Configuration.ToString();
			//string ProjectName = Project.ProjectFilePath.GetFileNameWithoutExtension();

			// Setup output path
			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(Platform);

			// Figure out if this is a monolithic build
			bool bShouldCompileMonolithic = BuildPlatform.ShouldCompileMonolithicBinary(Platform);

			if (TargetRulesObject != null)
			{
				bShouldCompileMonolithic |= (Target.CreateRulesDelegate(Platform, Configuration).LinkType == TargetLinkType.Monolithic);
			}

			TargetType TargetRulesType = Target.TargetRules == null ? TargetType.Program : Target.TargetRules.Type;

			// Get the output directory
			DirectoryReference RootDirectory = BuildTool.EngineDirectory;
			if (TargetRulesType != TargetType.Program && (bShouldCompileMonolithic || TargetRulesObject.BuildEnvironment == TargetBuildEnvironment.Unique))
			{
				if(Target.ProjectFilePath != null)
				{
					RootDirectory = Target.ProjectFilePath.Directory;
				}
			}

			if (TargetRulesType == TargetType.Program)
			{
				if(Target.ProjectFilePath != null)
				{
					RootDirectory = Target.ProjectFilePath.Directory;
				}
			}

			// Get the output directory
			DirectoryReference OutputDirectory = DirectoryReference.Combine(RootDirectory, "Binaries", UBTPlatformName);

			// Get the executable name (minus any platform or config suffixes)
			string BinaryName;
			if(Target.TargetRules.BuildEnvironment == TargetBuildEnvironment.Shared && TargetRulesType != TargetType.Program)
			{
				BinaryName = BuildTarget.GetAppNameForTargetType(TargetRulesType);
			}
			else
			{
				BinaryName = TargetName;
			}

			// Make the output file path
			string BinaryFileName = BuildTarget.MakeBinaryFileName(BinaryName, Platform, Configuration, TargetRulesObject.Architecture, TargetRulesObject.UndecoratedConfiguration, BuildBinaryType.Executable);
			string ExecutableFilename = FileReference.Combine(OutputDirectory, BinaryFileName).FullName;

			// Include the path to the actual executable for a Mac app bundle
			if (Platform == BuildTargetPlatform.Mac && !Target.TargetRules.bIsBuildingConsoleApplication)
			{
				ExecutableFilename += ".app/Contents/MacOS/" + Path.GetFileName(ExecutableFilename);
			}

			return new FileReference(ExecutableFilename);
		}

		private void WriteNativeLaunchConfig(VSCodeProjectData.VSCodeProject InProject, VSCodeJsonFile OutFile)
		{
			foreach (VSCodeProjectData.VSCodeTarget Target in InProject.Targets)
			{
				foreach (VSCodeProjectData.VSCodeBuildProduct BuildProduct in Target.BuildProducts)
				{
					if (BuildProduct.Platform == HostPlatform)
					{
						string LaunchTaskName = String.Format("{0} {1} {2} Build", Target.Name, BuildProduct.Platform, BuildProduct.Config);

						OutFile.BeginObject();
						{
							OutFile.AddField("name", Target.Name + " (" + BuildProduct.Config.ToString() + ")");
							OutFile.AddField("request", "launch");
							OutFile.AddField("preLaunchTask", LaunchTaskName);
							OutFile.AddField("program", MakeUnquotedPathString(BuildProduct.OutputFile, EPathType.Absolute));								
							
							OutFile.BeginArray("args");
							{
								if (Target.Type == TargetRules.TargetType.Editor)
								{
									if (InProject.Name != "MyEngine")
									{
										if (bForeignProject)
										{
											OutFile.AddUnnamedField(MakePathString(BuildProduct.UProjectFile, false, true));
										}
										else
										{
											OutFile.AddUnnamedField(InProject.Name);
										}
									}
								}

							}
							OutFile.EndArray();

							OutFile.AddField("cwd", MakeUnquotedPathString(ProjectRoot, EPathType.Absolute));

							if (HostPlatform == BuildTargetPlatform.Win64)
							{
								OutFile.AddField("stopAtEntry", false);
								OutFile.AddField("externalConsole", true);

								OutFile.AddField("type", "cppvsdbg");
								OutFile.AddField("visualizerFile", MakeUnquotedPathString(FileReference.Combine(ProjectRoot, "Engine", "Extras", "VisualStudioDebugging", "MyEngine.natvis"), EPathType.Absolute));
							}
							else
							{
								OutFile.AddField("type", "lldb");
							}
						}
						OutFile.EndObject();
					}
				}
			}
		}

		private void WriteSingleCSharpLaunchConfig(VSCodeJsonFile OutFile, string InTaskName, string InBuildTaskName, FileReference InExecutable, string[] InArgs, bool bIsDotNetCore)
		{
			OutFile.BeginObject();
			{
				OutFile.AddField("name", InTaskName);

				if (bIsDotNetCore)
				{
					OutFile.AddField("type", "coreclr");
				}
				else
				{
					if (HostPlatform == BuildTargetPlatform.Win64)
					{
						OutFile.AddField("type", "clr");
					}
					else
					{
						OutFile.AddField("type", "mono");
					}
				}

				OutFile.AddField("request", "launch");

				if (!string.IsNullOrEmpty(InBuildTaskName))
				{
					OutFile.AddField("preLaunchTask", InBuildTaskName);
				}
				
				DirectoryReference CWD = ProjectRoot;

				if (bIsDotNetCore)
				{
					OutFile.AddField("program", "dotnet");
					OutFile.BeginArray("args");
					{
						OutFile.AddUnnamedField(MakePathString(InExecutable));

						if (InArgs != null)
						{
							foreach (string Arg in InArgs)
							{
								OutFile.AddUnnamedField(Arg);
							}
						}
					}
					OutFile.EndArray();
					OutFile.AddField("externalConsole", true);
					OutFile.AddField("stopAtEntry", false);
				}
				else
				{
					OutFile.AddField("program", MakeUnquotedPathString(InExecutable, EPathType.Absolute));

					if (HostPlatform == BuildTargetPlatform.Win64)
					{
						OutFile.AddField("console", "externalTerminal");
					}
					else
					{
						OutFile.AddField("console", "internalConsole");
					}

					OutFile.BeginArray("args");
					if (InArgs != null)
					{
						foreach (string Arg in InArgs)
						{
							OutFile.AddUnnamedField(Arg);
						}
					}
					OutFile.EndArray();

					OutFile.AddField("internalConsoleOptions", "openOnSessionStart");
				}

				OutFile.AddField("cwd", MakeUnquotedPathString(CWD, EPathType.Absolute));
			}
			OutFile.EndObject();
		}

		private void WriteCSharpLaunchConfig(VSCodeProjectData.VSCodeProject InProject, VSCodeJsonFile OutFile)
		{
			VCSharpProjectFile CSharpProject = InProject.SourceProject as VCSharpProjectFile;
			bool bIsDotNetCore = CSharpProject.IsDotNETCoreProject();

			foreach (VSCodeProjectData.VSCodeTarget Target in InProject.Targets)
			{
				foreach (VSCodeProjectData.VSCodeBuildProduct BuildProduct in Target.BuildProducts)
				{
					if (BuildProduct.OutputType == VSCodeProjectData.EOutputType.Exe)
					{
						string TaskName = String.Format("{0} ({1})", Target.Name, BuildProduct.Config);
						string BuildTaskName = String.Format("{0} {1} {2} Build", Target.Name, HostPlatform, BuildProduct.Config);

						WriteSingleCSharpLaunchConfig(OutFile, TaskName, BuildTaskName, BuildProduct.OutputFile, null, bIsDotNetCore);
					}
				}
			}
		}

		private void WriteLaunchFile(VSCodeProjectData ProjectData)
		{
			VSCodeJsonFile OutFile = new VSCodeJsonFile();

			OutFile.BeginRootObject();
			{
				OutFile.AddField("version", "0.2.0");
				OutFile.BeginArray("configurations");

				{
					foreach (VSCodeProjectData.VSCodeProject Project in ProjectData.NativeProjects)
					{
						WriteNativeLaunchConfig(Project, OutFile);
					}

					foreach (VSCodeProjectData.VSCodeProject Project in ProjectData.CSharpProjects)
					{
						WriteCSharpLaunchConfig(Project, OutFile);
					}
				}

				// Add in a special task for regenerating project files
				string PreLaunchTask = "";
				List<string> Args = new List<string> { "-projectfiles", "-vscode" };

				if (bForeignProject)
				{
					Args.Add("-project=\\\"" + MakeUnquotedPathString(OnlyGameProject, EPathType.Absolute) + "\\\"");
					Args.Add("-game");
					Args.Add("-engine");
				}
				else
				{
					PreLaunchTask = "BuildTool " + HostPlatform.ToString() + " Development Build";
				}

				WriteSingleCSharpLaunchConfig
				(
					OutFile,
					"Generate Project Files",
					PreLaunchTask,
					FileReference.Combine(ProjectRoot, "Engine", "Binaries", "DotNET", "BuildTool.exe"),
					Args.ToArray(),
					bBuildingForDotNetCore
				);

				OutFile.EndArray();
			}
			OutFile.EndRootObject();

			OutFile.Write(FileReference.Combine(VSCodeDir, "launch.json"));
		}

		protected override void ConfigureProjectFileGeneration(string[] Arguments, ref bool IncludeAllPlatforms)
		{
			base.ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);
		}

		private void WriteWorkspaceSettingsFile(List<ProjectFile> Projects)
		{
			VSCodeJsonFile OutFile = new VSCodeJsonFile();

			List<string> PathsToExclude = new List<string>();

			foreach (ProjectFile Project in Projects)
			{
				bool bFoundTarget = false;
				foreach (ProjectTarget Target in Project.ProjectTargets)
				{
					if (Target.TargetFilePath != null)
					{
						DirectoryReference ProjDir = Target.TargetFilePath.Directory.GetDirectoryName() == "Source" ? Target.TargetFilePath.Directory.ParentDirectory : Target.TargetFilePath.Directory;
						GetExcludePathsCPP(ProjDir, PathsToExclude);
						
						DirectoryReference PluginRootDir = DirectoryReference.Combine(ProjDir, "Plugins");
						WriteWorkspaceSettingsFileForPlugins(PluginRootDir, PathsToExclude);

						bFoundTarget = true;
					}
				}

				if (!bFoundTarget)
				{
					GetExcludePathsCSharp(Project.ProjectFilePath.Directory.ToString(), PathsToExclude);
				}
			}

			OutFile.BeginRootObject();
			{
				if (!IncludeAllFiles)
				{
					OutFile.BeginObject("files.exclude");
					{
						string WorkspaceRoot = BuildTool.RootDirectory.ToString().Replace('\\', '/') + "/";
						foreach (string PathToExclude in PathsToExclude)
						{
							OutFile.AddField(PathToExclude.Replace('\\', '/').Replace(WorkspaceRoot, ""), true);
						}
					}
					OutFile.EndObject();
				}
			}
			OutFile.EndRootObject();

			OutFile.Write(FileReference.Combine(VSCodeDir, "settings.json"));
		}

		private void WriteWorkspaceSettingsFileForPlugins(DirectoryReference PluginBaseDir, List<string> PathsToExclude)
		{
			if (DirectoryReference.Exists(PluginBaseDir))
			{
				foreach (DirectoryReference SubDir in DirectoryReference.EnumerateDirectories(PluginBaseDir, "*", SearchOption.TopDirectoryOnly))
				{
					string[] UPluginFiles = Directory.GetFiles(SubDir.ToString(), "*.uplugin");
					if (UPluginFiles.Length == 1)
					{
						DirectoryReference PluginDir = SubDir;
						GetExcludePathsCPP(PluginDir, PathsToExclude);
					}
					else
					{
						WriteWorkspaceSettingsFileForPlugins(SubDir, PathsToExclude);
					}
				}
			}
		}
		
		private void WriteWorkspaceFile()
		{
			VSCodeJsonFile WorkspaceFile = new VSCodeJsonFile();

			WorkspaceFile.BeginRootObject();
			{
				WorkspaceFile.BeginArray("folders");
				{
					// Add the directory in which which the code-workspace file exists.
					// This is also known as ${workspaceRoot}
					WorkspaceFile.BeginObject();
					{
						string ProjectName = bForeignProject ? GameProjectName : "MyEngine";
						
						if(ProjectName is null)
						{
							ProjectName = "MyEngine";
						}

						WorkspaceFile.AddField("name", ProjectName);
						WorkspaceFile.AddField("path", ".");
					}
					WorkspaceFile.EndObject();

					// If this project is outside the engine folder, add the root engine directory
					if (bIncludeEngineSourceInSolution && bForeignProject)
					{
						WorkspaceFile.BeginObject();
						{
							WorkspaceFile.AddField("name", "MyEngine");
							WorkspaceFile.AddField("path", MakeUnquotedPathString(BuildTool.RootDirectory, EPathType.Absolute));
						}
						WorkspaceFile.EndObject();
					}
				}
				WorkspaceFile.EndArray();
			}

			WorkspaceFile.BeginObject("settings");
			{
				WorkspaceFile.AddField("typescript.tsc.autoDetect", "off");
			}
			WorkspaceFile.EndObject();
			
			WorkspaceFile.BeginObject("extensions");
			{
				// extensions is a set of recommended extensions that a user should install.
				// Adding this section aids discovery of extensions which are helpful to have installed for development.
				WorkspaceFile.BeginArray("recommendations");
				{
					WorkspaceFile.AddUnnamedField("ms-vscode.cpptools");
					WorkspaceFile.AddUnnamedField("ms-dotnettools.csharp");

					// If the platform we run the generator on uses mono, there are additional debugging extensions to add.
					if (Utils.IsRunningOnMono)
					{
						WorkspaceFile.AddUnnamedField("vadimcn.vscode-lldb");
						WorkspaceFile.AddUnnamedField("ms-vscode.mono-debug");
					}
				}
				WorkspaceFile.EndArray();
			}
			WorkspaceFile.EndObject();

			WorkspaceFile.EndRootObject();

			string WorkspaceName = bForeignProject ? GameProjectName : "MyEngine";
			WorkspaceFile.Write(FileReference.Combine(MasterProjectPath, WorkspaceName + ".code-workspace"));
		}

        private void GetExcludePathsCPP(DirectoryReference BaseDir, List<string> PathsToExclude)
		{
			string[] DirWhiteList = { "Binaries", "Build", "Config", "Plugins", "Source", "Private", "Public", "Classes", "Resources" };
			foreach (DirectoryReference SubDir in DirectoryReference.EnumerateDirectories(BaseDir, "*", SearchOption.TopDirectoryOnly))
			{
				if (Array.Find(DirWhiteList, Dir => Dir == SubDir.GetDirectoryName()) == null)
				{
					string NewSubDir = SubDir.ToString();
					if (!PathsToExclude.Contains(NewSubDir))
					{
						PathsToExclude.Add(NewSubDir);
					}
				}
			}
		}

		private void GetExcludePathsCSharp(string BaseDir, List<string> PathsToExclude)
		{
			string[] BlackList =
			{
				"obj",
				"bin"
			};

			foreach (string BlackListDir in BlackList)
			{
				string ExcludePath = Path.Combine(BaseDir, BlackListDir);
				if (!PathsToExclude.Contains(ExcludePath))
				{
					PathsToExclude.Add(ExcludePath);
				}
			}
		}
	}
}
