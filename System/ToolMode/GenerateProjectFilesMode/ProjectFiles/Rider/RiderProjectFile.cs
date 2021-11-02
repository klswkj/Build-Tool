// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	internal class RiderProjectFile : ProjectFile
	{
		public DirectoryReference RootPath;
		public HashSet<TargetType> TargetTypes;
		public CommandLineArguments Arguments;

		public RiderProjectFile(FileReference InProjectFilePath) : base(InProjectFilePath)
		{
		}

		
		// Write project file info in JSON file.
		// For every combination of <c>UnrealTargetPlatform</c>, <c>UnrealTargetConfiguration</c> and <c>TargetType</c>
		// will be generated separate JSON file.
		// Project file will be stored:
		// For UE4:  {UE4Root}/Engine/Intermediate/ProjectFiles/.Rider/{Platform}/{Configuration}/{TargetType}/{ProjectName}.json
		// For game: {GameRoot}/Intermediate/ProjectFiles/.Rider/{Platform}/{Configuration}/{TargetType}/{ProjectName}.json
		
		// <remarks>
		// * <c>UnrealTargetPlatform.Win32</c> will be always ignored.
		// * <c>TargetType.Editor</c> will be generated for current platform only and will ignore <c>UnrealTargetConfiguration.Test</c> and <c>UnrealTargetConfiguration.Shipping</c> configurations
		// * <c>TargetType.Program</c>  will be generated for current platform only and <c>UnrealTargetConfiguration.Development</c> configuration only 
		// </remarks>
		// <param name="InPlatforms"></param>
		// <param name="InConfigurations"></param>
		// <param name="PlatformProjectGenerators"></param>
		// <returns></returns>
		public override bool WriteProjectFile(List<UnrealTargetPlatform> InPlatforms,
			List<UnrealTargetConfiguration> InConfigurations,
			PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			string ProjectName = ProjectFilePath.GetFileNameWithoutAnyExtensions();
			DirectoryReference ProjectRootFolder = DirectoryReference.Combine(RootPath, ".Rider");
			List<Tuple<FileReference, BuildTarget>> FileToTarget = new List<Tuple<FileReference, BuildTarget>>();
			foreach (UnrealTargetPlatform Platform in InPlatforms)
			{
				foreach (UnrealTargetConfiguration Configuration in InConfigurations)
				{
					foreach (ProjectTarget ProjectTarget in ProjectTargets)
					{
						if (TargetTypes.Any() && !TargetTypes.Contains(ProjectTarget.TargetRules.Type)) continue;

						// Skip Programs for all configs except for current platform + Development configuration
						if (ProjectTarget.TargetRules.Type == TargetType.Program && (BuildHostPlatform.Current.Platform != Platform || Configuration != UnrealTargetConfiguration.Development))
						{
							continue;
						}

						// Skip Editor for all platforms except for current platform
						if (ProjectTarget.TargetRules.Type == TargetType.Editor && (BuildHostPlatform.Current.Platform != Platform || (Configuration == UnrealTargetConfiguration.Test || Configuration == UnrealTargetConfiguration.Shipping)))
						{
							continue;
						}
						
						DirectoryReference ConfigurationFolder = DirectoryReference.Combine(ProjectRootFolder, Platform.ToString(), Configuration.ToString());

						DirectoryReference TargetFolder =
							DirectoryReference.Combine(ConfigurationFolder, ProjectTarget.TargetRules.Type.ToString());

						string DefaultArchitecture = BuildPlatform
							.GetBuildPlatform(Platform)
							.GetDefaultArchitecture(ProjectTarget.UnrealProjectFilePath);
						BuildTargetDescriptor TargetDesc = new BuildTargetDescriptor(ProjectTarget.UnrealProjectFilePath, ProjectTarget.Name,
							Platform, Configuration, DefaultArchitecture, Arguments);
						try
						{
							BuildTarget BuildTarget = BuildTarget.CreateNewBuildTarget(TargetDesc, false, false);
						
							FileReference OutputFile = FileReference.Combine(TargetFolder, $"{ProjectName}.json");
							FileToTarget.Add(Tuple.Create(OutputFile, BuildTarget));
						}
						catch(Exception Ex)
						{
							Log.TraceWarning("Exception while generating include data for Target:{0}, Platform: {1}, Configuration: {2}", TargetDesc.Name, Platform.ToString(), Configuration.ToString());
							Log.TraceWarning(Ex.ToString());
						}
					}
				}
			}
			foreach (Tuple<FileReference,BuildTarget> tuple in FileToTarget)
			{
				SerializeTarget(tuple.Item1, tuple.Item2);
			}
			
			return true;
		}

		private static void SerializeTarget(FileReference OutputFile, BuildTarget BuildTarget)
		{
			DirectoryReference.CreateDirectory(OutputFile.Directory);
			using (JsonWriter Writer = new JsonWriter(OutputFile))
			{
				ExportTarget(BuildTarget, Writer);
			}
		}

		
		// Write a Target to a JSON writer. Is array is empty, don't write anything
		
		// <param name="Target"></param>
		// <param name="Writer">Writer for the array data</param>
		private static void ExportTarget(BuildTarget Target, JsonWriter Writer)
		{
			Writer.WriteObjectStart();

			Writer.WriteValue("Name", Target.TargetName);
			Writer.WriteValue("Configuration", Target.Configuration.ToString());
			Writer.WriteValue("Platform", Target.Platform.ToString());
			Writer.WriteValue("TargetFile", Target.TargetRulesFile.FullName);
			if (Target.ProjectFile != null)
			{
				Writer.WriteValue("ProjectFile", Target.ProjectFile.FullName);
			}
			
			ExportEnvironmentToJson(Target, Writer);
			
			if(Target.AllApplicationBuildBinaries.Any())
			{
				Writer.WriteArrayStart("Binaries");
				foreach (BuildBinary Binary in Target.AllApplicationBuildBinaries)
				{
					Writer.WriteObjectStart();
					ExportBinary(Binary, Writer);
					Writer.WriteObjectEnd();
				}
				Writer.WriteArrayEnd();
			}
			
			CppCompileEnvironment GlobalCompileEnvironment = Target.CreateCppCompileEnvironment();
			HashSet<string> ModuleNames = new HashSet<string>();
			Writer.WriteObjectStart("Modules");
			foreach (BuildBinary Binary in Target.AllApplicationBuildBinaries)
			{
				CppCompileEnvironment BinaryCompileEnvironment = Binary.OnlyCopyBuildingType(GlobalCompileEnvironment);
				foreach (BuildModule Module in Binary.LinkTogetherModules)
				{
					if(ModuleNames.Add(Module.ModuleRulesFileName))
					{
						Writer.WriteObjectStart(Module.ModuleRulesFileName);
						ExportModule(Module, Binary.OutputDir, Target.GetExecutableDir(), Writer);
						BuildModuleCPP ModuleCpp = Module as BuildModuleCPP;
						if (ModuleCpp != null)
						{
							CppCompileEnvironment ModuleCompileEnvironment = ModuleCpp.CreateCompileEnvironmentWithPCHAndForceIncludes(Target.Rules, BinaryCompileEnvironment);
							ExportModuleCpp(ModuleCpp, ModuleCompileEnvironment, Writer);
						}
						Writer.WriteObjectEnd();
					}
				}
			}
			Writer.WriteObjectEnd();
			
			ExportPluginsFromTarget(Target, Writer);
			
			Writer.WriteObjectEnd();
		}

		private static void ExportModuleCpp(BuildModuleCPP ModuleCPP, CppCompileEnvironment ModuleCompileEnvironment, JsonWriter Writer)
		{
			Writer.WriteValue("GeneratedCodeDirectory", ModuleCPP.GeneratedCodeDirectory != null ? ModuleCPP.GeneratedCodeDirectory.FullName : string.Empty);
			
			if (ModuleCompileEnvironment.PrecompiledHeaderIncludeFilename != null)
			{
				string CorrectFilePathPch;
				if(ExtractWrappedIncludeFile(ModuleCompileEnvironment.PrecompiledHeaderIncludeFilename, out CorrectFilePathPch))
					Writer.WriteValue("SharedPCHFilePath", CorrectFilePathPch);
			}
		}

		private static bool ExtractWrappedIncludeFile(FileSystemReference FileRef, out string CorrectFilePathPch)
		{
			CorrectFilePathPch = "";
			try
			{
				using (StreamReader Reader = new StreamReader(FileRef.FullName))
				{
					string Line = Reader.ReadLine();
					if (Line != null)
					{
						CorrectFilePathPch = Line.Substring("// PCH for ".Length).Trim();
						return true;
					}
				}
			}
			finally
			{
				Log.TraceVerbose("Couldn't extract path to PCH from {0}", FileRef);
			}
			return false;
		}

		
		// Write a Module to a JSON writer. If array is empty, don't write anything
		
		// <param name="BinaryOutputDir"></param>
		// <param name="TargetOutputDir"></param>
		// <param name="Writer">Writer for the array data</param>
		// <param name="Module"></param>
		private static void ExportModule(BuildModule Module, DirectoryReference BinaryOutputDir, DirectoryReference TargetOutputDir, JsonWriter Writer)
		{
			Writer.WriteValue("Name", Module.ModuleRulesFileName);
			Writer.WriteValue("Directory", Module.ModuleDirectory.FullName);
			Writer.WriteValue("Rules", Module.RulesFile.FullName);
			Writer.WriteValue("PCHUsage", Module.ModuleRulesForThis.PCHUsage.ToString());

			if (Module.ModuleRulesForThis.PrivatePCHHeaderFile != null)
			{
				Writer.WriteValue("PrivatePCH", FileReference.Combine(Module.ModuleDirectory, Module.ModuleRulesForThis.PrivatePCHHeaderFile).FullName);
			}

			if (Module.ModuleRulesForThis.SharedPCHHeaderFile != null)
			{
				Writer.WriteValue("SharedPCH", FileReference.Combine(Module.ModuleDirectory, Module.ModuleRulesForThis.SharedPCHHeaderFile).FullName);
			}

			ExportJsonModuleArray(Writer, "PublicDependencyModules", Module.PublicDependencyModules);
			ExportJsonModuleArray(Writer, "PublicIncludePathModules", Module.PublicIncludePathModules);
			ExportJsonModuleArray(Writer, "PrivateDependencyModules", Module.PrivateDependencyModules);
			ExportJsonModuleArray(Writer, "PrivateIncludePathModules", Module.PrivateIncludePathModules);
			ExportJsonModuleArray(Writer, "DynamicallyLoadedModules", Module.DynamicallyLoadedModules);

			ExportJsonStringArray(Writer, "PublicSystemIncludePaths", Module.PublicSystemIncludePaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, "PublicIncludePaths", Module.PublicIncludePaths.Select(x => x.FullName));
			
			ExportJsonStringArray(Writer, "LegacyPublicIncludePaths", Module.LegacyPublicIncludePaths.Select(x => x.FullName));
			
			ExportJsonStringArray(Writer, "PrivateIncludePaths", Module.PrivateIncludePaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, "PublicLibraryPaths", Module.PublicSystemLibraryPaths.Select(x => x.FullName));
			ExportJsonStringArray(Writer, "PublicAdditionalLibraries", Module.PublicSystemLibraries.Concat(Module.PublicAdditionalLibraries));
			ExportJsonStringArray(Writer, "PublicFrameworks", Module.PublicFrameworks);
			ExportJsonStringArray(Writer, "PublicWeakFrameworks", Module.PublicWeakFrameworks);
			ExportJsonStringArray(Writer, "PublicDelayLoadDLLs", Module.PublicDelayLoadDLLs);
			ExportJsonStringArray(Writer, "PublicDefinitions", Module.PublicDefinitions);
			ExportJsonStringArray(Writer, "PrivateDefinitions", Module.ModuleRulesForThis.PrivateDefinitions);
			ExportJsonStringArray(Writer, "ProjectDefinitions", /* TODO: Add method ShouldAddProjectDefinitions */ !Module.ModuleRulesForThis.bTreatAsEngineModule ? Module.ModuleRulesForThis.Target.ProjectDefinitions : new string[0]);
			ExportJsonStringArray(Writer, "ApiDefinitions", Module.GetEmptyApiMacros());
			Writer.WriteValue("ShouldAddLegacyPublicIncludePaths", Module.ModuleRulesForThis.bLegacyPublicIncludePaths);

			if(Module.ModuleRulesForThis.CircularlyReferencedDependentModules.Any())
			{
				Writer.WriteArrayStart("CircularlyReferencedModules");
				foreach (string ModuleName in Module.ModuleRulesForThis.CircularlyReferencedDependentModules)
				{
					Writer.WriteValue(ModuleName);
				}
				Writer.WriteArrayEnd();
			}
			
			if(Module.ModuleRulesForThis.RuntimeDependencies.Inner.Any())
			{
				// We don't use info from RuntimeDependencies for code analyzes (at the moment)
				// So we're OK with skipping some values if they are not presented
				Writer.WriteArrayStart("RuntimeDependencies");
				foreach (ModuleRules.RuntimeDependency RuntimeDependency in Module.ModuleRulesForThis.RuntimeDependencies.Inner)
				{
					Writer.WriteObjectStart();

					try
					{
						Writer.WriteValue("Path",
							Module.ExpandPathVariables(RuntimeDependency.Path, BinaryOutputDir, TargetOutputDir));
					}
					catch(BuildException buildException)
					{
						Log.TraceVerbose("Value {0} for module {1} will not be stored. Reason: {2}", "Path", Module.ModuleRulesFileName, buildException);	
					}
					
					if (RuntimeDependency.SourcePath != null)
					{
						try
						{
							Writer.WriteValue("SourcePath",
								Module.ExpandPathVariables(RuntimeDependency.SourcePath, BinaryOutputDir,
									TargetOutputDir));
						}
						catch(BuildException buildException)
						{
							Log.TraceVerbose("Value {0} for module {1} will not be stored. Reason: {2}", "SourcePath", Module.ModuleRulesFileName, buildException);	
						}
					}

					Writer.WriteValue("Type", RuntimeDependency.StagedType.ToString());
					
					Writer.WriteObjectEnd();
				}
				Writer.WriteArrayEnd();
			}
		}
		
		
		// Write an array of Modules to a JSON writer. If array is empty, don't write anything
		
		// <param name="Writer">Writer for the array data</param>
		// <param name="ArrayName">Name of the array property</param>
		// <param name="Modules">Sequence of Modules to write. May be null.</param>
		private static void ExportJsonModuleArray(JsonWriter Writer, string ArrayName, IEnumerable<BuildModule> Modules)
		{
			if (Modules == null || !Modules.Any()) return;
			
			Writer.WriteArrayStart(ArrayName);
			foreach (BuildModule Module in Modules)
			{
				Writer.WriteValue(Module.ModuleRulesFileName);
			}
			Writer.WriteArrayEnd();
		}
		
		
		// Write an array of strings to a JSON writer. Ifl array is empty, don't write anything
		
		// <param name="Writer">Writer for the array data</param>
		// <param name="ArrayName">Name of the array property</param>
		// <param name="Strings">Sequence of strings to write. May be null.</param>
		static void ExportJsonStringArray(JsonWriter Writer, string ArrayName, IEnumerable<string> Strings)
		{
			if (Strings == null || !Strings.Any()) return;
			
			Writer.WriteArrayStart(ArrayName);
			foreach (string String in Strings)
			{
				Writer.WriteValue(String);
			}
			Writer.WriteArrayEnd();
		}
		
		
		// Write uplugin content to a JSON writer
		
		// <param name="Plugin">Uplugin description</param>
		// <param name="Writer">JSON writer</param>
		private static void ExportPlugin(BuildPlugin Plugin, JsonWriter Writer)
		{
			Writer.WriteObjectStart(Plugin.Name);
			
			Writer.WriteValue("File", Plugin.File.FullName);
			Writer.WriteValue("Type", Plugin.Type.ToString());
			if(Plugin.Dependencies.Any())
			{
				Writer.WriteStringArrayField("Dependencies", Plugin.Dependencies.Select(it => it.Name));
			}
			if(Plugin.Modules.Any())
			{
				Writer.WriteStringArrayField("Modules", Plugin.Modules.Select(it => it.ModuleRulesFileName));
			}
			
			Writer.WriteObjectEnd();
		}
		
		
		// Setup plugins for Target and write plugins to JSON writer. Don't write anything if there are no plugins 
		
		// <param name="Target"></param>
		// <param name="Writer"></param>
		private static void ExportPluginsFromTarget(BuildTarget Target, JsonWriter Writer)
		{
			Target.SetupPlugins();
			if (!Target.BuildPlugins.Any()) return;
			
			Writer.WriteObjectStart("Plugins");
			foreach (BuildPlugin plugin in Target.BuildPlugins)
			{
				ExportPlugin(plugin, Writer);
			}
			Writer.WriteObjectEnd();
		}

		
		// Write information about this binary to a JSON file
		
		// <param name="Binary"></param>
		// <param name="Writer">Writer for this binary's data</param>
		private static void ExportBinary(BuildBinary Binary, JsonWriter Writer)
		{
			Writer.WriteValue("File", Binary.OutputFilePath.FullName);
			Writer.WriteValue("Type", Binary.Type.ToString());

			Writer.WriteArrayStart("Modules");
			foreach(BuildModule Module in Binary.LinkTogetherModules)
			{
				Writer.WriteValue(Module.ModuleRulesFileName);
			}
			Writer.WriteArrayEnd();
		}
		
		
		// Write C++ toolchain information to JSON writer
		
		// <param name="Target"></param>
		// <param name="Writer"></param>
		private static void ExportEnvironmentToJson(BuildTarget Target, JsonWriter Writer)
		{
			CppCompileEnvironment GlobalCompileEnvironment = Target.CreateCppCompileEnvironment();
			
			Writer.WriteArrayStart("EnvironmentIncludePaths");
			foreach (DirectoryReference Path in GlobalCompileEnvironment.UserIncludePaths)
			{
				Writer.WriteValue(Path.FullName);
			}
			foreach (DirectoryReference Path in GlobalCompileEnvironment.SystemIncludePaths)
			{
				Writer.WriteValue(Path.FullName);
			}
			
			// TODO: get corresponding includes for specific platforms
			if (BuildPlatform.IsPlatformInGroup(Target.Platform, UnrealPlatformGroup.Windows))
			{
				foreach (DirectoryReference Path in Target.Rules.WindowsPlatform.Environment.IncludePaths)
				{
					Writer.WriteValue(Path.FullName);
				}
			}
			Writer.WriteArrayEnd();
	
			Writer.WriteArrayStart("EnvironmentDefinitions");
			foreach (string Definition in GlobalCompileEnvironment.Definitions)
			{
				Writer.WriteValue(Definition);
			}
			Writer.WriteArrayEnd();
		}
	}
}