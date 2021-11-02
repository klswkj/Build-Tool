using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	abstract class ISPCToolChain : ToolChain
	{
		// Get CPU Instruction set targets for ISPC.
		
		// <param name="Platform">Which OS platform to target.</param>
		// <param name="Arch">Which architecture inside an OS platform to target. Only used for Android currently.</param>
		// <returns>List of instruction set targets passed to ISPC compiler</returns>
		public virtual List<string> GetISPCCompileTargets(BuildTargetPlatform Platform, string Arch)
		{
			List<string> ISPCTargets = new List<string>();

			if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Windows) ||
				(BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Unix) && Platform != BuildTargetPlatform.LinuxAArch64) ||
				Platform == BuildTargetPlatform.Mac)
			{
				ISPCTargets.AddRange(new string[] { "avx512skx-i32x8", "avx2", "avx", "sse4", "sse2" });
			}
			else if (Platform == BuildTargetPlatform.LinuxAArch64)
			{
				ISPCTargets.AddRange(new string[] { "neon" });
			}
			else if (Platform == BuildTargetPlatform.Android || Platform == BuildTargetPlatform.Lumin)
			{
				switch (Arch)
				{
					case "-armv7": ISPCTargets.Add("neon"); break; // Assumes NEON is in use
					case "-arm64": ISPCTargets.Add("neon"); break;
					case "-x86": ISPCTargets.AddRange(new string[] { "sse4", "sse2" }); break;
					case "-x64": ISPCTargets.AddRange(new string[] { "sse4", "sse2" }); break;
					default: Log.TraceWarning("Invalid Android architecture for ISPC. At least one architecture (armv7, x86, etc) needs to be selected in the project settings to build"); break;
				}
			}
			else
			{
				Log.TraceWarning("Unsupported ISPC platform target!");
			}

			return ISPCTargets;
		}

		
		// Get OS target for ISPC.
		public virtual string GetISPCOSTarget(BuildTargetPlatform Platform)
		{
			string ISPCOS = "";

			if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Windows))
			{
				ISPCOS += "windows";
			}
			else if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Unix))
			{
				ISPCOS += "linux";
			}
			else if (Platform == BuildTargetPlatform.Android || Platform == BuildTargetPlatform.Lumin)
			{
				ISPCOS += "android";
			}
			else if (Platform == BuildTargetPlatform.Mac)
			{
				ISPCOS += "macos";
			}
			else
			{
				Log.TraceWarning("Unsupported ISPC platform target!");
			}

			return ISPCOS;
		}

		
		// Get CPU architecture target for ISPC.
		
		// <param name="Platform">Which OS platform to target.</param>
		// <param name="Arch">Which architecture inside an OS platform to target. Only used for Android currently.</param>
		// <returns>Arch string passed to ISPC compiler</returns>
		public virtual string GetISPCArchTarget(BuildTargetPlatform Platform, string Arch)
		{
			string ISPCArch = "";

			if ((BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Windows) && Platform != BuildTargetPlatform.Win32) ||
				(BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Unix) && Platform != BuildTargetPlatform.LinuxAArch64) ||
				Platform == BuildTargetPlatform.Mac)
			{
				ISPCArch += "x86-64";
			}
			else if (Platform == BuildTargetPlatform.Win32)
			{
				ISPCArch += "x86";
			}
			else if (Platform == BuildTargetPlatform.LinuxAArch64)
			{
				ISPCArch += "aarch64";
			}
			else if (Platform == BuildTargetPlatform.Android || Platform == BuildTargetPlatform.Lumin)
			{
				switch (Arch)
				{
					case "-armv7": ISPCArch += "arm";     break; // Assumes NEON is in use
					case "-arm64": ISPCArch += "aarch64"; break;
					case "-x86":   ISPCArch += "x86";     break;
					case "-x64":   ISPCArch += "x86-64";  break;
					default: Log.TraceWarning("Invalid Android architecture for ISPC. At least one architecture (armv7, x86, etc) needs to be selected in the project settings to build"); break;
				}
			}
			else
			{
				Log.TraceWarning("Unsupported ISPC platform target!");
			}

			return ISPCArch;
		}

		// Get host compiler path for ISPC.
		public virtual string GetISPCHostCompilerPath(BuildTargetPlatform Platform)
		{
			string ISPCCompilerPathCommon = Path.Combine(BuildTool.EngineSourceThirdPartyDirectory.FullName, "IntelISPC", "bin");
            string ExeExtension = ".exe";

            string ISPCArchitecturePath;

            if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Windows))
            {
                ISPCArchitecturePath = "Windows";
            }
            else if (Platform == BuildTargetPlatform.Linux)
            {
                ISPCArchitecturePath = "Linux";
                ExeExtension = "";
            }
            else if (Platform == BuildTargetPlatform.Mac)
            {
                ISPCArchitecturePath = "Mac";
                ExeExtension = "";
            }
            else
            {
                Log.TraceWarning("Unsupported ISPC host!");
                throw new BuildException("Unsupported ISPC host!");
            }

            return Path.Combine(ISPCCompilerPathCommon, ISPCArchitecturePath, "ispc" + ExeExtension);
		}

		// Get object file suffix for ISPC.
		// <param name="Platform">Which OS build platform is running on.</param>
		// <returns>Object file suffix</returns>
		public virtual string GetISPCObjectFileSuffix(BuildTargetPlatform Platform)
		{
			string Suffix = "";

			if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Windows))
			{
				Suffix += ".obj";
			}
			else if (BuildPlatform.IsPlatformInGroup(Platform, BuildPlatformGroup.Unix) ||
					Platform == BuildTargetPlatform.Mac ||
					Platform == BuildTargetPlatform.Android ||
					Platform == BuildTargetPlatform.Lumin)
			{
				Suffix += ".o";
			}
			else
			{
				Log.TraceWarning("Unsupported ISPC platform target!");
				throw new BuildException("Unsupported ISPC platform target!");
			}

			return Suffix;
		}

		// Implicit SPMD Program Compiler (Intel® ISPC)
		public override CPPOutput GenerateOnlyISPCHeaders
		(
			CppCompileEnvironment CompileEnvironment,
			List<FileItem>        InputFiles, 
			DirectoryReference    OutputDir, 
			IActionGraphBuilder   Graph
		)
		{
			CPPOutput Result = new CPPOutput();

			if(!CompileEnvironment.bCompileISPC)
			{
				return Result;
			}

			List<string> CompileTargets = GetISPCCompileTargets(CompileEnvironment.Platform, null);

			foreach (FileItem IterISPCFile in InputFiles)
			{
				Action CompileAction = Graph.CreateAction(ActionType.Compile);

				CompileAction.CommandDescription  = "Compile";
				CompileAction.WorkingDirectory    = BuildTool.EngineSourceDirectory;
				CompileAction.CommandPath         = new FileReference(GetISPCHostCompilerPath(BuildHostPlatform.Current.Platform));
				CompileAction.StatusDescription   = Path.GetFileName(IterISPCFile.AbsolutePath);
				CompileAction.bCanExecuteRemotely = false;                                                // Disable remote execution to workaround mismatched case on XGE
				CompileAction.CommandArguments    = String.Format("\"{0}\" ", IterISPCFile.AbsolutePath); // Add the ISPC obj file as a prerequisite of the action.

				// Add the ISPC h file to the produced item list.
				FileItem ISPCIncludeHeaderFile = FileItem.GetItemByFileReference(
					FileReference.Combine(
						OutputDir,
						Path.GetFileName(IterISPCFile.AbsolutePath) + ".generated.dummy.h"
						));

				List<string> Arguments = new List<string>
				{
					// Add the ISPC file to be compiled.
					String.Format("-h \"{0}\"", ISPCIncludeHeaderFile),

					// Build target triplet
					String.Format("--target-os={0}", GetISPCOSTarget(CompileEnvironment.Platform)),
					String.Format("--arch={0}", GetISPCArchTarget(CompileEnvironment.Platform, null))
				};
				{
					// Build target string. No comma on last
					string TargetString = "";
					foreach (string Target in CompileTargets)
					{
						if (Target == CompileTargets[CompileTargets.Count - 1]) // .Last()
						{
							TargetString += Target;
						}
						else
						{
							TargetString += Target + ",";
						}
					}
					Arguments.Add(String.Format("--target={0}", TargetString));
				}

				// PIC is needed for modular builds except on Windows
				if ((CompileEnvironment.bIsBuildingDLL ||
					CompileEnvironment.bIsBuildingLibrary) &&
					!BuildPlatform.IsPlatformInGroup(CompileEnvironment.Platform, BuildPlatformGroup.Windows))
				{
					Arguments.Add("--pic");
				}

				// Include paths. Don't use AddIncludePath() here, since it uses the full path and exceeds the max command line length.
				// Because ISPC response files don't support white space in arguments, paths with white space need to be passed to the command line directly.
				foreach (DirectoryReference IncludePath in CompileEnvironment.UserIncludePaths)
				{
					Arguments.Add(String.Format("-I\"{0}\"", IncludePath));
				}

				// System include paths.
				foreach (DirectoryReference SystemIncludePath in CompileEnvironment.SystemIncludePaths)
				{
					Arguments.Add(String.Format("-I\"{0}\"", SystemIncludePath));
				}

				// Generate the included header dependency list
				if (CompileEnvironment.bGenerateDependenciesFile)
				{
					FileItem DependencyListFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(IterISPCFile.AbsolutePath) + ".txt"));
					Arguments.Add(String.Format("-MMM \"{0}\"", DependencyListFile.AbsolutePath.Replace('\\', '/')));
					CompileAction.DependencyListFile = DependencyListFile;
					CompileAction.ProducedItems.Add(DependencyListFile);
				}

				CompileAction.ProducedItems.Add(ISPCIncludeHeaderFile);

				FileReference ResponseFileName = new FileReference(ISPCIncludeHeaderFile.AbsolutePath + ".response");
				FileItem ResponseFileItem = Graph.CreateIntermediateTextFile(ResponseFileName, Arguments.Select(x => StringUtils.ExpandVariables(x)));
				CompileAction.CommandArguments += String.Format("@\"{0}\"", ResponseFileName);
				CompileAction.PrerequisiteItems.Add(ResponseFileItem);

				// Add the source file and its included files to the prerequisite item list.
				CompileAction.PrerequisiteItems.Add(IterISPCFile);

				FileItem ISPCFinalHeaderFile = FileItem.GetItemByFileReference(
					FileReference.Combine(
						OutputDir,
						Path.GetFileName(IterISPCFile.AbsolutePath) + ".generated.h"
						)
					);

				// Fix interrupted build issue by copying header after generation completes
				FileReference SourceFile = ISPCIncludeHeaderFile.FileDirectory; // *.Response
				FileReference TargetFile = ISPCFinalHeaderFile.FileDirectory;   // *.generated.h

				FileItem SourceFileItem = FileItem.GetItemByFileReference(SourceFile);
				FileItem TargetFileItem = FileItem.GetItemByFileReference(TargetFile);

				Action CopyAction = Graph.CreateAction(ActionType.BuildProject);
				CopyAction.CommandDescription = "Copy";
				CopyAction.CommandPath = BuildHostPlatform.Current.ShellPath;

				if (BuildHostPlatform.Current.ShellType == ShellType.Cmd)
				{
					CopyAction.CommandArguments = String.Format("/C \"copy /Y \"{0}\" \"{1}\" 1>nul\"", SourceFile, TargetFile);
				}
				else
				{
					CopyAction.CommandArguments = String.Format("-c 'cp -f \"{0}\" \"{1}\"'", SourceFile.FullName, TargetFile.FullName);
				}

				CopyAction.PrerequisiteItems.Add(SourceFileItem);
				CopyAction.ProducedItems.Add(TargetFileItem);
				CopyAction.WorkingDirectory               = BuildTool.EngineSourceDirectory;
				CopyAction.StatusDescription              = TargetFileItem.FileDirectory.GetFileName();
				CopyAction.bCanExecuteRemotely            = false;
				CopyAction.bShouldOutputStatusDescription = false;

				Result.ISPCGeneratedHeaderFiles.Add(TargetFileItem);

				Log.TraceVerbose("   ISPC Generating Header " + CompileAction.StatusDescription + ": \"" + CompileAction.CommandPath + "\"" + CompileAction.CommandArguments);
			} // End foreach (FileItem ISPCFile in InputFiles)

			return Result;
		}

		public override CPPOutput CompileISPCFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InISPCFiles, DirectoryReference OutputDir, IActionGraphBuilder GraphBuilder)
		{
			CPPOutput Result = new CPPOutput();

			if (!CompileEnvironment.bCompileISPC)
			{
				return Result;
			}

			List<string> CompileTargets = GetISPCCompileTargets(CompileEnvironment.Platform, null);

			foreach (FileItem IterISPCFile in InISPCFiles)
			{
				Action CompileAction = GraphBuilder.CreateAction(ActionType.Compile);
				CompileAction.CommandDescription = "Compile";
				CompileAction.WorkingDirectory   = BuildTool.EngineSourceDirectory;
				CompileAction.CommandPath        = new FileReference(GetISPCHostCompilerPath(BuildHostPlatform.Current.Platform));
				CompileAction.StatusDescription  = Path.GetFileName(IterISPCFile.AbsolutePath);

				// Disable remote execution to workaround mismatched case on XGE
				CompileAction.bCanExecuteRemotely = false;

				List<string> Arguments = new List<string>
				{
					// Add the ISPC file to be compiled.
					String.Format(" \"{0}\"", IterISPCFile.AbsolutePath)
				};

				List<FileItem> CompiledISPCObjFiles = new List<FileItem>(); // 여기가 주요 목표
				string TargetString = "";

				foreach (string Target in CompileTargets)
				{
					string ObjTarget = Target;

					if (Target.Contains("-"))
					{
						// Remove lane width and gang size from obj file name
						ObjTarget = Target.Split('-')[0];
					}

					FileItem CompiledISPCObjFile;

					if (1 < CompileTargets.Count)
					{
						CompiledISPCObjFile = FileItem.GetItemByFileReference
						(
							FileReference.Combine
							(
								OutputDir,
								Path.GetFileName(IterISPCFile.AbsolutePath) + "_" + ObjTarget + GetISPCObjectFileSuffix(CompileEnvironment.Platform)
							)
						);
					}
					else
					{
						CompiledISPCObjFile 
							= FileItem.GetItemByFileReference
							(
								FileReference.Combine
								(
									OutputDir,
									Path.GetFileName(IterISPCFile.AbsolutePath) + GetISPCObjectFileSuffix(CompileEnvironment.Platform)
							    )
							);
					}

					// Add the ISA specific ISPC obj files to the produced item list.
					CompiledISPCObjFiles.Add(CompiledISPCObjFile);

					// Build target string. No comma on last
					if (Target == CompileTargets[CompileTargets.Count - 1]) // .Last()
					{
						TargetString += Target;
					}
					else
					{
						TargetString += Target + ",";
					}
				}

				// Add the common ISPC obj file to the produced item list.
				// ISA = Instructure Set Architecture
				FileItem CompiledISPCObjFileNoISA = FileItem.GetItemByFileReference
				(
					FileReference.Combine
					(
						OutputDir,
						Path.GetFileName(IterISPCFile.AbsolutePath) + GetISPCObjectFileSuffix(CompileEnvironment.Platform)
					)
				);

				CompiledISPCObjFiles.Add(CompiledISPCObjFileNoISA);

				// Add the output ISPC obj file
				Arguments.Add(String.Format("-o \"{0}\"", CompiledISPCObjFileNoISA));

				// Build target triplet
				Arguments.Add(String.Format("--target-os=\"{0}\"", GetISPCOSTarget(CompileEnvironment.Platform)));
				Arguments.Add(String.Format("--arch=\"{0}\"",      GetISPCArchTarget(CompileEnvironment.Platform, null)));
				Arguments.Add(String.Format("--target=\"{0}\"",    TargetString));

				if (CompileEnvironment.Configuration == CppConfiguration.Debug)
				{
					Arguments.Add("-g -O0");
				}
				else
				{
					Arguments.Add("-O2");
				}

				// PIC is needed for modular builds except on Windows
				if ((CompileEnvironment.bIsBuildingDLL || 
					CompileEnvironment.bIsBuildingLibrary) &&
					!BuildPlatform.IsPlatformInGroup(CompileEnvironment.Platform, BuildPlatformGroup.Windows))
				{
					Arguments.Add("--pic");
				}

				// Include paths. Don't use AddIncludePath() here, since it uses the full path and exceeds the max command line length.
				foreach (DirectoryReference IncludePath in CompileEnvironment.UserIncludePaths)
				{
					Arguments.Add(String.Format("-I\"{0}\"", IncludePath));
				}

				// System include paths.
				foreach (DirectoryReference SystemIncludePath in CompileEnvironment.SystemIncludePaths)
				{
					Arguments.Add(String.Format("-I\"{0}\"", SystemIncludePath));
				}

				// Preprocessor definitions.
				foreach (string Definition in CompileEnvironment.Definitions)
				{
					Arguments.Add(String.Format("-D\"{0}\"", Definition));
				}

				// Consume the included header dependency list
				if (CompileEnvironment.bGenerateDependenciesFile)
				{
					FileItem DependencyListFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(IterISPCFile.AbsolutePath) + ".txt"));
					CompileAction.DependencyListFile = DependencyListFile;
					CompileAction.PrerequisiteItems.Add(DependencyListFile);
				}

				CompileAction.ProducedItems.AddRange(CompiledISPCObjFiles);

				Result.ObjectFiles.AddRange(CompiledISPCObjFiles); ///////////// ObjectFiles가 Return된다고 봐야함.

				FileReference ResponseFileName = new FileReference(CompiledISPCObjFileNoISA.AbsolutePath + ".response");
				FileItem ResponseFileItem = GraphBuilder.CreateIntermediateTextFile(ResponseFileName, Arguments.Select(x => StringUtils.ExpandVariables(x)));

				CompileAction.CommandArguments = " @\"" + ResponseFileName + "\"";
				CompileAction.PrerequisiteItems.Add(ResponseFileItem);
				CompileAction.PrerequisiteItems.Add(IterISPCFile); // Add the source file and its included files to the prerequisite item list.

				Log.TraceVerbose("   ISPC Compiling " + CompileAction.StatusDescription + ": \"" + CompileAction.CommandPath + "\"" + CompileAction.CommandArguments);
			}

			return Result;
		}
	}
}
