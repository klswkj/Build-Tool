using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// A module that is compiled from C++ code.
	internal sealed class BuildModuleCPP : BuildModule
	{
		// Stores a list of all source files, of different types
		public class InputFileCollection
		{
			public readonly List<FileItem> HeaderFiles     = new List<FileItem>();
			public readonly List<FileItem> ISPCHeaderFiles = new List<FileItem>(); // *.isph

			public readonly List<FileItem> CPPFiles  = new List<FileItem>();
			public readonly List<FileItem> CFiles    = new List<FileItem>();
			public readonly List<FileItem> CCFiles   = new List<FileItem>(); // g++?
			public readonly List<FileItem> MMFiles   = new List<FileItem>(); // Object-C with C++
			public readonly List<FileItem> RCFiles   = new List<FileItem>(); // Resource Compiler
			public readonly List<FileItem> ISPCFiles = new List<FileItem>(); // *.ispc
		}

		// The directory for this module's generated code
		public readonly DirectoryReference GeneratedCodeDirectory;

		// Set for modules that have generated code
		// UObject랑 관련
		public bool bAddGeneratedCodeIncludePath;

		// Wildcard matching the *.gen.cpp files for this module.  
		// If this is null then this module doesn't have any UHT-produced code.
		// UObject랑 관련.
		public string GeneratedCodeWildcard;

		// List of invalid include directives. 
		// These are buffered up and output before we start compiling.
		public List<string> InvalidIncludeDirectiveMessages;

		// Set of source directories referenced during a build
		HashSet<DirectoryReference> SourceDirectories;

		#region STATIC_FUNCTIONS

		// Determines if a module compile environment is compatible
		// with the given shared PCH compile environment
		private static bool IsCompatibleForSharedPCH(CppCompileEnvironment ModuleCompileEnvironment, CppCompileEnvironment CompileEnvironment)
		{
			if (ModuleCompileEnvironment.bOptimizeCode != CompileEnvironment.bOptimizeCode)
			{
				return false;
			}
			if (ModuleCompileEnvironment.bUseRTTI != CompileEnvironment.bUseRTTI)
			{
				return false;
			}
			if (ModuleCompileEnvironment.bEnableExceptions != CompileEnvironment.bEnableExceptions)
			{
				return false;
			}
			if (ModuleCompileEnvironment.ShadowVariableWarningLevel != CompileEnvironment.ShadowVariableWarningLevel)
			{
				return false;
			}
			if (ModuleCompileEnvironment.UnsafeTypeCastWarningLevel != CompileEnvironment.UnsafeTypeCastWarningLevel)
			{
				return false;
			}
			if (ModuleCompileEnvironment.bEnableUndefinedIdentifierWarnings != CompileEnvironment.bEnableUndefinedIdentifierWarnings)
			{
				return false;
			}

			return true;
		}

		// Gets the unique suffix for a shared PCH
		private static string GetSharedPCHExtension(CppCompileEnvironment SharedPCHCompileEnvironment, CppCompileEnvironment BaseCompileEnvironment)
		{
			string HeaderFileExtension = "";

			if (SharedPCHCompileEnvironment.bOptimizeCode != BaseCompileEnvironment.bOptimizeCode)
			{
				if (SharedPCHCompileEnvironment.bOptimizeCode)
				{
					HeaderFileExtension += Tag.Ext.Optimized;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NonOptimized;
				}
			}

			if (SharedPCHCompileEnvironment.bUseRTTI != BaseCompileEnvironment.bUseRTTI)
			{
				if (SharedPCHCompileEnvironment.bUseRTTI)
				{
					HeaderFileExtension += Tag.Ext.RTTI;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NonRTTI;
				}
			}

			if (SharedPCHCompileEnvironment.bEnableExceptions != BaseCompileEnvironment.bEnableExceptions)
			{
				if (SharedPCHCompileEnvironment.bEnableExceptions)
				{
					HeaderFileExtension += Tag.Ext.Exceptions;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NoExceptions;
				}
			}
			// 여기를 ShadowErrors로 끝내는게 아니고 ShadowVariable... 아니다
			// 이렇게 쓴게 최선인거 같음.
			if (SharedPCHCompileEnvironment.ShadowVariableWarningLevel != BaseCompileEnvironment.ShadowVariableWarningLevel)
			{
				if (SharedPCHCompileEnvironment.ShadowVariableWarningLevel == WarningLevel.Error)
				{
					HeaderFileExtension += Tag.Ext.ShadowErrors;
				}
				else if (SharedPCHCompileEnvironment.ShadowVariableWarningLevel == WarningLevel.Warning)
				{
					HeaderFileExtension += Tag.Ext.ShadowWarnings;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NoShadow;
				}
			}

			if (SharedPCHCompileEnvironment.UnsafeTypeCastWarningLevel != BaseCompileEnvironment.UnsafeTypeCastWarningLevel)
			{
				if (SharedPCHCompileEnvironment.UnsafeTypeCastWarningLevel == WarningLevel.Error)
				{
					HeaderFileExtension += Tag.Ext.TypeCastErrors;
				}
				else if (SharedPCHCompileEnvironment.UnsafeTypeCastWarningLevel == WarningLevel.Warning)
				{
					HeaderFileExtension += Tag.Ext.TypeCastWarnings;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NoTypeCast;
				}
			}

			if (SharedPCHCompileEnvironment.bEnableUndefinedIdentifierWarnings != BaseCompileEnvironment.bEnableUndefinedIdentifierWarnings)
			{
				if (SharedPCHCompileEnvironment.bEnableUndefinedIdentifierWarnings)
				{
					HeaderFileExtension += Tag.Ext.Undef;
				}
				else
				{
					HeaderFileExtension += Tag.Ext.NoUndef;
				}
			}

			return HeaderFileExtension;
		}

		// Copy settings from the module's compile environment into the environment for the shared PCH
		private static void CopySettingsForSharedPCH(CppCompileEnvironment ModuleCompileEnvironment, CppCompileEnvironment CompileEnvironment)
		{
			CompileEnvironment.bOptimizeCode                      = ModuleCompileEnvironment.bOptimizeCode;
			CompileEnvironment.bUseRTTI                           = ModuleCompileEnvironment.bUseRTTI;
			CompileEnvironment.bEnableExceptions                  = ModuleCompileEnvironment.bEnableExceptions;
			CompileEnvironment.ShadowVariableWarningLevel         = ModuleCompileEnvironment.ShadowVariableWarningLevel;
			CompileEnvironment.UnsafeTypeCastWarningLevel         = ModuleCompileEnvironment.UnsafeTypeCastWarningLevel;
			CompileEnvironment.bEnableUndefinedIdentifierWarnings = ModuleCompileEnvironment.bEnableUndefinedIdentifierWarnings;
		}

        private static CPPOutput CompileAdaptiveNonUnityFiles(ToolChain ToolChain, CppCompileEnvironment CompileEnvironment, List<FileItem> Files, DirectoryReference IntermediateDirectory, string ModuleName, IActionGraphBuilder Graph)
		{
			// Write all the definitions out to a separate file
			DefinitionsAddToForceIncludes(CompileEnvironment, IntermediateDirectory, "Adaptive", Graph);

			// Compile the files
			return ToolChain.CompileCPPFiles(CompileEnvironment, Files, IntermediateDirectory, ModuleName, Graph);
		}

		static CPPOutput CompileAdaptiveNonUnityFilesWithoutPCH(ToolChain ToolChain, CppCompileEnvironment CompileEnvironment, List<FileItem> Files, DirectoryReference IntermediateDirectory, string ModuleName, IActionGraphBuilder Graph)
		{
			// Disable precompiled headers
			CompileEnvironment.PCHAction = PCHAction.None;

			// Write all the definitions out to a separate file
			DefinitionsAddToForceIncludes(CompileEnvironment, IntermediateDirectory, "Adaptive", Graph);

			// Compile the files
			return ToolChain.CompileCPPFiles(CompileEnvironment, Files, IntermediateDirectory, ModuleName, Graph);
		}

		static CPPOutput CompileAdaptiveNonUnityFilesWithDedicatedPCH(ToolChain ToolChain, CppCompileEnvironment CompileEnvironment, List<FileItem> Files, DirectoryReference IntermediateDirectory, string ModuleName, IActionGraphBuilder Graph)
		{
			CPPOutput Output = new CPPOutput();
			foreach (FileItem File in Files)
			{
				// Build the contents of the wrapper file
				StringBuilder WrapperContents = new StringBuilder();
				using (StringWriter Writer = new StringWriter(WrapperContents))
				{
					Writer.WriteLine("// Dedicated PCH for {0}", File.AbsolutePath);
					Writer.WriteLine();
					WriteDefinitions(CompileEnvironment.Definitions, Writer);
					Writer.WriteLine();
					using (StreamReader Reader = new StreamReader(File.FileDirectory.FullName))
					{
						CppIncludeParser.CopyIncludeDirectives(Reader, Writer);
					}
				}

				// Write the PCH header
				FileReference DedicatedPCHLocation = FileReference.Combine(IntermediateDirectory, String.Format(Tag.OutputFile.PCH + ".Dedicated.{0}.h", File.FileDirectory.GetFileNameWithoutExtension()));
				FileItem DedicatedPchFile = Graph.CreateIntermediateTextFile(DedicatedPCHLocation, WrapperContents.ToString());

				// Create a new C++ environment to compile the PCH
				CppCompileEnvironment PchEnvironment = new CppCompileEnvironment(CompileEnvironment);
				PchEnvironment.Definitions.Clear();
				PchEnvironment.UserIncludePaths.Add(File.FileDirectory.Directory); // Need to be able to include headers in the same directory as the source file
				PchEnvironment.PCHAction = PCHAction.Create;
				PchEnvironment.PCHIncludeFilename = DedicatedPchFile.FileDirectory;

				// Create the action to compile the PCH file.
				CPPOutput PchOutput = ToolChain.CompileCPPFiles(PchEnvironment, new List<FileItem>() { DedicatedPchFile }, IntermediateDirectory, ModuleName, Graph);
				Output.ObjectFiles.AddRange(PchOutput.ObjectFiles);

				// Create a new C++ environment to compile the original file
				CppCompileEnvironment FileEnvironment = new CppCompileEnvironment(CompileEnvironment);
				FileEnvironment.Definitions.Clear();
				FileEnvironment.PCHAction = PCHAction.Include;
				FileEnvironment.PCHIncludeFilename = DedicatedPchFile.FileDirectory;
				FileEnvironment.PrecompiledHeaderFile = PchOutput.PCHFile;

				// Create the action to compile the PCH file.
				CPPOutput FileOutput = ToolChain.CompileCPPFiles(FileEnvironment, new List<FileItem>() { File }, IntermediateDirectory, ModuleName, Graph);
				Output.ObjectFiles.AddRange(FileOutput.ObjectFiles);
			}
			return Output;
		}

		// Creates a header file containing all the preprocessor definitions for a compile environment,
		// and force-include it. We allow a more flexible syntax for preprocessor definitions than is typically allowed on the command line
		// (allowing function macros or double-quote characters, for example).
		static void DefinitionsAddToForceIncludes
		(
			CppCompileEnvironment OutCompileEnvironment,
			DirectoryReference    DirectoryToCreateGeneratedFile,
			string                IncludedHeaderFileSuffix, // "Adaptvie" or null
			IActionGraphBuilder   ActionGraph               // NullActionGraph
		)
		{
			// Create Definitions.{Some Additional Options}.h
			// 하지만 거의 안붙음 => Defintions.h
			// 그 전에 SetPCHs에서 안지워져서 PCH
			if (0 < OutCompileEnvironment.Definitions.Count)
			{
				StringBuilder PrivateDefinitionsName = new StringBuilder(Tag.OutputFile.Definitions); // ...\$(Module)\Definitions.h

				if (IncludedHeaderFileSuffix.HasValue())
				{
					PrivateDefinitionsName.Append('.');
					PrivateDefinitionsName.Append(IncludedHeaderFileSuffix);
				}

				PrivateDefinitionsName.Append(Tag.Ext.Header);

				FileReference PrivateDefinitionsFile = FileReference.Combine(DirectoryToCreateGeneratedFile, PrivateDefinitionsName.ToString());

				using (StringWriter Writer = new StringWriter())
				{
					WriteDefinitions(OutCompileEnvironment.Definitions, Writer);
					OutCompileEnvironment.Definitions.Clear();

					FileItem PrivateDefinitionsFileItem = ActionGraph.CreateIntermediateTextFile(PrivateDefinitionsFile, Writer.ToString());
					OutCompileEnvironment.ForceIncludeFiles.Add(PrivateDefinitionsFileItem);
				}
			}
		}

        // Creates header files from ISPC for inclusion and adds them as dependencies.
        private static void CreateHeadersForISPC
		(
			ToolChain             ToolChain, 
			CppCompileEnvironment CompileEnvironment, 
			List<FileItem>        InputISPCFiles, 
			DirectoryReference    GeneratedDirectory, 
			IActionGraphBuilder   Graph
		)
		{
			CPPOutput Output = ToolChain.GenerateOnlyISPCHeaders(CompileEnvironment, InputISPCFiles, GeneratedDirectory, Graph);

			CompileEnvironment.AdditionalPrerequisites.AddRange(Output.ISPCGeneratedHeaderFiles);
			CompileEnvironment.UserIncludePaths.Add(GeneratedDirectory);
		}

        // Create a header file containing the module definitions, which also includes the PCH itself.
        // Including through another file is necessary on  Clang, since we get warnings about #pragma once otherwise,
        // but it also allows us to consistently define the preprocessor state on all platforms.
        private static FileItem CreatePCHWrapperFile
		(
            FileReference       OutputFile,
            IEnumerable<string> Definitions,
            FileItem            IncludedFile,
            IActionGraphBuilder Graph
		)
		{
			// Build the contents of the wrapper file
			StringBuilder AnyPCH = new StringBuilder();
			using (StringWriter Writer = new StringWriter(AnyPCH))
			{
				Writer.WriteLine("// PCH for {0}", IncludedFile.AbsolutePath);
				WriteDefinitions(Definitions, Writer);
				Writer.WriteLine(Tag.CppContents.Include + "\"{0}\"", IncludedFile.AbsolutePath.Replace('\\', '/'));

				// TODO : 
				// PCH for AbsolutePath로 주석 달고
				// Definitions 쓰고
				// 아래에 #include IncludeFile.AbsolutePath쓰기
			}

			// Create the item
			FileItem AnyPCHFile = Graph.CreateIntermediateTextFile(OutputFile, AnyPCH.ToString());

			// Touch it if the included file is newer, to make sure our timestamp dependency checking is accurate.
			if (AnyPCHFile.LastWriteTimeUtc < IncludedFile.LastWriteTimeUtc)
			{
				File.SetLastWriteTimeUtc(AnyPCHFile.AbsolutePath, DateTime.UtcNow);
				AnyPCHFile.ResetCachedInfo();
			}

			return AnyPCHFile;
		}

		// Write a list of macro definitions to an output file
		static void WriteDefinitions(IEnumerable<string> Definitions, TextWriter Writer)
		{
			foreach (string Definition in Definitions)
			{
				int EqualsIdx = Definition.IndexOf('=');
				if (EqualsIdx == -1)
				{
					Writer.WriteLine(Tag.CppContents.Define + "{0} 1", Definition);
				}
				else
				{
					Writer.WriteLine(Tag.CppContents.Define + "{0} {1}", Definition.Substring(0, EqualsIdx), Definition.Substring(EqualsIdx + 1));
				}
			}
		}

		public static bool ShouldEnableOptimization(ModuleRules.CodeOptimization Setting, TargetConfiguration TargetConfiguration, bool bIsEngineModule)
		{
			// Determine whether optimization should be enabled for a given target
			switch (Setting)
			{
				case ModuleRules.CodeOptimization.Never:
					return false;
				case ModuleRules.CodeOptimization.Default:
				case ModuleRules.CodeOptimization.InNonDebugBuilds:
					return TargetConfiguration != TargetConfiguration.Debug && 
						  (TargetConfiguration != TargetConfiguration.DebugGame || bIsEngineModule);
				case ModuleRules.CodeOptimization.InShippingBuildsOnly:
					return (TargetConfiguration == TargetConfiguration.Shipping);
				case ModuleRules.CodeOptimization.Always:
					return true;
				default:
#if DEBUG
#warning Don't expect to Optimize compiler make Look-up table for this switch statement.
					throw new BuildException("UnExpected CodeOptimization Option.");
#else
					return false;
#endif
			}
		}

		// Enumerates legacy include paths under a given base directory>
		static void EnumerateLegacyIncludePaths(DirectoryItem BaseDirectory, ReadOnlyHashSet<string> ExcludeNames, HashSet<DirectoryReference> LegacyPublicIncludePaths)
		{
			foreach (DirectoryItem SubDirectory in BaseDirectory.EnumerateSubDirectories())
			{
				if (!ExcludeNames.Contains(SubDirectory.Name))
				{
					LegacyPublicIncludePaths.Add(SubDirectory.FullDirectory);
					EnumerateLegacyIncludePaths(SubDirectory, ExcludeNames, LegacyPublicIncludePaths);
				}
			}
		}

		#endregion STATIC_FUNCTIONS
		protected override void GetReferencedDirectories(HashSet<DirectoryReference> Directories)
		{
			base.GetReferencedDirectories(Directories);

			if(!ModuleRule.bUsePrecompiled)
			{

				// SourceDirectories is Only Constructed In FindInputFiles()
				if (SourceDirectories == null)
				{
					throw new BuildException("GetReferencedDirectories() should not be called before building.");
				}
				Directories.UnionWith(SourceDirectories);
			}
		}

        // List of whitelisted circular dependencies. 
        // Please do NOT add new modules here;
		// refactor to allow the modules to be decoupled instead.
        private static readonly KeyValuePair<string, string>[] WhitelistedCircularDependencies =
		{
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.Landscape),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.UMG),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.GameplayTags),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.MaterialShaderQualitySettings),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.AudioMixer),
			new KeyValuePair<string, string>(Tag.Module.Engine.PacketHandlers.PacketHandler, Tag.Module.Engine.PacketHandlers.ReliabilityHandlerComponent),
			new KeyValuePair<string, string>(Tag.Module.EngineAndEditor.GameplayDebugger, Tag.Module.Engine.AIModule),
			new KeyValuePair<string, string>(Tag.Module.EngineAndEditor.GameplayDebugger, Tag.Module.Engine.GameplayTasks),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Engine.CinematicCamera),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.EngineAndEditor.CollisionAnalyzer),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.EngineAndEditor.LogVisualizer),
			new KeyValuePair<string, string>(Tag.Module.Engine.EngineModule, Tag.Module.Editor.Kismet),
			new KeyValuePair<string, string>(Tag.Module.Engine.Landscape, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Engine.Landscape, Tag.Module.EngineAndEditor.MaterialUtilities),
			new KeyValuePair<string, string>(Tag.Module.Editor.LocalizationDashBoard, Tag.Module.EngineAndEditor.LocalizationService),
			new KeyValuePair<string, string>(Tag.Module.Editor.LocalizationDashBoard, Tag.Module.Editor.MainFrame),
			new KeyValuePair<string, string>(Tag.Module.Editor.LocalizationDashBoard, Tag.Module.Editor.TranslationEditor),
			new KeyValuePair<string, string>(Tag.Module.Editor.Documentation, Tag.Module.EngineAndEditor.SourceControl),
			new KeyValuePair<string, string>(Tag.Module.Editor.EditorEd, Tag.Module.Editor.GraphEditor),
			new KeyValuePair<string, string>(Tag.Module.Editor.EditorEd, Tag.Module.Editor.Kismet),
			new KeyValuePair<string, string>(Tag.Module.Editor.EditorEd, Tag.Module.Editor.AudioEditor),
			new KeyValuePair<string, string>(Tag.Module.Editor.BlueprintGraph, Tag.Module.Editor.KismetCompiler),
			new KeyValuePair<string, string>(Tag.Module.Editor.BlueprintGraph, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Editor.BlueprintGraph, Tag.Module.Editor.GraphEditor),
			new KeyValuePair<string, string>(Tag.Module.Editor.BlueprintGraph, Tag.Module.Editor.Kismet),
			new KeyValuePair<string, string>(Tag.Module.Editor.BlueprintGraph, Tag.Module.Engine.CinematicCamera),
			new KeyValuePair<string, string>(Tag.Module.Editor.ConfigEditor, Tag.Module.Editor.PropertyEditor),
			new KeyValuePair<string, string>(Tag.Module.EngineAndEditor.SourceControl, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Editor.Kismet, Tag.Module.Editor.BlueprintGraph),
			new KeyValuePair<string, string>(Tag.Module.Editor.Kismet, Tag.Module.Editor.UMGEditor),
			new KeyValuePair<string, string>(Tag.Module.Editor.MovieSceneTools, Tag.Module.Editor.Sequencer),
			new KeyValuePair<string, string>(Tag.Module.Editor.Sequencer, Tag.Module.Editor.MovieSceneTools),
			new KeyValuePair<string, string>(Tag.Module.Engine.AIModule, Tag.Module.EngineAndEditor.AITestSuite),
			new KeyValuePair<string, string>(Tag.Module.Engine.GameplayTasks, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Editor.AnimGraph, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Editor.AnimGraph, Tag.Module.Editor.GraphEditor),
			new KeyValuePair<string, string>(Tag.Module.EngineAndEditor.MaterialUtilities, Tag.Module.Engine.Landscape),
			new KeyValuePair<string, string>(Tag.Module.Editor.HierarchicalLODOutliner, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Editor.PixelInspector, Tag.Module.Editor.EditorEd),
			new KeyValuePair<string, string>(Tag.Module.Plugins.GameplayAbilities.GameplayAbilitiesEditor, Tag.Module.Editor.BlueprintGraph),
            new KeyValuePair<string, string>(Tag.Module.Editor.EditorEd, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Editor.EditorEd, Tag.Module.Editor.VREditor),
            new KeyValuePair<string, string>(Tag.Module.Editor.LandscapeEditor, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Editor.LandscapeEditor, Tag.Module.Editor.VREditor),
            new KeyValuePair<string, string>(Tag.Module.Editor.FoliageEdit, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Editor.FoliageEdit, Tag.Module.Editor.VREditor),
            new KeyValuePair<string, string>(Tag.Module.Editor.MeshPaint, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Editor.MeshPaint, Tag.Module.Editor.VREditor),
            new KeyValuePair<string, string>(Tag.Module.Editor.MeshPaintMode, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Editor.MeshPaintMode, Tag.Module.Editor.VREditor),
            new KeyValuePair<string, string>(Tag.Module.Editor.Sequencer, Tag.Module.Editor.ViewportInteraction),
            new KeyValuePair<string, string>(Tag.Module.Engine.NavigationSystem, Tag.Module.Editor.EditorEd),
        };

		public BuildModuleCPP(ModuleRules Rules, DirectoryReference IntermediateDirectory, DirectoryReference GeneratedCodeDirectory)
			: base(Rules, IntermediateDirectory)
		{
			this.GeneratedCodeDirectory = GeneratedCodeDirectory;

			foreach (string Def in PublicDefinitions)
			{
				// ModuleRulesFileName = "Launch", Def = "UE_BUILD_DEVELOPMENT_WITH_DEBUGGAME=0"
				Log.TraceVerbose("Compile Env {0}: {1}", ModuleRuleFileName, Def);
			}

			foreach (string Def in Rules.PrivateDefinitions)
			{
				// ModuleRulesFileName = "Launch", [0] = "UE_BUILD_DEVELOPMENT_WITH_DEBUGGAME=0"
				Log.TraceVerbose("Compile Env {0}: {1}", ModuleRuleFileName, Def);
			}

			foreach(string CircularlyReferencedModuleName in Rules.CircularlyReferencedDependentModules)
			{
				if(!WhitelistedCircularDependencies.Any(x => x.Key == ModuleRuleFileName && x.Value == CircularlyReferencedModuleName))
				{
					Log.TraceWarning("Found reference between '{0}' and '{1}'. Support for circular references is being phased out; please do not introduce new ones.", ModuleRuleFileName, CircularlyReferencedModuleName);
				}
			}

			AddDefaultIncludePaths();
		}

		// Determines if a file is part of the given module
		// <returns>True if the file is part of this module</returns>
		public override bool ContainsFile(FileReference Location)
		{
			if (base.ContainsFile(Location))
			{
				return true;
			}
			if (GeneratedCodeDirectory != null && Location.IsUnderDirectory(GeneratedCodeDirectory))
			{
				return true;
			}
			return false;
		}

		// Add the default include paths for this module to its settings
		private void AddDefaultIncludePaths()
		{
			// Add the module's parent directory to the public include paths, so other modules may include headers from it explicitly.

			// [0] = {D:\UERelease\Engine\Source\Runtime\Launch}
			foreach (DirectoryReference ModuleDir in ModuleDirectories)
			{
				// [0] = {D:\UERelease\Engine\Source\Runtime}
				PublicIncludePaths.Add(ModuleDir.ParentDirectory);

				// Add the base directory to the legacy include paths.
				// [0] = {D:\UERelease\Engine\Source\Runtime\Launch}
				LegacyPublicIncludePaths.Add(ModuleDir);

				{
					// Add the 'classes' directory, if it exists
					// ClassesDirectory = {D:\UERelease\Engine\Source\Runtime\Launch\Classes}
					DirectoryReference ClassesDirectory = DirectoryReference.Combine(ModuleDir, Tag.Directory.Classes);
					if (DirectoryLookupCache.DirectoryExists(ClassesDirectory))
					{
						PublicIncludePaths.Add(ClassesDirectory);
					}
				}
				{
					// Add all the public directories
					// PublicDirectory = {D:\UERelease\Engine\Source\Runtime\Launch\Public}
					DirectoryReference PublicDirectory = DirectoryReference.Combine(ModuleDir, Tag.Directory.Public);
					if (DirectoryLookupCache.DirectoryExists(PublicDirectory))
					{
						PublicIncludePaths.Add(PublicDirectory);
						// ExcludeNames = (20) { Win32, HoloLens, ... }
						ReadOnlyHashSet<string> ExcludeNames = BuildPlatform.GetBuildPlatform(ModuleRule.Target.Platform).GetExcludedFolderNames();
						EnumerateLegacyIncludePaths(DirectoryItem.GetItemByDirectoryReference(PublicDirectory), ExcludeNames, LegacyPublicIncludePaths);
					}
				}
				{
					// Add all the public-only directories
					// PublicOnlyDirectory = {D:\UERelease\Engine\Source\Runtime\Launch\PublicOnly}
					DirectoryReference PublicOnlyDirectory = DirectoryReference.Combine(ModuleDir, Tag.Directory.PublicOnly);
					if (DirectoryLookupCache.DirectoryExists(PublicOnlyDirectory))
					{
						PublicIncludePaths.Add(PublicOnlyDirectory);
						ReadOnlyHashSet<string> ExcludeNames = BuildPlatform.GetBuildPlatform(ModuleRule.Target.Platform).GetExcludedFolderNames();
						EnumerateLegacyIncludePaths(DirectoryItem.GetItemByDirectoryReference(PublicOnlyDirectory), ExcludeNames, LegacyPublicIncludePaths);
					}
				}

				// Add the base private directory for this module
				// PrivateDirectory = {D:\UERelease\Engine\Source\Runtime\Launch\Private}
				DirectoryReference PrivateDirectory = DirectoryReference.Combine(ModuleDir, Tag.Directory.Private);
				if (DirectoryLookupCache.DirectoryExists(PrivateDirectory))
				{
					// [0] = {D:\UERelease\Engine\Source\Runtime\Launch\Private},
					// [1] = {D:\UERelease\Engine\Source\Developer\DerivedDataCache\Public}
					// 
					PrivateIncludePaths.Add(PrivateDirectory);
				}
			}
		}

        // Path to the precompiled manifest location
        public FileReference PrecompiledManifestLocation 
			=> FileReference.Combine(GeneratedDirectory, String.Format("{0}" + Tag.Ext.PreCompiled, ModuleRuleFileName));

        // Sets up the environment for compiling any module that includes the public interface of this module.
        public override void AddModuleToCompileEnvironment
		(
			BuildBinary                 SourceBinary,
			HashSet<DirectoryReference> IncludePaths,
			HashSet<DirectoryReference> SystemIncludePaths,
			List<string>                Definitions,
			List<BuildFramework>        AdditionalFrameworks,
			List<FileItem>              AdditionalPrerequisites,
			bool                        bLegacyPublicIncludePaths
		)
		{
			// This directory may not exist for this module (or ever exist, if it doesn't contain any generated headers),
			// but we want the project files to search it so we can pick up generated code definitions after UHT is run for the first time.
			if(bAddGeneratedCodeIncludePath || 
				(ProjectFileGenerator.bGenerateProjectFiles && GeneratedCodeDirectory != null))
			{
				IncludePaths.Add(GeneratedCodeDirectory);
			}

			base.AddModuleToCompileEnvironment(SourceBinary, IncludePaths, SystemIncludePaths, Definitions, AdditionalFrameworks, AdditionalPrerequisites, bLegacyPublicIncludePaths);
		}

		// UEBuildModule interface.
		// API_DEFINE
		// (GenerateFilesMode인지만 확인)두군데에서 레퍼런스되는데 
		// 여기 무조건 BuildMode에서 접근됨
		public override List<FileItem> Compile
		(
			ReadOnlyTargetRules   Target, 
			ToolChain             ToolChain, 
			CppCompileEnvironment BinaryCompileEnvironment, 
			FileReference         SingleFileToCompile, 
			ISourceFileWorkingSet WorkingSet, 
			IActionGraphBuilder   Graph
		)
		{
			// base.compile return new List<FileItem>
			List<FileItem> LinkInputFileItems = base.Compile(Target, ToolChain, BinaryCompileEnvironment, SingleFileToCompile, WorkingSet, Graph);

			CppCompileEnvironment ModuleCppCompileEnvironment = CreateModuleCppCompileEnvironment(Target, BinaryCompileEnvironment);

			// If the module is precompiled, read the object files from the manifest
			if(ModuleRule.bUsePrecompiled && 
				Target.LinkType == TargetLinkType.Monolithic)
			{
				if(!FileReference.Exists(PrecompiledManifestLocation))
				{
					throw new BuildException("Missing precompiled manifest for '{0}'. " +
						"This module was most likely not flagged for being included in a precompiled build - " +
						"set 'PrecompileForTargets = PrecompileTargetsType.Any;' in {0}{1} to override.", ModuleRuleFileName, Tag.Ext.BuildCS);
				}

				PrecompiledManifest Manifest = PrecompiledManifest.Read(PrecompiledManifestLocation);

				foreach(FileReference OutputFile in Manifest.OutputFiles)
				{
					FileItem ObjectFile = FileItem.GetItemByFileReference(OutputFile);
					if(!ObjectFile.Exists)
					{
						throw new BuildException("Missing object file {0} listed in {1}", OutputFile, PrecompiledManifestLocation);
					}
					LinkInputFileItems.Add(ObjectFile);
				}
				return LinkInputFileItems;
			}

			// Add all the module source directories to the makefile
			foreach (DirectoryReference ModuleDirectory in ModuleDirectories)
			{
				DirectoryItem ModuleDirectoryItem = DirectoryItem.GetItemByDirectoryReference(ModuleDirectory);
				Graph.AddSourceDir(ModuleDirectoryItem);
			}

			// Find all the input files
			Dictionary<DirectoryItem, FileItem[]> DirectoryToSourceFiles = new Dictionary<DirectoryItem, FileItem[]>();

			// 여기서 UEBuildModuleCPP::ModuleDirectories에 있는
			// 모든 *.cpp, *.c, *.cc, (*.m / *.mm), *.rc, *.ispc를
			// UEBuildModuleCPP::SourceDirectories에  저장,
			// _InputFilesCollection에 Extension별로 저장.
			InputFileCollection _InputFilesCollection = FindInputFiles(Target.Platform, DirectoryToSourceFiles);

			foreach (KeyValuePair<DirectoryItem, FileItem[]> Pair in DirectoryToSourceFiles)
			{
				Graph.AddSourceFiles(Pair.Key, Pair.Value);
			}

			// If we're compiling a single file, strip out anything else.
			// This prevents us clobbering response files for anything we're not going to build,
			// triggering a larger build than necessary when we do a regular build again.
			if(SingleFileToCompile != null)
			{
				_InputFilesCollection.CPPFiles.RemoveAll(x => x.FileDirectory != SingleFileToCompile);
				_InputFilesCollection.CCFiles .RemoveAll(x => x.FileDirectory != SingleFileToCompile);
				_InputFilesCollection.CFiles  .RemoveAll(x => x.FileDirectory != SingleFileToCompile);

				if(_InputFilesCollection.CPPFiles.Count == 0 && 
				   _InputFilesCollection.CCFiles.Count  == 0 && 
				   _InputFilesCollection.CFiles.Count   == 0 && 
					!ContainsFile(SingleFileToCompile))
				{
					return new List<FileItem>();
				}
			}

			// Process all of the header file dependencies for this module
			CheckFirstIncludeMatchesEachCppFile(Target, ModuleCppCompileEnvironment, _InputFilesCollection.HeaderFiles, _InputFilesCollection.CPPFiles);

			#region HIDE_SECLECTION
			// Should we force a precompiled header to be generated for this module?
			// Usually, we only bother with a precompiled header if there are at least several source files in the module
			// (after combining them for unity builds.)
			// But for game modules, it can be convenient to always have a precompiled header to single-file
			// changes to code is really quick to compile.

			/*
			int MinFilesUsingPrecompiledHeader = Target.MinFilesUsingPrecompiledHeader;

			if (m_ModuleRules.MinFilesUsingPrecompiledHeaderOverride != 0)
			{
				MinFilesUsingPrecompiledHeader = m_ModuleRules.MinFilesUsingPrecompiledHeaderOverride;
			}
			else if (!m_ModuleRules.bTreatAsEngineModule && Target.bForcePrecompiledHeaderForGameModules)
			{
				// This is a game module with only a small number of source files, so go ahead and force a precompiled header
				// to be generated to make incremental changes to source files as fast as possible for small projects.
				MinFilesUsingPrecompiledHeader = 1;
			}
			*/
			#endregion HIDE_SECLECTION

			// Should we use unity build mode for this module?
			bool bModuleUsesUnityBuild = false;

			{
				// Engine modules will always use unity build mode unless MinSourceFilesForUnityBuildOverride is specified in
				// the module rules file.  By default, game modules only use unity of they have enough source files for that
				// to be worthwhile.  If you have a lot of small game modules, consider specifying MinSourceFilesForUnityBuildOverride=0
				// in the modules that you don't typically iterate on source files in very frequently.
				int MinSourceFilesForUnityBuild = 2;

				if (ModuleRule.MinSourceFilesForUnityBuildOverride != 0)
				{
					MinSourceFilesForUnityBuild = ModuleRule.MinSourceFilesForUnityBuildOverride;
				}
				else if (Target.ProjectFile != null && RulesFile.IsUnderDirectory(DirectoryReference.Combine(Target.ProjectFile.Directory, Tag.Directory.SourceCode)))
				{
					// Game modules with only a small number of source files are usually better off having faster iteration times
					// on single source file changes, so we forcibly disable unity build for those modules
					MinSourceFilesForUnityBuild = Target.MinGameModuleSourceFilesForUnityBuild;
				}


				if (Target.bUseUnityBuild || Target.bForceUnityBuild)
				{
					if (Target.bForceUnityBuild)
					{
						Log.TraceVerbose("Module '{0}' using unity {1} ({2} enabled for this module)", this.ModuleRuleFileName, nameof(BuildMode), nameof(ReadOnlyTargetRules.bForceUnityBuild));
						bModuleUsesUnityBuild = true;
					}
					else if (!ModuleRule.bUseUnity)
					{
						Log.TraceVerbose("Module '{0}' not using unity {1} ({2} disabled for this module)", this.ModuleRuleFileName, nameof(BuildMode), nameof(ModuleRules.bUseUnity));
						bModuleUsesUnityBuild = false;
					}
					else if (_InputFilesCollection.CPPFiles.Count < MinSourceFilesForUnityBuild)
					{
						Log.TraceVerbose("Module '{0}' not using unity build mode (module with fewer than {1} source files)", this.ModuleRuleFileName, MinSourceFilesForUnityBuild);
						bModuleUsesUnityBuild = false;
					}
					else
					{
						Log.TraceVerbose("Module '{0}' using unity {1}", this.ModuleRuleFileName, nameof(BuildMode));
						bModuleUsesUnityBuild = true;
					}
				}
				else
				{
					Log.TraceVerbose("Module '{0}' not using unity {1}", this.ModuleRuleFileName, nameof(BuildMode));
				}
			}

			// Set up the environment with which to compile the CPP files
			CppCompileEnvironment ReplicaModuleCompileEnvironment = ModuleCppCompileEnvironment;

			// Generate ISPC headers first so C++ can consume them
			if (0 < _InputFilesCollection.ISPCFiles.Count)
			{
				CreateHeadersForISPC(ToolChain, ReplicaModuleCompileEnvironment, _InputFilesCollection.ISPCFiles, GeneratedDirectory, Graph);
				// CPPOutput _Output = ToolChain.GenerateOnlyISPCHeaders(ReplicaModuleCompileEnvironment, _InputFilesCollection.ISPCFiles, IntermediateDirectory, Graph);
				// ReplicaModuleCompileEnvironment.AdditionalPrerequisites.AddRange(_Output.ISPCGeneratedHeaderFiles);
				// ReplicaModuleCompileEnvironment.UserIncludePaths.Add(IntermediateDirectory);
			}

			// Configure the precompiled headers for this module
			SetupPCHs(ReplicaModuleCompileEnvironment, Target, ToolChain, LinkInputFileItems, Graph);

			// Write all the definitions to a separate file
			DefinitionsAddToForceIncludes(ReplicaModuleCompileEnvironment, GeneratedDirectory, null, Graph);

			// Mapping of source file to unity file. We output this to intermediate directories for other tools (eg. live coding) to use.
			Dictionary<FileItem, FileItem> SourceFileToUnityFile = new Dictionary<FileItem, FileItem>();

			// Compile CPP files
			// 여기서 Definitions.cpp파일들 만듬. (1 of 14, ... 그런 cpp들)
			List<FileItem> CPPFilesToCompile = _InputFilesCollection.CPPFiles;

			if (bModuleUsesUnityBuild)
			{
				CPPFilesToCompile = UnityCPP.GenerateUnityCPPs
				(
					Target, 
					CPPFilesToCompile, 
					WorkingSet, 
					ModuleRule.ShortName ?? ModuleRuleFileName, 
					GeneratedDirectory, 
					Graph, 
					SourceFileToUnityFile
				);
				LinkInputFileItems.AddRange
				(
					CompileUnityFilesWithToolChain
					(
						Target, 
						ToolChain, 
						ReplicaModuleCompileEnvironment, 
						ModuleCppCompileEnvironment, 
						CPPFilesToCompile, 
						Graph
					).ObjectFiles
				);
			}
			else
			{
				// API_DEFINE
				LinkInputFileItems.AddRange
				(
					ToolChain.CompileCPPFiles
				    (
					    ReplicaModuleCompileEnvironment,
					    CPPFilesToCompile,
					    GeneratedDirectory,
					    ModuleRuleFileName, Graph
				    ).ObjectFiles
				);
			}

			// Compile all the generated CPP files
			// GeneratedCodeWildCard가 UOBject랑 관련 (UHTModuleInfo)
			if (GeneratedCodeWildcard != null && 
				!ReplicaModuleCompileEnvironment.bHackHeaderGenerator)
			{
				string[] GeneratedFiles = Directory.GetFiles(Path.GetDirectoryName(GeneratedCodeWildcard), Path.GetFileName(GeneratedCodeWildcard));

				if(0 < GeneratedFiles.Length)
				{
					// Create a compile environment for the generated files. We can disable creating debug info here to improve link times.
					CppCompileEnvironment GeneratedCPPCompileEnvironment = ReplicaModuleCompileEnvironment;
					if(GeneratedCPPCompileEnvironment.bCreateDebugInfo && Target.bDisableDebugInfoForGeneratedCode)
					{
                        GeneratedCPPCompileEnvironment = new CppCompileEnvironment(GeneratedCPPCompileEnvironment)  { bCreateDebugInfo = false };
                    }

					// Always force include the PCH, even if PCHs are disabled, for generated code. Legacy code can rely on PCHs being included to compile correctly, and this used to be done by UHT manually including it.
					if(GeneratedCPPCompileEnvironment.PrecompiledHeaderFile == null && 
						ModuleRule.PrivatePCHHeaderFile != null && 
						ModuleRule.PCHUsage != ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs)
					{
						FileItem PrivatePchFileItem = FileItem.GetItemByFileReference(FileReference.Combine(ModuleDirectory, ModuleRule.PrivatePCHHeaderFile));

						if(PrivatePchFileItem.Exists == false)
						{
							throw new BuildException("Unable to find private PCH file '{0}', referenced by '{1}'", PrivatePchFileItem.FileDirectory, RulesFile);
						}

						GeneratedCPPCompileEnvironment = new CppCompileEnvironment(GeneratedCPPCompileEnvironment);
						GeneratedCPPCompileEnvironment.ForceIncludeFiles.Add(PrivatePchFileItem);
					}

					// Compile all the generated files
					List<FileItem> GeneratedFileItems = new List<FileItem>();
					foreach (string GeneratedFilename in GeneratedFiles)
					{
						FileItem GeneratedCppFileItem = FileItem.GetItemByPath(GeneratedFilename);
						if (SingleFileToCompile == null || GeneratedCppFileItem.FileDirectory == SingleFileToCompile)
						{
							GeneratedFileItems.Add(GeneratedCppFileItem);
						}
					}

					if (bModuleUsesUnityBuild)
					{
						GeneratedFileItems 
							= UnityCPP.GenerateUnityCPPs
							(
                                Target,
                                GeneratedFileItems,
                                WorkingSet,
                                (ModuleRule.ShortName ?? ModuleRuleFileName) + Tag.Ext.Gen,
                                GeneratedDirectory,
                                Graph,
                                SourceFileToUnityFile
							);
                        LinkInputFileItems.AddRange
                        (
                            CompileUnityFilesWithToolChain
                            (
                                Target,
                                ToolChain,
                                GeneratedCPPCompileEnvironment,
                                ModuleCppCompileEnvironment,
                                GeneratedFileItems,
                                 Graph
                            ).ObjectFiles
						);
                    }
					else
					{
						// API_DEFINE
						LinkInputFileItems.AddRange
						(
							ToolChain.CompileCPPFiles
							(
                                GeneratedCPPCompileEnvironment,
                                GeneratedFileItems,
                                GeneratedDirectory,
                                ModuleRuleFileName,
                                Graph
							).ObjectFiles);
					}
				}
			}

			// Compile ISPC files directly
			if (0 < _InputFilesCollection.ISPCFiles.Count)
			{
				CPPOutput Result = ToolChain.CompileISPCFiles(ReplicaModuleCompileEnvironment, _InputFilesCollection.ISPCFiles, GeneratedDirectory, Graph);

				if (Result != null && 
					0 < Result.ObjectFiles.Count)
				{
					LinkInputFileItems.AddRange(Result.ObjectFiles);
				}
			}

			// Compile C files directly. Do not use a PCH here, because a C++ PCH is not compatible with C source files.
			if(0 < _InputFilesCollection.CFiles.Count)
			{
				// API_DEFINE
				LinkInputFileItems.AddRange(ToolChain.CompileCPPFiles(ModuleCppCompileEnvironment, _InputFilesCollection.CFiles, GeneratedDirectory, ModuleRuleFileName, Graph).ObjectFiles);
			}

			// Compile CC files directly.
			if(0 < _InputFilesCollection.CCFiles.Count)
			{
				LinkInputFileItems.AddRange(ToolChain.CompileCPPFiles(ReplicaModuleCompileEnvironment, _InputFilesCollection.CCFiles, GeneratedDirectory, ModuleRuleFileName, Graph).ObjectFiles);
			}

			// Compile MM files directly.
			if(0 < _InputFilesCollection.MMFiles.Count)
			{
				LinkInputFileItems.AddRange(ToolChain.CompileCPPFiles(ReplicaModuleCompileEnvironment, _InputFilesCollection.MMFiles, GeneratedDirectory, ModuleRuleFileName, Graph).ObjectFiles);
			}

			// Compile RC files. The resource compiler does not work with response files, and
			// using the regular compile environment can easily result in the command line length exceeding the OS limit.
			// Use the binary compile environment to keep the size down, and
			// require that all include paths must be specified relative to the resource file itself or Engine/Source.
			if(0 < _InputFilesCollection.RCFiles.Count)
			{
				CppCompileEnvironment ResourceCompileEnvironment = new CppCompileEnvironment(BinaryCompileEnvironment);
				if(Binary != null)
				{
					// @todo: This should be in some Windows code somewhere...
					ResourceCompileEnvironment.Definitions.Add(Tag.CppContents.Def.OriginalFileName + Binary.OutputFilePaths[0].GetFileName() + "\"");
				}

				CPPOutput Result = ToolChain.CompileRCFiles(ResourceCompileEnvironment, _InputFilesCollection.RCFiles, GeneratedDirectory, Graph);

				if (Result != null && 0 < 
					Result.ObjectFiles.Count)
				{
					LinkInputFileItems.AddRange(Result.ObjectFiles);
				}
			}

			// Write the compiled manifest
			if(ModuleRule.bPrecompile && Target.LinkType == TargetLinkType.Monolithic)
			{
				DirectoryReference.CreateDirectory(PrecompiledManifestLocation.Directory);

				PrecompiledManifest Manifest = new PrecompiledManifest();
				Manifest.OutputFiles.AddRange(LinkInputFileItems.Select(x => x.FileDirectory));
				Manifest.WriteIfModified(PrecompiledManifestLocation);
			}

			// Write a mapping of unity object file to standalone object file for live coding
			if(ModuleRule.Target.bWithLiveCoding)
			{
				FileReference UnityManifestFile = FileReference.Combine(GeneratedDirectory, Tag.OutputFile.LiveCodingManifest + Tag.Ext.JSON);
				using (JsonWriter Writer = new JsonWriter(UnityManifestFile))
				{
					Writer.WriteObjectStart();
					Writer.WriteObjectStart("RemapUnityFiles");
					foreach (IGrouping<FileItem, KeyValuePair<FileItem, FileItem>> UnityGroup in SourceFileToUnityFile.GroupBy(x => x.Value))
					{
						Writer.WriteArrayStart(UnityGroup.Key.FileDirectory.GetFileName() + Tag.Ext.Obj);
						foreach (FileItem SourceFile in UnityGroup.Select(x => x.Key))
						{
							Writer.WriteValue(SourceFile.FileDirectory.GetFileName() + Tag.Ext.Obj);
						}
						Writer.WriteArrayEnd();
					}
					Writer.WriteObjectEnd();
					Writer.WriteObjectEnd();
				}
			}

			return LinkInputFileItems;
		}

		// Create a shared PCH template for this module, which allows constructing shared PCH instances in the future
		public PCHTemplate CreateSharedPCHTemplate(BuildTarget BuildTargetOwnThisModule, CppCompileEnvironment BaseCppCompileEnvironment)
		{
			CppCompileEnvironment CompileEnvironment = CreateSharedPCHCompileEnvironment(BuildTargetOwnThisModule, BaseCppCompileEnvironment);
			FileItem              HeaderFile         = FileItem.GetItemByFileReference(FileReference.Combine(ModuleDirectory, ModuleRule.SharedPCHHeaderFile));

			DirectoryReference PrecompiledHeaderDir;

			if(ModuleRule.bUsePrecompiled)
			{
				PrecompiledHeaderDir = DirectoryReference.Combine(BuildTargetOwnThisModule.ProjectIntermediateDirectory, ModuleRuleFileName);
			}
			else
			{
				PrecompiledHeaderDir = GeneratedDirectory;
			}

			return new PCHTemplate(this, CompileEnvironment, HeaderFile, PrecompiledHeaderDir);
		}

		// Creates a precompiled header action to generate a new pch file
		private PCHInstance CreatePrivatePCH
		(
			ToolChain             ToolChainToCGeneratePCH, 
			FileItem              HeaderFile, 
			CppCompileEnvironment ModuleCompileEnvironment, 
			IActionGraphBuilder   Graph
		)
		{
			// Create the wrapper file, which sets all the definitions needed to compile it
			// WrapperLocation이 PCH.{ModuleRulesFIleName}.h이고 실제 디스크에 작성.
			FileReference PCHLocation = FileReference.Combine(GeneratedDirectory, Tag.OutputFile.PCH + "." + ModuleRuleFileName + Tag.Ext.Header);
			FileItem PCHFile = CreatePCHWrapperFile(PCHLocation, ModuleCompileEnvironment.Definitions, HeaderFile, Graph);

			// Create a new C++ environment that is used to create the PCH.
			CppCompileEnvironment CompileEnvironment = new CppCompileEnvironment(ModuleCompileEnvironment);
			CompileEnvironment.Definitions.Clear();
			CompileEnvironment.PCHAction = PCHAction.Create;
			CompileEnvironment.PCHIncludeFilename = PCHFile.FileDirectory;
			CompileEnvironment.bOptimizeCode = ModuleCompileEnvironment.bOptimizeCode;

			// Create the action to compile the PCH file.
			CPPOutput Output;
			if (ToolChainToCGeneratePCH == null)
			{
				Output = new CPPOutput();
			}
			else
			{
				Output = ToolChainToCGeneratePCH.CompileCPPFiles
				(
                    CompileEnvironment,
                    new List<FileItem>() { PCHFile },
                    GeneratedDirectory,
                    ModuleRuleFileName,
                    Graph
				);
			}
			return new PCHInstance(PCHFile, CompileEnvironment, Output);
		}

		// Generates a precompiled header instance from the given template,
		// or returns an existing one if it already exists
		public PCHInstance FindOrCreateSharedPCH
		(
			PCHTemplate           PCHTemplateReceivingSharedPCH, 
			CppCompileEnvironment ModuleCompileEnvironment,
			ToolChain             ToolChain,
			IActionGraphBuilder   Graph // Usually NullActionBuildGraph
		)
		{
			PCHInstance Instance = PCHTemplateReceivingSharedPCH.PCHInstances.Find(x => IsCompatibleForSharedPCH(x.CompileEnvironment, ModuleCompileEnvironment));
			
			if(Instance == null)
			{
				// Create a suffix to distinguish this shared PCH variant from any others. Currently only optimized and non-optimized shared PCHs are supported.
				string AdditionalSuffix = GetSharedPCHExtension(ModuleCompileEnvironment, PCHTemplateReceivingSharedPCH.BaseCompileEnvironmentToUse);

				// Create the wrapper file, which sets all the definitions needed to compile it
				FileReference SharedPCHLocation = FileReference.Combine(PCHTemplateReceivingSharedPCH.OutputDirForThisPCH, 
					String.Format(Tag.OutputFile.SharedPCH + ".{0}{1}.h", PCHTemplateReceivingSharedPCH.ModuleWithValidSharedPCH.ModuleRuleFileName, AdditionalSuffix));

                FileItem SharedPCHFile = CreatePCHWrapperFile(SharedPCHLocation, PCHTemplateReceivingSharedPCH.BaseCompileEnvironmentToUse.Definitions, PCHTemplateReceivingSharedPCH.PCHFile, Graph);

                // Create the compile environment for this PCH
                CppCompileEnvironment CompileEnvironment = new CppCompileEnvironment(PCHTemplateReceivingSharedPCH.BaseCompileEnvironmentToUse);
				CompileEnvironment.Definitions.Clear();
				CompileEnvironment.PCHAction = PCHAction.Create;
				CompileEnvironment.PCHIncludeFilename = SharedPCHFile.FileDirectory; // Put Other Module's SharedPCH

				CopySettingsForSharedPCH(ModuleCompileEnvironment, CompileEnvironment); 

				// Create the PCH
				CPPOutput Output;

				if (ToolChain == null)
				{
					Output = new CPPOutput();
				}
				else
				{
					Output = ToolChain.CompileCPPFiles(CompileEnvironment, new List<FileItem>() { SharedPCHFile }, PCHTemplateReceivingSharedPCH.OutputDirForThisPCH, "Shared", Graph);
				}

				Instance = new PCHInstance(SharedPCHFile, CompileEnvironment, Output);

				PCHTemplateReceivingSharedPCH.PCHInstances.Add(Instance);
			}

			return Instance;
		}

		// Compiles the provided CPP unity files.
		private CPPOutput CompileUnityFilesWithToolChain
		(
			ReadOnlyTargetRules   Target, 
			ToolChain             ToolChain, 
			CppCompileEnvironment CompileEnvironment, 
			CppCompileEnvironment ModuleCompileEnvironment, 
			List<FileItem>        SourceFiles, 
			IActionGraphBuilder   Graph
		)
		{
			List<FileItem> NormalFiles   = new List<FileItem>();
			List<FileItem> AdaptiveFiles = new List<FileItem>();

			bool bAdaptiveUnityDisablesPCH = false;

			if(ModuleRule.PCHUsage == ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs)
			{
				if(ModuleRule.bTreatAsEngineModule || 
				   ModuleRule.PrivatePCHHeaderFile == null)
				{
					bAdaptiveUnityDisablesPCH = Target.bAdaptiveUnityDisablesPCH;
				}
				else
				{
					bAdaptiveUnityDisablesPCH = Target.bAdaptiveUnityDisablesPCHForProject;
				}
			}

			if ((Target.bAdaptiveUnityDisablesOptimizations || bAdaptiveUnityDisablesPCH || Target.bAdaptiveUnityCreatesDedicatedPCH) 
				&& !Target.bStressTestUnity)
			{
				foreach (FileItem File in SourceFiles)
				{
					// Basic check as to whether something in this module is/isn't a unity file...
					if (File.FileDirectory.GetFileName().StartsWith(UnityCPP.ModulePrefix))
					{
						NormalFiles.Add(File);
					}
					else
					{
						AdaptiveFiles.Add(File);
					}
				}
			}
			else
			{
				NormalFiles.AddRange(SourceFiles);
			}

			CPPOutput OutputFiles = new CPPOutput();

			if (0 < NormalFiles.Count)
			{
				OutputFiles = ToolChain.CompileCPPFiles(CompileEnvironment, NormalFiles, GeneratedDirectory, ModuleRuleFileName, Graph);
			}

			if (0 < AdaptiveFiles.Count)
			{
				// Create the new compile environment. Always turn off PCH due to different compiler settings.
				CppCompileEnvironment AdaptiveUnityEnvironment = new CppCompileEnvironment(ModuleCompileEnvironment);
				if(Target.bAdaptiveUnityDisablesOptimizations)
				{
					AdaptiveUnityEnvironment.bOptimizeCode = false;
				}
				if (Target.bAdaptiveUnityEnablesEditAndContinue)
				{
					AdaptiveUnityEnvironment.bSupportEditAndContinue = true;
				}

				// Create a per-file PCH
				CPPOutput AdaptiveOutput;
				if(Target.bAdaptiveUnityCreatesDedicatedPCH)
				{
					AdaptiveOutput = 
					CompileAdaptiveNonUnityFilesWithDedicatedPCH
					(
						ToolChain, 
						AdaptiveUnityEnvironment, 
						AdaptiveFiles, 
						GeneratedDirectory, 
						ModuleRuleFileName, 
						Graph
					);
				}
				else if(bAdaptiveUnityDisablesPCH)
				{
					AdaptiveOutput = CompileAdaptiveNonUnityFilesWithoutPCH
					(
                        ToolChain,
                        AdaptiveUnityEnvironment,
                        AdaptiveFiles,
                        GeneratedDirectory,
                        ModuleRuleFileName,
                        Graph
					);
				}
				else if(AdaptiveUnityEnvironment.bOptimizeCode != CompileEnvironment.bOptimizeCode || 
					    AdaptiveUnityEnvironment.bSupportEditAndContinue != CompileEnvironment.bSupportEditAndContinue)
				{
					AdaptiveOutput = 
					CompileAdaptiveNonUnityFiles
					(
						ToolChain, 
						AdaptiveUnityEnvironment, 
						AdaptiveFiles, 
						GeneratedDirectory, 
						ModuleRuleFileName, 
						Graph
					);
				}
				else
				{
					AdaptiveOutput = 
					CompileAdaptiveNonUnityFiles
					(
						ToolChain, 
						CompileEnvironment, 
						AdaptiveFiles, 
						GeneratedDirectory,
						ModuleRuleFileName, 
						Graph
					);
				}

				// Merge output
				OutputFiles.ObjectFiles.AddRange(AdaptiveOutput.ObjectFiles);
				OutputFiles.DebugDataFiles.AddRange(AdaptiveOutput.DebugDataFiles);
			}

			return OutputFiles;
		}

		// Configure precompiled headers for this module
		void SetupPCHs //                       When            GenerateFileMode       BuildMode
		(
			CppCompileEnvironment OutCppCompileEnvironment, //  ModuleCPPCompileEnviron
			ReadOnlyTargetRules   TargetRulesBeingBuilt,    //  ProjectTargetRules
			ToolChain             ToolChain,                //  Null
			List<FileItem>        LinkInputFiles,           //  Null
			IActionGraphBuilder   Graph                     //  Null
		)
		{
			if (TargetRulesBeingBuilt.bUsePCHFiles && 
				ModuleRule.PCHUsage != ModuleRules.PCHUsageMode.NoPCHs)
			{
                // If this module doesn't need a shared PCH, configure that
                if (ModuleRule.PrivatePCHHeaderFile != null &&
                  (
                      ModuleRule.PCHUsage == ModuleRules.PCHUsageMode.NoSharedPCHs ||
                      ModuleRule.PCHUsage == ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs)
                  )
                {
                    // Some Plugins, ImageWrapper, Core, EditorEd, Engine, InstallBundlemanager, PixelStreaming
                    PCHInstance _PCHInstance =
                        CreatePrivatePCH
                        (
                            ToolChain,
                            FileItem.GetItemByFileReference(FileReference.Combine(ModuleDirectory, ModuleRule.PrivatePCHHeaderFile)),
                            OutCppCompileEnvironment,
                            Graph
                        );

                    OutCppCompileEnvironment = new CppCompileEnvironment(OutCppCompileEnvironment);
					OutCppCompileEnvironment.Definitions.Clear();
					OutCppCompileEnvironment.PCHAction             = PCHAction.Include;
					OutCppCompileEnvironment.PCHIncludeFilename    = _PCHInstance.HeaderFile.FileDirectory;
					OutCppCompileEnvironment.PrecompiledHeaderFile = _PCHInstance.CppOutputFiles.PCHFile;

					if(LinkInputFiles != null)
					{
						LinkInputFiles.AddRange(_PCHInstance.CppOutputFiles.ObjectFiles);
					}
					else
					{
						Debugger.Break();
					}
				}

				// Try to find a suitable shared PCH for this module
				if (OutCppCompileEnvironment.PCHIncludeFilename == null && 
					0 < OutCppCompileEnvironment.SharedPCHs.Count       && 
					!OutCppCompileEnvironment.bIsBuildingLibrary        && 
					ModuleRule.PCHUsage != ModuleRules.PCHUsageMode.NoSharedPCHs)
				{
					// Find all the dependencies of this module
					HashSet<BuildModule> ReferencedModules = new HashSet<BuildModule>();
					RecursivelyGetAllDependencyModules
					(
						new List<BuildModule>(), 
						ReferencedModules, 
						bIncludeDynamicallyLoaded        : false, 
						bIgnoreCircularDependencies      : false, 
						OutbThisModuleDirectDependencies : true
					);

					// Find the first shared PCH module we can use
					// ReferencedModules중에
					PCHTemplate _PCHTemplate = OutCppCompileEnvironment.SharedPCHs.FirstOrDefault(x => ReferencedModules.Contains(x.ModuleWithValidSharedPCH));

					if(_PCHTemplate != null && 
					   _PCHTemplate.IsValidFor(OutCppCompileEnvironment))
					{
						FileItem PrivateDefinitionsFileItem;

						using (StringWriter Writer = new StringWriter())
						{
							Writer.WriteLine("// For Remove the Circurlar Dependencies");
							Writer.WriteLine("// between the Shared PCH module and modules using it.");
							// Remove the module _API definition for cases where there are circular dependencies
							// between the shared PCH module and modules using it
							Writer.WriteLine(Tag.CppContents.Undef + ModuleApiDefine);
							Writer.WriteLine("");
							// Games may choose to use shared PCHs from the engine, so allow them to change the value of these macros
							if(!ModuleRule.bTreatAsEngineModule)
							{
								Writer.WriteLine(Tag.CppContents.Undef + Tag.CppContents.Def.IsEngineModule);
								Writer.WriteLine(Tag.CppContents.Undef + Tag.CppContents.Def.DeprecatedForGame);
								Writer.WriteLine(Tag.CppContents.Define + Tag.CppContents.Def.DeprecatedForGame + Tag.CppContents.Def.DeprecatedValue);
								// Writer.WriteLine("#undef UE_IS_ENGINE_MODULE");

								// Writer.WriteLine("#undef DEPRECATED_FORGAME");
								// Writer.WriteLine("#define DEPRECATED_FORGAME DEPRECATED");

								// Writer.WriteLine("#undef UE_DEPRECATED_FORGAME");
								// Writer.WriteLine("#define UE_DEPRECATED_FORGAME UE_DEPRECATED");
							}

							WriteDefinitions(OutCppCompileEnvironment.Definitions, Writer);

							FileReference PrivateDefinitionsFile = FileReference.Combine(GeneratedDirectory, Tag.OutputFile.Definitions + "." + ModuleRuleFileName + Tag.Ext.Header);

							PrivateDefinitionsFileItem = Graph.CreateIntermediateTextFile(PrivateDefinitionsFile, Writer.ToString());
						} // End StringWriter

						PCHInstance PCHInstance = FindOrCreateSharedPCH(_PCHTemplate, OutCppCompileEnvironment, ToolChain, Graph);

						OutCppCompileEnvironment = new CppCompileEnvironment(OutCppCompileEnvironment);
						OutCppCompileEnvironment.Definitions.Clear();
						OutCppCompileEnvironment.ForceIncludeFiles.Add(PrivateDefinitionsFileItem);
						OutCppCompileEnvironment.PCHAction             = PCHAction.Include;
						OutCppCompileEnvironment.PCHIncludeFilename    = PCHInstance.HeaderFile.FileDirectory;
						OutCppCompileEnvironment.PrecompiledHeaderFile = PCHInstance.CppOutputFiles.PCHFile;

						if (LinkInputFiles != null)
						{
							LinkInputFiles.AddRange(PCHInstance.CppOutputFiles.ObjectFiles);
						}
					} // End if statement
				}
			}
		}

		// Checks that the first header included by the source files in this module all include the same header
		private void CheckFirstIncludeMatchesEachCppFile
		(
            ReadOnlyTargetRules Target,
            CppCompileEnvironment ModuleCompileEnvironment,
            List<FileItem> HeaderFiles,
            List<FileItem> CppFiles
		)
		{
			if(ModuleRule.PCHUsage == ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs)
			{
				if(InvalidIncludeDirectiveMessages == null)
				{
					// Find headers used by the source file.
					Dictionary<string, FileReference> NameToHeaderFile = new Dictionary<string, FileReference>();
					foreach(FileItem HeaderFile in HeaderFiles)
					{
						NameToHeaderFile[HeaderFile.FileDirectory.GetFileNameWithoutExtension()] = HeaderFile.FileDirectory;
					}

					// Find the directly included files for each source file, and make sure it includes the matching header if possible
					InvalidIncludeDirectiveMessages = new List<string>();
					if (ModuleRule != null      && 
						ModuleRule.bEnforceIWYU && 
						Target.bEnforceIWYU)
					{
						foreach (FileItem CppFile in CppFiles)
						{
							string FirstInclude = ModuleCompileEnvironment.MetadataCache.GetFirstInclude(CppFile);
							if(FirstInclude != null)
							{
								string IncludeName = Path.GetFileNameWithoutExtension(FirstInclude);
								string ExpectedName = CppFile.FileDirectory.GetFileNameWithoutExtension();
								if (String.Compare(IncludeName, ExpectedName, StringComparison.OrdinalIgnoreCase) != 0)
								{
                                    if (NameToHeaderFile.TryGetValue(ExpectedName, out FileReference HeaderFile) && 
										!IgnoreMismatchedHeader(ExpectedName))
                                    {
                                        InvalidIncludeDirectiveMessages.Add(String.Format("{0}(1): error: Expected {1} to be first header included.", CppFile.FileDirectory, HeaderFile.GetFileName()));
                                    }
                                }
							}
						}
					}
				}
			}
		}

		private bool IgnoreMismatchedHeader(string ExpectedName)
		{
			if(ExpectedName == Tag.Module.Engine.RHI.DynamicRHI     ||
			   ExpectedName == Tag.Module.Engine.RHI.RHICommandList ||
			   ExpectedName == Tag.Module.Engine.RHI.RHIUtilities)
            {
				return true;
            }

			if(ModuleRuleFileName == Tag.Module.Engine.Windows.D3D11RHI ||
			   ModuleRuleFileName == Tag.Module.Engine.Windows.D3D12RHI ||
			   ModuleRuleFileName == Tag.Module.Engine.VulkanRHI        ||
			   ModuleRuleFileName == Tag.Module.Engine.MetalRHI)
            {
				return true;
            }

			return false;
		}

		public CppCompileEnvironment CreateCompileEnvironmentWithPCHAndForceIncludes(ReadOnlyTargetRules ProjectTargetRules, CppCompileEnvironment BinariesCompileEnvironment)
		{
			CppCompileEnvironment OutModuleCompileEnvironment = CreateModuleCppCompileEnvironment(ProjectTargetRules, BinariesCompileEnvironment);

			SetupPCHs(OutModuleCompileEnvironment, ProjectTargetRules, null, new List<FileItem>(), new NullActionGraphBuilder());

			DefinitionsAddToForceIncludes(OutModuleCompileEnvironment, GeneratedDirectory, null, new NullActionGraphBuilder());

			return OutModuleCompileEnvironment;
		}

		// Creates a compile environment from a base environment based on the module settings.
		public CppCompileEnvironment CreateModuleCppCompileEnvironment(ReadOnlyTargetRules Target, CppCompileEnvironment BaseCompileEnvironment)
		{
#pragma warning disable IDE0017 // Simplify object initialization
            CppCompileEnvironment Result = new CppCompileEnvironment(BaseCompileEnvironment);
#pragma warning restore IDE0017 // Simplify object initialization

            // Override compile environment
            Result.bUseUnity                              = ModuleRule.bUseUnity;
			Result.bOptimizeCode                          = ShouldEnableOptimization(ModuleRule.OptimizeCode, Target.Configuration, ModuleRule.bTreatAsEngineModule);
			Result.bUseRTTI                              |= ModuleRule.bUseRTTI;
			Result.bUseAVX                                = ModuleRule.bUseAVX;
			Result.bEnableBufferSecurityChecks            = ModuleRule.bEnableBufferSecurityChecks;
			Result.MinSourceFilesForUnityBuildOverride    = ModuleRule.MinSourceFilesForUnityBuildOverride;
			Result.MinFilesUsingPrecompiledHeaderOverride = ModuleRule.MinFilesUsingPrecompiledHeaderOverride;
			Result.bBuildLocallyWithSNDBS                 = ModuleRule.bBuildLocallyWithSNDBS;
			Result.ShadowVariableWarningLevel             = ModuleRule.ShadowVariableWarningLevel;
			Result.UnsafeTypeCastWarningLevel             = ModuleRule.UnsafeTypeCastWarningLevel;
			Result.bEnableUndefinedIdentifierWarnings     = ModuleRule.bEnableUndefinedIdentifierWarnings;

			// If the module overrides the C++ language version, override it on the compile environment
			if(ModuleRule.CppStandard != CppStandardVersion.Default)
			{
				Result.CppStandard = ModuleRule.CppStandard;
			}

			// Set the macro used to check whether monolithic headers can be used
			if (ModuleRule.bTreatAsEngineModule && (!ModuleRule.bEnforceIWYU || !Target.bEnforceIWYU))
			{
				Result.Definitions.Add(Tag.CppContents.Def.SuppressMonoithicsHeaderWarning + Tag.Boolean.One);
			}

			// Add a macro for when we're compiling an engine module, to enable additional compiler diagnostics through code.
			if (ModuleRule.bTreatAsEngineModule)
			{
				Result.Definitions.Add(Tag.CppContents.Def.IsEngineModule + Tag.Boolean.One);
			}
			else
			{
				Result.Definitions.Add(Tag.CppContents.Def.IsEngineModule + Tag.Boolean.Zero);
			}

			// For game modules, set the define for the project and target names, which will be used by the IMPLEMENT_PRIMARY_GAME_MODULE macro.
			if (!ModuleRule.bTreatAsEngineModule)
			{
				// Make sure we don't set any define for a non-engine module that's under the engine directory (eg. DefaultGame)
				if (Target.ProjectFile != null && RulesFile.IsUnderDirectory(Target.ProjectFile.Directory))
				{
					string ProjectName = Target.ProjectFile.GetFileNameWithoutExtension();
					Result.Definitions.Add(String.Format(Tag.CppContents.Def.ProjectName + "={0}", ProjectName));
					Result.Definitions.Add(String.Format(Tag.CppContents.Def.TargetName + "={0}", Target.Name));
				}
			}

			// Add the module's public and private definitions.
			Result.Definitions.AddRange(PublicDefinitions);
			Result.Definitions.AddRange(ModuleRule.PrivateDefinitions);

			// Add the project definitions
			if(!ModuleRule.bTreatAsEngineModule)
			{
				Result.Definitions.AddRange(ModuleRule.Target.ProjectDefinitions);
			}

			// Setup the compile environment for the module.
			// API_DEFINE
			SetupPrivateCompileEnvironment(Result.UserIncludePaths, Result.SystemIncludePaths, Result.Definitions, Result.AdditionalFrameworks, Result.AdditionalPrerequisites, ModuleRule.bLegacyPublicIncludePaths);

			return Result;
		}

		// Creates a compile environment for a shared PCH from a base environment based on the module settings.
		public CppCompileEnvironment CreateSharedPCHCompileEnvironment(BuildTarget Target, CppCompileEnvironment BaseCompileEnvironment)
		{
			CppCompileEnvironment OutCompileEnvironment = new CppCompileEnvironment(BaseCompileEnvironment)
			{
				// Use the default optimization setting for 
				bOptimizeCode = ShouldEnableOptimization(ModuleRules.CodeOptimization.Default, Target.Configuration, ModuleRule.bTreatAsEngineModule),

				// Override compile environment
				bIsBuildingDLL     = !Target.ShouldCompileMonolithic(),
				bIsBuildingLibrary = false
			};

			// Add a macro for when we're compiling an engine module, to enable additional compiler diagnostics through code.
			if (ModuleRule.bTreatAsEngineModule)
			{
				OutCompileEnvironment.Definitions.Add("UE_IS_ENGINE_MODULE=1");
			}
			else
			{
				OutCompileEnvironment.Definitions.Add("UE_IS_ENGINE_MODULE=0");
			}

			// Add the module's private definitions.
			OutCompileEnvironment.Definitions.AddRange(PublicDefinitions);

			// Find all the modules that are part of the public compile environment for this module.
			Dictionary<BuildModule, bool> ModuleToIncludePathsOnlyFlag = new Dictionary<BuildModule, bool>();
			RecursivelyFindModulesInPublicCompileEnvironment(ModuleToIncludePathsOnlyFlag);

			// Now set up the compile environment for the modules in the original order that we encountered them
			foreach (BuildModule Module in ModuleToIncludePathsOnlyFlag.Keys)
			{
				Module.AddModuleToCompileEnvironment
				(
					null,
					OutCompileEnvironment.UserIncludePaths, 
					OutCompileEnvironment.SystemIncludePaths, 
					OutCompileEnvironment.Definitions, 
					OutCompileEnvironment.AdditionalFrameworks, 
					OutCompileEnvironment.AdditionalPrerequisites, 
					ModuleRule.bLegacyPublicIncludePaths
				);
			}

			return OutCompileEnvironment;
		}

		public override void RecursivelyGetAllDependencyModules
		(
			List<BuildModule>    ReferencedModules, 
			HashSet<BuildModule> IgnoreReferencedModules, 
			bool                 bIncludeDynamicallyLoaded, 
			bool                 bIgnoreCircularDependencies, 
			bool                 OutbThisModuleDirectDependencies
		)
		{
			List<BuildModule> AllDependencyModules = new List<BuildModule>();
			AllDependencyModules.AddRange(PrivateDependencyModules);
			AllDependencyModules.AddRange(PublicDependencyModules);

			if (bIncludeDynamicallyLoaded)
			{
				AllDependencyModules.AddRange(DynamicallyLoadedModules);
			}

			foreach (BuildModule DependencyModule in AllDependencyModules)
			{
				if (!IgnoreReferencedModules.Contains(DependencyModule))
				{
					// Don't follow circular back-references!
					bool bIsCircular = HasCircularDependencyOn(DependencyModule.ModuleRuleFileName);

					if (bIgnoreCircularDependencies || !bIsCircular)
					{
						IgnoreReferencedModules.Add(DependencyModule);

						if (!OutbThisModuleDirectDependencies)
						{
							// Recurse into dependent modules first
							DependencyModule.RecursivelyGetAllDependencyModules
							(
								ReferencedModules, 
								IgnoreReferencedModules, 
								bIncludeDynamicallyLoaded, 
								bIgnoreCircularDependencies, 
								OutbThisModuleDirectDependencies
							);
						}

						ReferencedModules.Add(DependencyModule);
					}
				}
			}
		}

		// Finds all the source files that should be built for this module
		public InputFileCollection FindInputFiles(BuildTargetPlatform Platform, Dictionary<DirectoryItem, FileItem[]> OutDirectoryToSourceFiles)
		{
			InputFileCollection ShoutbeBuiltSourceFile = new InputFileCollection();

			ReadOnlyHashSet<string> ExcludedNames = BuildPlatform.GetBuildPlatform(Platform).GetExcludedFolderNames();

			SourceDirectories = new HashSet<DirectoryReference>();

			foreach (DirectoryReference Dir in ModuleDirectories)
			{
				DirectoryItem ModuleDirectoryItem = DirectoryItem.GetItemByDirectoryReference(Dir);
				RecursivelyFindInputFilesFromDirectory
				(
                    ModuleDirectoryItem,
                    ExcludedNames,
                    SourceDirectories,
                    OutDirectoryToSourceFiles,
                    ShoutbeBuiltSourceFile
				);
			}

			return ShoutbeBuiltSourceFile;
		}

		// Finds all the source files that should be built for this module
		void RecursivelyFindInputFilesFromDirectory
		(
			DirectoryItem                         BaseDirectory, 
			ReadOnlyHashSet<string>               ExcludedNames, 
			HashSet<DirectoryReference>           OutSourceDirectories,      // Including *.cpp, *.c, *.cc, *.(m /mm), *.rc, *.ispc. 
			Dictionary<DirectoryItem, FileItem[]> OutDirectoryToSourceFiles, // SourceDirectories + *.isph + *.h
			InputFileCollection                   InputFiles
		)
		{
			foreach(DirectoryItem SubDirectory in BaseDirectory.EnumerateSubDirectories())
			{
				if(!ExcludedNames.Contains(SubDirectory.Name))
				{
					RecursivelyFindInputFilesFromDirectory(SubDirectory, ExcludedNames, OutSourceDirectories, OutDirectoryToSourceFiles, InputFiles);
				}
			}

			// SourceFiles -> *.cpp, *.c, *.cc, *.(m /mm), *.rc, *.ispc
			FileItem[] SourceFiles = AllAddToInputFilesFromDirectory(BaseDirectory, InputFiles);

			if(0 < SourceFiles.Length)
			{
				OutSourceDirectories.Add(BaseDirectory.FullDirectory);
			}

			OutDirectoryToSourceFiles.Add(BaseDirectory, SourceFiles);
		}

		// Finds the input files that should be built for this module, from a given directory
		static FileItem[] AllAddToInputFilesFromDirectory(DirectoryItem BaseDirectory, InputFileCollection InputFiles)
		{
			// Only Add *.cpp, *.c, *.cc, (*.m / *.mm), *.rc, *.ispc
			List<FileItem> SourceFiles = new List<FileItem>();

			foreach(FileItem InputFile in BaseDirectory.EnumerateAllCachedFiles())
			{
				if (InputFile.HasExtension(Tag.Ext.Header))
				{
					InputFiles.HeaderFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.ISPH))
				{
					InputFiles.ISPCHeaderFiles.Add(InputFile);
				}
				if (InputFile.HasExtension(Tag.Ext.CppSource))
				{
					SourceFiles.Add(InputFile);
					InputFiles.CPPFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.CSource))
				{
					SourceFiles.Add(InputFile);
					InputFiles.CFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.CCSource))
				{
					SourceFiles.Add(InputFile);
					InputFiles.CCFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.ObjCSource) || 
					     InputFile.HasExtension(Tag.Ext.ObjCSource2))
				{
					SourceFiles.Add(InputFile);
					InputFiles.MMFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.RC))
				{
					SourceFiles.Add(InputFile);
					InputFiles.RCFiles.Add(InputFile);
				}
				else if (InputFile.HasExtension(Tag.Ext.ISPC))
				{
					SourceFiles.Add(InputFile);
					InputFiles.ISPCFiles.Add(InputFile);
				}
			}
			return SourceFiles.ToArray();
		}

		// Gets a set of source files for the given directory.
		// Used to detect when the makefile is out of date.
		public static FileItem[] GetSourceFiles(DirectoryItem Directory)
		{
			return AllAddToInputFilesFromDirectory(Directory, new InputFileCollection());
		}
	}
}
