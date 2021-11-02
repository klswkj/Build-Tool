using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Text;
using System.Linq;
using Ionic.Zip;
using BuildToolUtilities;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BuildTool
{
	class MacToolChainSettings : AppleToolChainSettings
	{
		/***********************************************************************
		 * NOTE:
		 *  Do NOT change the defaults to set your values, instead you should set the environment variables
		 *  properly in your system, as other tools make use of them to work properly!
		 *  The defaults are there simply for examples so you know what to put in your env vars...
		 ***********************************************************************/

		// Which version of the Mac OS SDK to target at build time
		public string MacOSSDKVersion = "latest";

		public float MacOSSDKVersionFloat = 0.0f;

		// Which version of the Mac OS X to allow at run time
		public string MacOSVersion = "10.14";

		// Minimum version of Mac OS X to actually run on,
		// running on earlier versions will display the system minimum version error dialog and exit.
		public string MinMacOSVersion = "10.14.6";

		public string ToolchainDir = "";
		public string BaseSDKDir;

		public MacToolChainSettings(bool bVerbose) 
			: base(bVerbose)
		{
			BaseSDKDir   = XcodeDeveloperDir + "Platforms/MacOSX.platform/Developer/SDKs";
			ToolchainDir = XcodeDeveloperDir + "Toolchains/XcodeDefault.xctoolchain/usr/bin/";

			SelectSDK(BaseSDKDir, "MacOSX", ref MacOSSDKVersion, bVerbose);

			// convert to float for easy comparison
			if(String.IsNullOrWhiteSpace(MacOSSDKVersion))
			{
				throw new BuildException("Unable to find installed MacOS SDK on remote agent.");
			}
			else if(!float.TryParse(MacOSSDKVersion, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture.NumberFormat, out MacOSSDKVersionFloat))
			{
				throw new BuildException("Unable to parse installed MacOS version (\"{0}\")", MacOSSDKVersion);
			}
		}
	}

	// Option flags for the Mac toolchain
	[Flags]
	enum MacToolChainOptions
	{
		None                             = 0x00, // No custom options
		EnableAddressSanitizer           = 0x01, // Enable address sanitzier
		EnableThreadSanitizer            = 0x02, // Enable thread sanitizer
		EnableUndefinedBehaviorSanitizer = 0x04, // Enable undefined behavior sanitizer
		OutputDylib                      = 0x08, // Whether we're outputting a dylib instead of an executable
	}

	// Mac toolchain wrapper
	class MacToolChain : AppleToolChain
	{
		// Whether to compile with ASan enabled
		private readonly MacToolChainOptions Options;

		public MacToolChain(FileReference InProjectFile, MacToolChainOptions InOptions)
			: base(InProjectFile)
		{
			this.Options = InOptions;
		}

		public static Lazy<MacToolChainSettings> SettingsPrivate = new Lazy<MacToolChainSettings>(() => new MacToolChainSettings(false));

		public static MacToolChainSettings Settings 
			=> SettingsPrivate.Value;

		// Which compiler frontend to use
		private const string MacCompiler = "clang++";

		// Which linker frontend to use		
		private const string MacLinker = "clang++";

		// Which archiver to use		
		private const string MacArchiver = "libtool";

		// Track which scripts need to be deleted before appending to
		private bool bHasWipedFixDylibScript = false;

		private static readonly List<FileItem> BundleDependencies = new List<FileItem>();

		public override void SetUpGlobalEnvironment(ReadOnlyTargetRules Target)
		{
			base.SetUpGlobalEnvironment(Target);
		}

		private string GetCompileArguments_Global(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";

			Result += " -fmessage-length=0";
			Result += " -pipe";
			Result += " -fpascal-strings";

			Result += " -fexceptions";
			Result += " -fasm-blocks";

			if(CompileEnvironment.bHideSymbolsByDefault)
			{
				Result += " -fvisibility-ms-compat";
				Result += " -fvisibility-inlines-hidden";
			}
			if (Options.HasFlag(MacToolChainOptions.EnableAddressSanitizer))
			{
				Result += " -fsanitize=address";
			}
			if (Options.HasFlag(MacToolChainOptions.EnableThreadSanitizer))
			{
				Result += " -fsanitize=thread";
			}
			if (Options.HasFlag(MacToolChainOptions.EnableUndefinedBehaviorSanitizer))
			{
				Result += " -fsanitize=undefined";
			}			

			Result += " -Wall -Werror";
			Result += " -Wdelete-non-virtual-dtor";

			// clang 12.00 has a new warning for copies in ranged loops. Instances have all been fixed up (2020/6/26) but
			// are likely to be reintroduced due to no equivalent on other platforms at this time so disable the warning
			if (12 <= GetClangVersion().Major)
			{
				Result += " -Wno-range-loop-analysis";
			}			

			//Result += " -Wsign-compare"; // fed up of not seeing the signed/unsigned warnings we get on Windows - lets enable them here too.

			if (CompileEnvironment.ShadowVariableWarningLevel != WarningLevel.Off)
			{
				Result += " -Wshadow" + ((CompileEnvironment.ShadowVariableWarningLevel == WarningLevel.Error) ? "" : " -Wno-error=shadow");
			}

			if (CompileEnvironment.bEnableUndefinedIdentifierWarnings)
			{
				Result += " -Wundef" + (CompileEnvironment.bUndefinedIdentifierWarningsAsErrors ? "" : " -Wno-error=undef");
			}

			Result += " -c";

			Result += " -arch x86_64";
			Result += " -isysroot " + Settings.BaseSDKDir + "/MacOSX" + Settings.MacOSSDKVersion + ".sdk";
			Result += " -mmacosx-version-min=" + (CompileEnvironment.bEnableOSX109Support ? "10.9" : Settings.MacOSVersion);

			bool bStaticAnalysis = false;
			string StaticAnalysisMode = Environment.GetEnvironmentVariable("CLANG_STATIC_ANALYZER_MODE");
			if(StaticAnalysisMode != null && StaticAnalysisMode != "")
			{
				bStaticAnalysis = true;
			}

			// Optimize non- debug builds.
			if (CompileEnvironment.bOptimizeCode && !bStaticAnalysis)
			{
				// Don't over optimise if using AddressSanitizer or you'll get false positive errors due to erroneous optimisation of necessary AddressSanitizer instrumentation.
				if (Options.HasFlag(MacToolChainOptions.EnableAddressSanitizer))
				{
					Result += " -O1 -g -fno-optimize-sibling-calls -fno-omit-frame-pointer";
				}
				else if (Options.HasFlag(MacToolChainOptions.EnableThreadSanitizer))
				{
					Result += " -O1 -g";
				}
				else if (CompileEnvironment.bOptimizeForSize)
				{
					Result += " -Oz";
				}
				else
				{
					Result += " -O3";
				}
			}
			else
			{
				Result += " -O0";
			}

			if (!CompileEnvironment.bUseInlining)
			{
				Result += " -fno-inline-functions";
			}

			// Create DWARF format debug info if wanted,
			if (CompileEnvironment.bCreateDebugInfo)
			{
				Result += " -gdwarf-2";
			}

			return Result;
		}

		private static string GetCppStandardCompileArgument(CppCompileEnvironment CompileEnvironment)
		{
			Dictionary<CppStandardVersion, string> Mapping = new Dictionary<CppStandardVersion, string>
			{
				{ CppStandardVersion.Cpp14, " -std=c++14" },
				{ CppStandardVersion.Cpp17, " -std=c++17" },
				{ CppStandardVersion.Latest, " -std=c++17" },
				{ CppStandardVersion.Default, " -std=c++14" }
			};
			return Mapping[CompileEnvironment.CppStandard];
		}

		private static string GetCompileArguments_CPP(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x objective-c++";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += GetCppStandardCompileArgument(CompileEnvironment);
			Result += " -stdlib=libc++";
			return Result;
		}

		private static string GetCompileArguments_MM(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x objective-c++";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += GetCppStandardCompileArgument(CompileEnvironment);
			Result += " -stdlib=libc++";
			return Result;
		}

		private static string GetCompileArguments_M()
		{
			string Result = "";
			Result += " -x objective-c";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += " -stdlib=libc++";
			return Result;
		}

		static string GetCompileArguments_C()
		{
			string Result = "";
			Result += " -x c";
			return Result;
		}

		static string GetCompileArguments_PCH(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x objective-c++-header";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += GetCppStandardCompileArgument(CompileEnvironment);
			Result += " -stdlib=libc++";
			return Result;
		}

		// Conditionally enable (default disabled) generation of information about every class with virtual functions for use by the C++ runtime type identification features 
		// (`dynamic_cast' and `typeid'). If you don't use those parts of the language, you can save some space by using -fno-rtti. 
		// Note that exception handling uses the same information, but it will generate it as needed. 
		private static string GetRTTIFlag(CppCompileEnvironment CompileEnvironment)
		{
			return CompileEnvironment.bUseRTTI ? " -frtti" : " -fno-rtti";
		}

		private string AddFrameworkToLinkCommand(string FrameworkName, string Arg = "-framework")
		{
			string Result = "";
			if (FrameworkName.EndsWith(".framework"))
			{
				Result += " -F \"" + Path.GetDirectoryName(Path.GetFullPath(FrameworkName)) + "\"";
				FrameworkName = Path.GetFileNameWithoutExtension(FrameworkName);
			}
			Result += " " + Arg + " \"" + FrameworkName + "\"";
			return Result;
		}

		string GetLinkArguments_Global(LinkEnvironment LinkEnvironment)
		{
			string Result = "";

			Result += " -arch x86_64";
			Result += " -isysroot " + Settings.BaseSDKDir + "/MacOSX" + Settings.MacOSSDKVersion + ".sdk";
			Result += " -mmacosx-version-min=" + Settings.MacOSVersion;
			Result += " -dead_strip";

			if (Options.HasFlag(MacToolChainOptions.EnableAddressSanitizer) || 
				Options.HasFlag(MacToolChainOptions.EnableThreadSanitizer)  || 
				Options.HasFlag(MacToolChainOptions.EnableUndefinedBehaviorSanitizer))
			{
				Result += " -g";
				if (Options.HasFlag(MacToolChainOptions.EnableAddressSanitizer))
				{
					Result += " -fsanitize=address";
				}
				else if (Options.HasFlag(MacToolChainOptions.EnableThreadSanitizer))
				{
					Result += " -fsanitize=thread";
				}
				else if (Options.HasFlag(MacToolChainOptions.EnableUndefinedBehaviorSanitizer))
				{
					Result += " -fsanitize=undefined";
				}
			}

			if (LinkEnvironment.bIsBuildingDLL)
			{
				Result += " -dynamiclib";
			}

			if (LinkEnvironment.Configuration == CppConfiguration.Debug)
			{
				// Apple's Clang is not supposed to run the de-duplication pass when linking in debug configs. Xcode adds this flag automatically, we need it as well, otherwise linking would take very long
				Result += " -Wl,-no_deduplicate";
			}

			// Needed to make sure install_name_tool will be able to update paths in Mach-O headers
			Result += " -headerpad_max_install_names";

			Result += " -lc++";

			return Result;
		}

		public override CPPOutput CompileCPPFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InputFiles, DirectoryReference OutputDir, string ModuleName, IActionGraphBuilder Graph)
		{
			StringBuilder Arguments = new StringBuilder();
			StringBuilder PCHArguments = new StringBuilder();

			Arguments.Append(GetCompileArguments_Global(CompileEnvironment));

			if (CompileEnvironment.PCHAction == PCHAction.Include)
			{
				// Add the precompiled header file's path to the include path so GCC can find it.
				// This needs to be before the other include paths to ensure GCC uses it instead of the source header file.
				PCHArguments.Append(" -include \"");
				PCHArguments.Append(CompileEnvironment.PCHIncludeFilename);
				PCHArguments.Append("\"");
			}

			// Add include paths to the argument list.
			HashSet<DirectoryReference> AllIncludes = new HashSet<DirectoryReference>(CompileEnvironment.UserIncludePaths);
			AllIncludes.UnionWith(CompileEnvironment.SystemIncludePaths);
			foreach (DirectoryReference IncludePath in AllIncludes)
			{
				Arguments.Append(" -I\"");
				Arguments.Append(IncludePath);
				Arguments.Append("\"");
			}

			foreach (string Definition in CompileEnvironment.Definitions)
			{
				string DefinitionArgument = Definition.Contains("\"") ? Definition.Replace("\"", "\\\"") : Definition;
				Arguments.Append(" -D\"");
				Arguments.Append(DefinitionArgument);
				Arguments.Append("\"");
			}

			List<string> FrameworksSearchPaths = new List<string>();
			foreach (BuildFramework Framework in CompileEnvironment.AdditionalFrameworks)
			{
				string FrameworkPath = Path.GetDirectoryName(Path.GetFullPath(Framework.FrameworkName));
				if (!FrameworksSearchPaths.Contains(FrameworkPath))
				{
					Arguments.Append(" -F \"");
					Arguments.Append(FrameworkPath);
					Arguments.Append("\"");
					FrameworksSearchPaths.Add(FrameworkPath);
				}
			}

			CPPOutput Result = new CPPOutput();
			// Create a compile action for each source file.
			foreach (FileItem SourceFile in InputFiles)
			{
				Action CompileAction = Graph.CreateAction(ActionType.Compile);
				CompileAction.PrerequisiteItems.AddRange(CompileEnvironment.ForceIncludeFiles);
				CompileAction.PrerequisiteItems.AddRange(CompileEnvironment.AdditionalPrerequisites);

				string FileArguments = "";
				string Extension = Path.GetExtension(SourceFile.AbsolutePath).ToUpperInvariant();

				if (CompileEnvironment.PCHAction == PCHAction.Create)
				{
					// Compile the file as a C++ PCH.
					FileArguments += GetCompileArguments_PCH(CompileEnvironment);
					FileArguments += GetRTTIFlag(CompileEnvironment);
				}
				else if (Extension == ".C")
				{
					// Compile the file as C code.
					FileArguments += GetCompileArguments_C();
				}
				else if (Extension == ".MM")
				{
					// Compile the file as Objective-C++ code.
					FileArguments += GetCompileArguments_MM(CompileEnvironment);
					FileArguments += GetRTTIFlag(CompileEnvironment);
				}
				else if (Extension == ".M")
				{
					// Compile the file as Objective-C++ code.
					FileArguments += GetCompileArguments_M();
				}
				else
				{
					// Compile the file as C++ code.
					FileArguments += GetCompileArguments_CPP(CompileEnvironment);
					FileArguments += GetRTTIFlag(CompileEnvironment);

					// only use PCH for .cpp files
					FileArguments += PCHArguments.ToString();
				}

				foreach (FileItem ForceIncludeFile in CompileEnvironment.ForceIncludeFiles)
				{
					FileArguments += String.Format(" -include \"{0}\"", ForceIncludeFile.FileDirectory);
				}

				// Add the C++ source file and its included files to the prerequisite item list.
				CompileAction.PrerequisiteItems.Add(SourceFile);

				string OutputFilePath = null;
				if (CompileEnvironment.PCHAction == PCHAction.Create)
				{
					// Add the precompiled header file to the produced item list.
					FileItem PrecompiledHeaderFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".gch"));
					CompileAction.ProducedItems.Add(PrecompiledHeaderFile);
					Result.PCHFile = PrecompiledHeaderFile;

					// Add the parameters needed to compile the precompiled header file to the command-line.
					FileArguments += string.Format(" -o \"{0}\"", PrecompiledHeaderFile.AbsolutePath);
				}
				else
				{
					if (CompileEnvironment.PCHAction == PCHAction.Include)
					{
						CompileAction.PrerequisiteItems.Add(CompileEnvironment.PrecompiledHeaderFile);
					}
					// Add the object file to the produced item list.
					FileItem ObjectFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".o"));

					CompileAction.ProducedItems.Add(ObjectFile);
					Result.ObjectFiles.Add(ObjectFile);
					FileArguments += string.Format(" -o \"{0}\"", ObjectFile.AbsolutePath);
					OutputFilePath = ObjectFile.AbsolutePath;
				}

				// Add the source file path to the command-line.
				FileArguments += string.Format(" \"{0}\"", SourceFile.AbsolutePath);

				// Generate the included header dependency list
				if(CompileEnvironment.bGenerateDependenciesFile)
				{
					FileItem DependencyListFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".d"));
					FileArguments += string.Format(" -MD -MF\"{0}\"", DependencyListFile.AbsolutePath.Replace('\\', '/'));
					CompileAction.DependencyListFile = DependencyListFile;
					CompileAction.ProducedItems.Add(DependencyListFile);
				}

				string EscapedAdditionalArgs = "";
				if(!string.IsNullOrWhiteSpace(CompileEnvironment.AdditionalArguments))
				{
					foreach(string AdditionalArg in CompileEnvironment.AdditionalArguments.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
					{
						Match DefinitionMatch = Regex.Match(AdditionalArg, "-D\"?(?<Name>.*)=(?<Value>.*)\"?");
						if (DefinitionMatch.Success)
						{
							EscapedAdditionalArgs += string.Format(" -D{0}=\"{1}\"", DefinitionMatch.Groups["Name"].Value, DefinitionMatch.Groups["Value"].Value);
						}
						else
						{
							EscapedAdditionalArgs += " " + AdditionalArg;
						}
					}
				}

				string AllArgs = Arguments + FileArguments + EscapedAdditionalArgs;
				string CompilerPath = Settings.ToolchainDir + MacCompiler;
				
				// Analyze and then compile using the shell to perform the indirection
				string StaticAnalysisMode = Environment.GetEnvironmentVariable("CLANG_STATIC_ANALYZER_MODE");
				if(StaticAnalysisMode != null && StaticAnalysisMode != "" && OutputFilePath != null)
				{
					string TempArgs = "-c \"" + CompilerPath + " " + AllArgs + " --analyze -Wno-unused-command-line-argument -Xclang -analyzer-output=html -Xclang -analyzer-config -Xclang path-diagnostics-alternate=true -Xclang -analyzer-config -Xclang report-in-main-source-file=true -Xclang -analyzer-disable-checker -Xclang deadcode.DeadStores -o " + OutputFilePath.Replace(".o", ".html") + "; " + CompilerPath + " " + AllArgs + "\"";
					AllArgs = TempArgs;
					CompilerPath = "/bin/sh";
				}

				CompileAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
				CompileAction.CommandPath = new FileReference(CompilerPath);
				CompileAction.CommandArguments = AllArgs;
				CompileAction.CommandDescription = "Compile";
				CompileAction.StatusDescription = Path.GetFileName(SourceFile.AbsolutePath);
				CompileAction.bIsGCCCompiler = true;
				// We're already distributing the command by execution on Mac.
				CompileAction.bCanExecuteRemotely = Extension != ".C";
				CompileAction.bShouldOutputStatusDescription = true;
			}
			return Result;
		}

		private void AppendMacLine(StreamWriter Writer, string Format, params object[] Arg)
		{
			string PreLine = String.Format(Format, Arg);
			Writer.Write(PreLine + "\n");
		}

		public static string LoadEngineDisplayVersion(bool bIgnorePatchVersion = false)
		{
			return BuildVersion.TryRead(BuildVersion.GetDefaultFileName(), out BuildVersion Version)
				? String.Format("{0}.{1}.{2}", Version.MajorVersion, Version.MinorVersion, bIgnorePatchVersion ? 0 : Version.PatchVersion)
				: "4.0.0";
		}

		private string LoadEngineAPIVersion()
		{
			int CL = 0;

			if (BuildVersion.TryRead(BuildVersion.GetDefaultFileName(), out BuildVersion Version))
			{
				CL = Version.EffectiveCompatibleChangelist;
			}

			return String.Format("{0}.{1}.{2}", CL / (100 * 100), (CL / 100) % 100, CL % 100);
		}

		private void AddLibraryPathToRPaths(string Library, string ExeAbsolutePath, ref List<string> RPaths, ref string LinkCommand, bool bIsBuildingAppBundle)
		{
			string LibraryFullPath = Path.GetFullPath(Library);
 			string LibraryDir      = Path.GetDirectoryName(LibraryFullPath);
			string ExeDir          = Path.GetDirectoryName(ExeAbsolutePath);

			// Only dylibs and frameworks, and only those that are outside of Engine/Binaries/Mac and Engine/Source/ThirdParty, and outside of the folder where the executable is need an additional RPATH entry
			if ((Library.EndsWith("dylib") || Library.EndsWith(".framework"))
				&& !LibraryFullPath.Contains("/Engine/Source/ThirdParty/")
				&& LibraryDir != ExeDir 
				&& !RPaths.Contains(LibraryDir))
			{
				// macOS gatekeeper erroneously complains about not seeing the CEF3 framework in the codesigned Launcher because it's only present in one of the folders specified in RPATHs.
				// To work around this we will only add a single RPATH entry for it, for the framework stored in .app/Contents/MyEngine/ subfolder of the packaged app bundle
				bool bCanUseMultipleRPATHs = !ExeAbsolutePath.Contains("EpicGamesLauncher-Mac-Shipping") || !Library.Contains("CEF3");

				// First, add a path relative to the executable.
				string RelativePath = Utils.MakePathRelativeTo(LibraryDir, ExeDir).Replace("\\", "/");
				if (bCanUseMultipleRPATHs)
				{
					LinkCommand += " -rpath \"@loader_path/" + RelativePath + "\"";
				}

				// If building an app bundle, we also need an RPATH for use in packaged game and a separate one for staged builds
				if (bIsBuildingAppBundle)
				{
					string EngineDir = BuildTool.RootDirectory.ToString();

					// In packaged games dylibs are stored in Contents/MyEngine subfolders, for example in GameName.app/Contents/MyEngine/Engine/Binaries/ThirdParty/PhysX/Mac
					string BundleDir         = Path.GetFullPath(ExeDir + "/../../Contents/MyEngine");
					string BundleLibraryDir  = LibraryDir.Replace(EngineDir, BundleDir);
					string BundleRelativeDir = Utils.MakePathRelativeTo(BundleLibraryDir, ExeDir).Replace("\\", "/");

					LinkCommand += " -rpath \"@loader_path/" + BundleRelativeDir + "\"";

					// For staged code-based games we need additional entry if the game is not stored directly in the engine's root directory
					if (bCanUseMultipleRPATHs)
					{
						string StagedDir = Path.GetFullPath(ExeDir + "/../../../../../..");
						string StagedLibraryDir = LibraryDir.Replace(EngineDir, StagedDir);
						string StagedRelativeDir = Utils.MakePathRelativeTo(StagedLibraryDir, ExeDir).Replace("\\", "/");
						if (StagedRelativeDir != RelativePath)
						{
							LinkCommand += " -rpath \"@loader_path/" + StagedRelativeDir + "\"";
						}
					}
				}

				RPaths.Add(LibraryDir);
			}
		}

		public override CPPOutput CompileRCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph)
		{
			return null;
		}

		public override FileItem LinkFiles(LinkEnvironment LinkEnvironment, bool bBuildImportLibraryOnly, IActionGraphBuilder Graph)
		{
			bool bIsBuildingLibrary = LinkEnvironment.bIsBuildingLibrary || bBuildImportLibraryOnly;

			// Create an action that invokes the linker.
			Action LinkAction = Graph.CreateAction(ActionType.Link);

			LinkAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
			LinkAction.CommandPath = BuildHostPlatform.Current.ShellPath;
			LinkAction.CommandDescription = "Link";

			// Tell the action that we're building an import library here and it should conditionally be
			// ignored as a prerequisite for other actions
			LinkAction.bProducesImportLibrary = !Utils.IsRunningOnMono && (bBuildImportLibraryOnly || LinkEnvironment.bIsBuildingDLL);


			string Linker = bIsBuildingLibrary ? MacArchiver : MacLinker;
			string EngineDisplayVersion = LoadEngineDisplayVersion(true);
			string EngineAPIVersion = LoadEngineAPIVersion();

			string VersionArg = LinkEnvironment.bIsBuildingDLL ? " -current_version " + EngineAPIVersion + " -compatibility_version " + EngineDisplayVersion : "";
			string LinkCommand = Settings.ToolchainDir + Linker + VersionArg + " " + (bIsBuildingLibrary ? "-static" : GetLinkArguments_Global(LinkEnvironment));

			if (!bIsBuildingLibrary)
			{
				LinkCommand += " -rpath @loader_path/ -rpath @executable_path/";
			}

			bool bIsBuildingAppBundle = !(LinkEnvironment.bIsBuildingDLL || LinkEnvironment.bIsBuildingLibrary || LinkEnvironment.bIsBuildingConsoleApplication);
			if (bIsBuildingAppBundle)
			{
				LinkCommand += " -rpath @executable_path/../../../";
			}

			List<string> RPaths = new List<string>();

			// Add the output file as a production of the link action.
			FileItem OutputFile = FileItem.GetItemByFileReference(LinkEnvironment.OutputFilePath);
			string AbsolutePath = OutputFile.AbsolutePath.Replace("\\", "/");

			// To solve the problem with cross dependencies, for now we create a broken dylib that does not link with other engine dylibs.
			// This is fixed in later step, FixDylibDependencies. For this and to know what libraries to copy whilst creating an app bundle,
			// we gather the list of engine dylibs.
			List<string> EngineAndGameLibraries = new List<string>();
			if (!bIsBuildingLibrary)
			{
				// Add the additional libraries to the argument list.
				foreach (string AdditionalLibrary in LinkEnvironment.AdditionalLibraries)
				{
					// Can't link dynamic libraries when creating a static one
					if (bIsBuildingLibrary && 
						(Path.GetExtension(AdditionalLibrary) == ".dylib" || AdditionalLibrary == "z"))
					{
						continue;
					}

					if (Path.GetDirectoryName(AdditionalLibrary) != "" &&
						(Path.GetDirectoryName(AdditionalLibrary).Contains("Binaries/Mac") || Path.GetDirectoryName(AdditionalLibrary).Contains("Binaries\\Mac")))
					{
						// It's an engine or game dylib. Save it for later
						EngineAndGameLibraries.Add(Path.GetFullPath(AdditionalLibrary));

						if (LinkEnvironment.bIsCrossReferenced == false)
						{
							FileItem EngineLibDependency = FileItem.GetItemByPath(AdditionalLibrary);
							LinkAction.PrerequisiteItems.Add(EngineLibDependency);
						}
					}
					else if (AdditionalLibrary.Contains(".framework/"))
					{
						LinkCommand += string.Format(" \"{0}\"", AdditionalLibrary);
					}
					else if (string.IsNullOrEmpty(Path.GetDirectoryName(AdditionalLibrary)) && 
						     string.IsNullOrEmpty(Path.GetExtension(AdditionalLibrary)))
					{
						LinkCommand += string.Format(" -l{0}", AdditionalLibrary);
					}
					else
					{
						LinkCommand += string.Format(" \"{0}\"", Path.GetFullPath(AdditionalLibrary));
					}

					AddLibraryPathToRPaths(AdditionalLibrary, AbsolutePath, ref RPaths, ref LinkCommand, bIsBuildingAppBundle);
				}

				foreach (string AdditionalLibrary in LinkEnvironment.DelayLoadDLLs)
				{
					// Can't link dynamic libraries when creating a static one
					if (bIsBuildingLibrary && 
						(Path.GetExtension(AdditionalLibrary) == ".dylib" || AdditionalLibrary == "z"))
					{
						continue;
					}

					LinkCommand += string.Format(" -weak_library \"{0}\"", Path.GetFullPath(AdditionalLibrary));

					AddLibraryPathToRPaths(AdditionalLibrary, AbsolutePath, ref RPaths, ref LinkCommand, bIsBuildingAppBundle);
				}
			}

			// Add frameworks
			Dictionary<string, bool> AllFrameworks = new Dictionary<string, bool>();
			foreach (string Framework in LinkEnvironment.Frameworks)
			{
				if (!AllFrameworks.ContainsKey(Framework))
				{
					AllFrameworks.Add(Framework, false);
				}
			}
			foreach (BuildFramework Framework in LinkEnvironment.AdditionalFrameworks)
			{
				if (!AllFrameworks.ContainsKey(Framework.FrameworkName))
				{
					AllFrameworks.Add(Framework.FrameworkName, false);
				}
			}
			foreach (string Framework in LinkEnvironment.WeakFrameworks)
			{
				if (!AllFrameworks.ContainsKey(Framework))
				{
					AllFrameworks.Add(Framework, true);
				}
			}

			if (!bIsBuildingLibrary)
			{
				foreach (KeyValuePair<string, bool> Framework in AllFrameworks)
				{
					LinkCommand += AddFrameworkToLinkCommand(Framework.Key, Framework.Value ? "-weak_framework" : "-framework");
					AddLibraryPathToRPaths(Framework.Key, AbsolutePath, ref RPaths, ref LinkCommand, bIsBuildingAppBundle);
				}
			}

			List<string> InputFileNames = new List<string>();
			foreach (FileItem InputFile in LinkEnvironment.InputFiles)
			{
				InputFileNames.Add(string.Format("\"{0}\"", InputFile.AbsolutePath));
				LinkAction.PrerequisiteItems.Add(InputFile);
			}

			foreach (string Filename in InputFileNames)
			{
				LinkCommand += " " + Filename;
			}

			if (LinkEnvironment.bIsBuildingDLL)
			{
				// Add the output file to the command-line.
				string InstallName = LinkEnvironment.InstallName;
				if(InstallName == null)
				{
					string DylibsPath = "@rpath";
					InstallName = string.Format("{0}/{1}", DylibsPath, Path.GetFileName(OutputFile.AbsolutePath));
				}

				LinkCommand += string.Format(" -install_name {0}", InstallName);
			}

			if (!bIsBuildingLibrary)
			{
				if (BuildTool.IsEngineInstalled() || 
					(Utils.IsRunningOnMono && LinkEnvironment.bIsCrossReferenced == false))
				{
					foreach (string Library in EngineAndGameLibraries)
					{
						LinkCommand += " \"" + Library + "\"";
					}
				}
				else
				{
					// Tell linker to ignore unresolved symbols, so we don't have a problem with cross dependent dylibs that do not exist yet.
					// This is fixed in later step, FixDylibDependencies.
					LinkCommand += string.Format(" -undefined dynamic_lookup");
				}

				// Write the MAP file to the output directory.
				if (LinkEnvironment.bCreateMapFile)
				{
					string MapFileBaseName = OutputFile.AbsolutePath;

					int AppIdx = MapFileBaseName.IndexOf(".app/Contents/MacOS");
					if(AppIdx != -1)
					{
						MapFileBaseName = MapFileBaseName.Substring(0, AppIdx);
					}

					FileReference MapFilePath = new FileReference(MapFileBaseName + ".map");
					FileItem MapFile = FileItem.GetItemByFileReference(MapFilePath);
					LinkCommand += string.Format(" -Wl,-map,\"{0}\"", MapFilePath);
					LinkAction.ProducedItems.Add(MapFile);
				}
			}

			// Add the output file to the command-line.
			LinkCommand += string.Format(" -o \"{0}\"", OutputFile.AbsolutePath);

			// Add the additional arguments specified by the environment.
			LinkCommand += LinkEnvironment.AdditionalArguments;

			LinkAction.CommandArguments = "-c '" + LinkCommand + "'";

			// Only execute linking on the local Mac.
			LinkAction.bCanExecuteRemotely = false;

			LinkAction.StatusDescription = Path.GetFileName(OutputFile.AbsolutePath);

			LinkAction.ProducedItems.Add(OutputFile);

			if (!DirectoryReference.Exists(LinkEnvironment.IntermediateDirectory))
			{
				return OutputFile;
			}

			if (!bIsBuildingLibrary)
			{
				// Prepare a script that will run later, once every dylibs and the executable are created. This script will be called by action created in FixDylibDependencies()
				FileReference FixDylibDepsScriptPath = FileReference.Combine(LinkEnvironment.LocalShadowDirectory, "FixDylibDependencies.sh");
				if (!bHasWipedFixDylibScript)
				{
					if (FileReference.Exists(FixDylibDepsScriptPath))
					{
						FileReference.Delete(FixDylibDepsScriptPath);
					}
					bHasWipedFixDylibScript = true;
				}

				if (!DirectoryReference.Exists(LinkEnvironment.LocalShadowDirectory))
				{
					DirectoryReference.CreateDirectory(LinkEnvironment.LocalShadowDirectory);
				}

				StreamWriter FixDylibDepsScript = File.AppendText(FixDylibDepsScriptPath.FullName);

				if (LinkEnvironment.bIsCrossReferenced || !Utils.IsRunningOnMono)
				{
					string EngineAndGameLibrariesString = "";
					foreach (string Library in EngineAndGameLibraries)
					{
						EngineAndGameLibrariesString += " \"" + Library + "\"";
					}
					string FixDylibLine = "pushd \"" + Directory.GetCurrentDirectory() + "\"  > /dev/null; ";
					FixDylibLine += string.Format("TIMESTAMP=`stat -n -f \"%Sm\" -t \"%Y%m%d%H%M.%S\" \"{0}\"`; ", OutputFile.AbsolutePath);
					FixDylibLine += LinkCommand.Replace("-undefined dynamic_lookup", EngineAndGameLibrariesString).Replace("$", "\\$");
					FixDylibLine += string.Format("; touch -t $TIMESTAMP \"{0}\"; if [[ $? -ne 0 ]]; then exit 1; fi; ", OutputFile.AbsolutePath);
					FixDylibLine += "popd > /dev/null";
					AppendMacLine(FixDylibDepsScript, FixDylibLine);
				}

				FixDylibDepsScript.Close();

				// For non-console application, prepare a script that will create the app bundle. It'll be run by FinalizeAppBundle action
				if (bIsBuildingAppBundle)
				{
					FileReference FinalizeAppBundleScriptPath = FileReference.Combine(LinkEnvironment.IntermediateDirectory, "FinalizeAppBundle.sh");
					StreamWriter FinalizeAppBundleScript = File.CreateText(FinalizeAppBundleScriptPath.FullName);

					string BinariesPath = Path.GetDirectoryName(OutputFile.AbsolutePath);
					BinariesPath = Path.GetDirectoryName(BinariesPath.Substring(0, BinariesPath.IndexOf(".app")));

					AppendMacLine(FinalizeAppBundleScript, "#!/bin/sh");
					AppendMacLine(FinalizeAppBundleScript, "cd \"{0}\"", BinariesPath.Replace("$", "\\$"));

					string BundleVersion = LinkEnvironment.BundleVersion;
					if(BundleVersion == null)
					{
						BundleVersion = LoadEngineDisplayVersion();
					}

					string ExeName = Path.GetFileName(OutputFile.AbsolutePath);
					string[] ExeNameParts = ExeName.Split('-');
					string GameName = ExeNameParts[0];

                    // bundle identifier
                    // plist replacements
                    DirectoryReference DirRef = BuildTool.GetRemoteIniPath().HasValue() ? 
						new DirectoryReference(BuildTool.GetRemoteIniPath()) : 
						(ProjectFile?.Directory);

                    ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, DirRef, BuildTargetPlatform.IOS);

					Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "BundleIdentifier", out string BundleIdentifier);

					string ProjectName = GameName;
					FileReference UProjectFilePath = ProjectFile;

					if (UProjectFilePath != null)
					{
						ProjectName = UProjectFilePath.GetFileNameWithoutAnyExtensions();
					}

					if (GameName == "EpicGamesBootstrapLauncher")
					{
						GameName = "EpicGamesLauncher";
					}
					else if (GameName == "MyEngine" && UProjectFilePath != null)
					{
						GameName = UProjectFilePath.GetFileNameWithoutAnyExtensions();
					}

					AppendMacLine(FinalizeAppBundleScript, "mkdir -p \"{0}.app/Contents/MacOS\"", ExeName);
					AppendMacLine(FinalizeAppBundleScript, "mkdir -p \"{0}.app/Contents/Resources\"", ExeName);
					
					string CustomResourcesPath = "";
					string CustomBuildPath = "";

					if (UProjectFilePath == null)
					{
						string[] TargetFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), GameName + ".Target.cs", SearchOption.AllDirectories);
						if (TargetFiles.Length == 1)
						{
							CustomResourcesPath = Path.GetDirectoryName(TargetFiles[0]) + "/Resources/Mac";
							CustomBuildPath     = Path.GetDirectoryName(TargetFiles[0]) + "../Build/Mac";
						}
						else
						{
							Log.TraceWarning("Found {0} Target.cs files for {1} in alldir search of directory {2}", TargetFiles.Length, GameName, Directory.GetCurrentDirectory());
						}
					}
					else
					{
						bool bIsLauncherProduct = ExeName.StartsWith("Launcher") || ExeName.StartsWith("BootstrapLauncher");
						string ResourceParentFolderName = bIsLauncherProduct ? "Application" : GameName;

						CustomResourcesPath = Path.GetDirectoryName(UProjectFilePath.FullName) + "/Source/" + ResourceParentFolderName + "/Resources/Mac";

						if (!Directory.Exists(CustomResourcesPath))
						{
							CustomResourcesPath = Path.GetDirectoryName(UProjectFilePath.FullName) + "/Source/" + ProjectName + "/Resources/Mac";
						}

						CustomBuildPath = Path.GetDirectoryName(UProjectFilePath.FullName) + "/Build/Mac";
					}

					bool bBuildingEditor = GameName.EndsWith("Editor");

					string IconName = "MyEngine";
					string EngineSourcePath = Directory.GetCurrentDirectory().Replace("$", "\\$");

					// Copy resources
					string DefaultIcon = EngineSourcePath + "/Runtime/Launch/Resources/Mac/" + IconName + ".icns";
					string CustomIcon;

					if (bBuildingEditor)
					{
						CustomIcon = DefaultIcon;
					}
					else
					{
						CustomIcon = CustomBuildPath + "/Application.icns";
						if (!File.Exists(CustomIcon))
						{
							CustomIcon = CustomResourcesPath + "/" + GameName + ".icns";
							if (!File.Exists(CustomIcon))
							{
								CustomIcon = DefaultIcon;
							}
						}
					}

					AppendMacLine(FinalizeAppBundleScript, FormatCopyCommand(CustomIcon, String.Format("{0}.app/Contents/Resources/{1}.icns", ExeName, GameName)));

					if (ExeName.StartsWith("Editor"))
					{
						AppendMacLine(FinalizeAppBundleScript, FormatCopyCommand(String.Format("{0}/Runtime/Launch/Resources/Mac/UProject.icns", EngineSourcePath), String.Format("{0}.app/Contents/Resources/UProject.icns", ExeName)));
					}

					string InfoPlistFile = CustomResourcesPath + (bBuildingEditor ? "/Info-Editor.plist" : "/Info.plist");
					if (!File.Exists(InfoPlistFile))
					{
						InfoPlistFile = EngineSourcePath + "/Runtime/Launch/Resources/Mac/" + (bBuildingEditor ? "Info-Editor.plist" : "Info.plist");
					}

					string TempInfoPlist = "$TMPDIR/TempInfo.plist";
					AppendMacLine(FinalizeAppBundleScript, FormatCopyCommand(InfoPlistFile, TempInfoPlist));

					// Fix contents of Info.plist
					AppendMacLine(FinalizeAppBundleScript, "sed -i \"\" -e \"s/\\${0}/{1}/g\" \"{2}\"", "{EXECUTABLE_NAME}", ExeName, TempInfoPlist);
					AppendMacLine(FinalizeAppBundleScript, "sed -i \"\" -e \"s/\\${0}/{1}/g\" \"{2}\"", "{APP_NAME}", bBuildingEditor ? ("com.epicgames." + GameName) : (BundleIdentifier.Replace("[PROJECT_NAME]", GameName).Replace("_", "")), TempInfoPlist);
					AppendMacLine(FinalizeAppBundleScript, "sed -i \"\" -e \"s/\\${0}/{1}/g\" \"{2}\"", "{MACOSX_DEPLOYMENT_TARGET}", Settings.MinMacOSVersion, TempInfoPlist);
					AppendMacLine(FinalizeAppBundleScript, "sed -i \"\" -e \"s/\\${0}/{1}/g\" \"{2}\"", "{ICON_NAME}", GameName, TempInfoPlist);
					AppendMacLine(FinalizeAppBundleScript, "sed -i \"\" -e \"s/\\${0}/{1}/g\" \"{2}\"", "{BUNDLE_VERSION}", BundleVersion, TempInfoPlist);

					// Copy it into place
					AppendMacLine(FinalizeAppBundleScript, FormatCopyCommand(TempInfoPlist, String.Format("{0}.app/Contents/Info.plist", ExeName)));
					AppendMacLine(FinalizeAppBundleScript, "chmod 644 \"{0}.app/Contents/Info.plist\"", ExeName);

					// Generate PkgInfo file
					string TempPkgInfo = "$TMPDIR/TempPkgInfo";
					AppendMacLine(FinalizeAppBundleScript, "echo 'echo -n \"APPL????\"' | bash > \"{0}\"", TempPkgInfo);
					AppendMacLine(FinalizeAppBundleScript, FormatCopyCommand(TempPkgInfo, String.Format("{0}.app/Contents/PkgInfo", ExeName)));

					// Make sure OS X knows the bundle was updated
					AppendMacLine(FinalizeAppBundleScript, "touch -c \"{0}.app\"", ExeName);

					FinalizeAppBundleScript.Close();
				}
			}

			return OutputFile;
		}

		static string FormatCopyCommand(string SourceFile, string TargetFile)
		{
			return String.Format("rsync --checksum \"{0}\" \"{1}\"", SourceFile, TargetFile);
		}

		FileItem FixDylibDependencies(LinkEnvironment LinkEnvironment, FileItem Executable, IActionGraphBuilder Graph)
		{
			Action FixDylibAction = Graph.CreateAction(ActionType.PostBuildStep);
			FixDylibAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
			FixDylibAction.CommandPath = BuildHostPlatform.Current.ShellPath;
			FixDylibAction.CommandDescription = "";

			// Call the FixDylibDependencies.sh script which will link the dylibs and the main executable, this time proper ones, as it's called
			// once all are already created, so the cross dependency problem no longer prevents linking.
			// The script is deleted after it's executed so it's empty when we start appending link commands for the next executable.
			FileItem FixDylibDepsScript = FileItem.GetItemByFileReference(FileReference.Combine(LinkEnvironment.LocalShadowDirectory, "FixDylibDependencies.sh"));

			FixDylibAction.CommandArguments = "-c 'chmod +x \"" + FixDylibDepsScript.AbsolutePath + "\"; \"" + FixDylibDepsScript.AbsolutePath + "\"; if [[ $? -ne 0 ]]; then exit 1; fi; ";

			// Make sure this action is executed after all the dylibs and the main executable are created

			foreach (FileItem Dependency in BundleDependencies)
			{
				FixDylibAction.PrerequisiteItems.Add(Dependency);
			}

			FixDylibAction.StatusDescription = string.Format("Fixing dylib dependencies for {0}", Path.GetFileName(Executable.AbsolutePath));
			FixDylibAction.bCanExecuteRemotely = false;

			FileItem OutputFile = FileItem.GetItemByFileReference(FileReference.Combine(LinkEnvironment.LocalShadowDirectory, Path.GetFileNameWithoutExtension(Executable.AbsolutePath) + ".link"));

			FixDylibAction.CommandArguments += "echo \"Dummy\" >> \"" + OutputFile.AbsolutePath + "\"";
			FixDylibAction.CommandArguments += "'";

			FixDylibAction.ProducedItems.Add(OutputFile);

			return OutputFile;
		}

		// MachOBinary - FileItem describing the executable or dylib to generate debug info for
		public FileItem GenerateDebugInfo(FileItem MachOBinary, LinkEnvironment LinkEnvironment, IActionGraphBuilder Graph)
		{
			string BinaryPath = MachOBinary.AbsolutePath;
			if (BinaryPath.Contains(".app"))
			{
				while (BinaryPath.Contains(".app"))
				{
					BinaryPath = Path.GetDirectoryName(BinaryPath);
				}
				BinaryPath = Path.Combine(BinaryPath, Path.GetFileName(Path.ChangeExtension(MachOBinary.AbsolutePath, ".dSYM")));
			}
			else
			{
				BinaryPath = Path.ChangeExtension(BinaryPath, ".dSYM");
			}

			FileItem OutputFile = FileItem.GetItemByPath(BinaryPath);

			// Delete on the local machine
			if (Directory.Exists(OutputFile.AbsolutePath))
			{
				Directory.Delete(OutputFile.AbsolutePath, true);
			}

			// Make the compile action
			Action GenDebugAction = Graph.CreateAction(ActionType.GenerateDebugInfo);
			GenDebugAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
			GenDebugAction.CommandPath = BuildHostPlatform.Current.ShellPath;

			// Deletes ay existing file on the building machine. Also, waits 30 seconds,
			// if needed, for the input file to be created in an attempt to work around
			// a problem where dsymutil would exit with an error saying the input file did not exist.
			// Note that the source and dest are switched from a copy command
			string DsymutilPath = GetDsymutilPath(out string ExtraOptions, bIsForLTOBuild: false);
			
			GenDebugAction.CommandArguments = string.Format
			(
				"-c 'rm -rf \"{2}\"; for i in {{1..30}}; do if [ -f \"{1}\" ] ; then break; else echo\"Waiting for {1} before generating dSYM file.\"; sleep 1; fi; done; \"{0}\" {3} -f \"{1}\" -o \"{2}\"'",
				DsymutilPath,
				MachOBinary.AbsolutePath,
				OutputFile.AbsolutePath,
				ExtraOptions
			);

			if (LinkEnvironment.bIsCrossReferenced)
			{
				GenDebugAction.PrerequisiteItems.Add(FixDylibOutputFile);
			}

			GenDebugAction.PrerequisiteItems.Add(MachOBinary);
			GenDebugAction.ProducedItems.Add(OutputFile);
			GenDebugAction.CommandDescription = "";
			GenDebugAction.StatusDescription = "Generating " + Path.GetFileName(BinaryPath);
			GenDebugAction.bCanExecuteRemotely = false;

			return OutputFile;
		}

		// Creates app bundle for a given executable
		FileItem FinalizeAppBundle(LinkEnvironment LinkEnvironment, FileItem Executable, FileItem FixDylibOutputFile, IActionGraphBuilder GraphTobeExecuted)
		{
			// Make a file item for the source and destination files
			string FullDestPath = Executable.AbsolutePath.Substring(0, Executable.AbsolutePath.IndexOf(".app") + 4);
			FileItem DestFile = FileItem.GetItemByPath(FullDestPath);

			// Make the compile action
			Action FinalizeAppBundleAction = GraphTobeExecuted.CreateAction(ActionType.CreateAppBundle);
			FinalizeAppBundleAction.WorkingDirectory = BuildTool.EngineSourceDirectory; // Path.GetFullPath(".");
			FinalizeAppBundleAction.CommandPath = BuildHostPlatform.Current.ShellPath;
			FinalizeAppBundleAction.CommandDescription = "";

			// make path to the script
			FileItem BundleScript = FileItem.GetItemByFileReference(FileReference.Combine(LinkEnvironment.IntermediateDirectory, "FinalizeAppBundle.sh"));

			FinalizeAppBundleAction.CommandArguments = "\"" + BundleScript.AbsolutePath + "\"";
			FinalizeAppBundleAction.PrerequisiteItems.Add(FixDylibOutputFile);
			FinalizeAppBundleAction.ProducedItems.Add(DestFile);
			FinalizeAppBundleAction.StatusDescription = string.Format("Finalizing app bundle: {0}.app", Path.GetFileName(Executable.AbsolutePath));
			FinalizeAppBundleAction.bCanExecuteRemotely = false;

			return DestFile;
		}

		FileItem CopyBundleResource(BuildBundleResource Resource, FileItem Executable, DirectoryReference BundleDirectory, IActionGraphBuilder Graph)
		{
			Action CopyAction = Graph.CreateAction(ActionType.CreateAppBundle);
			CopyAction.WorkingDirectory = BuildTool.EngineSourceDirectory; // Path.GetFullPath(".");
			CopyAction.CommandPath = BuildHostPlatform.Current.ShellPath;
			CopyAction.CommandDescription = "";

			string BundlePath = BundleDirectory.FullName;
			string SourcePath = Path.Combine(Path.GetFullPath("."), Resource.ResourcePath);
			string TargetPath = Path.Combine(BundlePath, "Contents", Resource.BundleContentsSubdir, Path.GetFileName(Resource.ResourcePath));

			FileItem TargetItem = FileItem.GetItemByPath(TargetPath);

			CopyAction.CommandArguments = string.Format("-c 'cp -f -R \"{0}\" \"{1}\"; touch -c \"{2}\"'", SourcePath, Path.GetDirectoryName(TargetPath).Replace('\\', '/') + "/", TargetPath.Replace('\\', '/'));
			CopyAction.PrerequisiteItems.Add(Executable);
			CopyAction.ProducedItems.Add(TargetItem);
			CopyAction.bShouldOutputStatusDescription = Resource.bShouldLog;
			CopyAction.StatusDescription = string.Format("Copying {0} to app bundle", Path.GetFileName(Resource.ResourcePath));
			CopyAction.bCanExecuteRemotely = false;

			return TargetItem;
		}

		public override void SetupBundleDependencies(List<BuildBinary> Binaries, string GameName)
		{
			base.SetupBundleDependencies(Binaries, GameName);

			foreach (BuildBinary Binary in Binaries)
			{
				BundleDependencies.Add(FileItem.GetItemByFileReference(Binary.OutputFilePath));
			}
		}

		static private DirectoryReference BundleContentsDirectory;

		public override void ModifyBuildProducts(ReadOnlyTargetRules Target, BuildBinary Binary, List<string> Libraries, List<BuildBundleResource> BundleResources, Dictionary<FileReference, BuildProductType> BuildProducts)
		{
			if (Target.bUsePDBFiles == true)
			{
				KeyValuePair<FileReference, BuildProductType>[] BuildProductsArray = BuildProducts.ToArray();

				foreach (KeyValuePair<FileReference, BuildProductType> BuildProductPair in BuildProductsArray)
				{
					string[] DebugExtensions = new string[] {};
					switch (BuildProductPair.Value)
					{
						case BuildProductType.Executable:
							DebugExtensions = BuildPlatform.GetBuildPlatform(Target.Platform).GetDebugInfoExtensions(Target, BuildBinaryType.Executable);
							break;
						case BuildProductType.DynamicLibrary:
							DebugExtensions = BuildPlatform.GetBuildPlatform(Target.Platform).GetDebugInfoExtensions(Target, BuildBinaryType.DynamicLinkLibrary);
							break;
					}
					string DSYMExtension = Array.Find(DebugExtensions, element => element == ".dSYM");
					if (!string.IsNullOrEmpty(DSYMExtension))
					{
						string BinaryPath = BuildProductPair.Key.FullName;
						if(BinaryPath.Contains(".app"))
						{
							while(BinaryPath.Contains(".app"))
							{
								BinaryPath = Path.GetDirectoryName(BinaryPath);
							}
							BinaryPath = Path.Combine(BinaryPath, BuildProductPair.Key.GetFileName());
							BinaryPath = Path.ChangeExtension(BinaryPath, DSYMExtension);
							FileReference Ref = new FileReference(BinaryPath);
							BuildProducts[Ref] = BuildProductType.SymbolFile;
						}
					}
					else if(BuildProductPair.Value == BuildProductType.SymbolFile && BuildProductPair.Key.FullName.Contains(".app"))
					{
						BuildProducts.Remove(BuildProductPair.Key);
					}
					if(BuildProductPair.Value == BuildProductType.DynamicLibrary && Target.bCreateMapFile)
					{
						BuildProducts.Add(new FileReference(BuildProductPair.Key.FullName + ".map"), BuildProductType.MapFile);
					}
				}
			}

			if (Target.bIsBuildingConsoleApplication)
			{
				return;
			}

			if (BundleContentsDirectory == null && Binary.Type == BuildBinaryType.Executable)
			{
				BundleContentsDirectory = Binary.OutputFilePath.Directory.ParentDirectory;
			}

			// We need to know what third party dylibs would be copied to the bundle
			if (Binary.Type != BuildBinaryType.StaticLibrary)
			{
			    foreach (BuildBundleResource Resource in BundleResources)
				{
					if (Directory.Exists(Resource.ResourcePath))
					{
						foreach (string ResourceFile in Directory.GetFiles(Resource.ResourcePath, "*", SearchOption.AllDirectories))
						{
							BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, Resource.BundleContentsSubdir, ResourceFile.Substring(Path.GetDirectoryName(Resource.ResourcePath).Length + 1)), BuildProductType.RequiredResource);
						}
					}
					else if (BundleContentsDirectory != null)
					{
						BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, Resource.BundleContentsSubdir, Path.GetFileName(Resource.ResourcePath)), BuildProductType.RequiredResource);
					}
				}
			}

			if (Binary.Type == BuildBinaryType.Executable)
			{
				// And we also need all the resources
				BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, "Info.plist"), BuildProductType.RequiredResource);
				BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, "PkgInfo"), BuildProductType.RequiredResource);

				if (Target.Type == TargetType.Editor)
				{
					BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, "Resources/MyEngineEditor.icns"), BuildProductType.RequiredResource);
					BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, "Resources/UProject.icns"), BuildProductType.RequiredResource);
				}
				else
				{
					string IconName = Target.Name;
					if (IconName == "EpicGamesBootstrapLauncher")
					{
						IconName = "EpicGamesLauncher";
					}
					BuildProducts.Add(FileReference.Combine(BundleContentsDirectory, "Resources/" + IconName + ".icns"), BuildProductType.RequiredResource);
				}
			}
		}

		private readonly List<FileItem> DebugInfoFiles = new List<FileItem>();

		public override void FinalizeOutput(ReadOnlyTargetRules Target, TargetMakefile Makefile)
		{
			// Re-add any .dSYM files that may have been stripped out.
			List<string> OutputFiles = Makefile.OutputItems.Select(Item => Path.ChangeExtension(Item.FullName, ".dSYM")).Distinct().ToList();
			foreach (FileItem DebugItem in DebugInfoFiles)
			{
				if(OutputFiles.Any(Item => string.Equals(Item, DebugItem.FullName, StringComparison.InvariantCultureIgnoreCase)))
				{
					Makefile.OutputItems.Add(DebugItem);
				}
			}
		}

		public override ICollection<FileItem> PostBuild(FileItem Executable, LinkEnvironment BinaryLinkEnvironment, IActionGraphBuilder Graph)
		{
			ICollection<FileItem> OutputFiles = base.PostBuild(Executable, BinaryLinkEnvironment, Graph);

			if (BinaryLinkEnvironment.bIsBuildingLibrary)
			{
				return OutputFiles;
			}

			if(BinaryLinkEnvironment.BundleDirectory != null)
			{
				foreach (BuildBundleResource Resource in BinaryLinkEnvironment.AdditionalBundleResources)
				{
					OutputFiles.Add(CopyBundleResource(Resource, Executable, BinaryLinkEnvironment.BundleDirectory, Graph));
				}
			}

			// For Mac, generate the dSYM file if the config file is set to do so
			if (BinaryLinkEnvironment.bUsePDBFiles == true)
			{
				// We want dsyms to be created after all dylib dependencies are fixed. If FixDylibDependencies action was not created yet, save the info for later.
				if (FixDylibOutputFile != null)
				{
					DebugInfoFiles.Add(GenerateDebugInfo(Executable, BinaryLinkEnvironment, Graph));
				}
				else
				{
					ExecutablesThatNeedDsyms.Add(Executable);
				}
			}

			if ((BinaryLinkEnvironment.bIsBuildingDLL && (Options & MacToolChainOptions.OutputDylib) == 0) || (BinaryLinkEnvironment.bIsBuildingConsoleApplication && Executable.Name.Contains("Editor") && Executable.Name.EndsWith("-Cmd")))
			{
				return OutputFiles;
			}

			FixDylibOutputFile = FixDylibDependencies(BinaryLinkEnvironment, Executable, Graph);
			OutputFiles.Add(FixDylibOutputFile);
			if (!BinaryLinkEnvironment.bIsBuildingConsoleApplication)
			{
				OutputFiles.Add(FinalizeAppBundle(BinaryLinkEnvironment, Executable, FixDylibOutputFile, Graph));
			}

			// Add dsyms that we couldn't add before FixDylibDependencies action was created
			foreach (FileItem Exe in ExecutablesThatNeedDsyms)
			{
				DebugInfoFiles.Add(GenerateDebugInfo(Exe, BinaryLinkEnvironment, Graph));
			}
			ExecutablesThatNeedDsyms.Clear();

			return OutputFiles;
		}

		private FileItem FixDylibOutputFile = null;
		private readonly List<FileItem> ExecutablesThatNeedDsyms = new List<FileItem>();

		public void StripSymbols(FileReference SourceFile, FileReference TargetFile)
		{
			StripSymbolsWithXcode(SourceFile, TargetFile, Settings.ToolchainDir);
		}
	};
}
