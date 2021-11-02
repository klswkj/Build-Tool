﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using BuildToolUtilities;

namespace BuildTool
{
	internal static class VCEnvironmentFastbuildExtensions
	{
		// This replaces the VCToolPath64 readonly property that was available in 4.19 . Note that GetVCToolPath64
		// is still used internally, but the property for it is no longer exposed.
		// 
		public static DirectoryReference GetToolPath(this VCEnvironment VCEnv)
		{
			return VCEnv.CompilerPath.Directory;
		}

		public static DirectoryReference GetVCInstallDirectory(this VCEnvironment VCEnv)
		{
			// TODO: Check registry values before moving up ParentDirectories (as in 4.19)
			return VCEnv.ToolChainDir.ParentDirectory.ParentDirectory.ParentDirectory;
		}
	}

	///////////////////////////////////////////////////////////////////////

	internal enum FASTBuildCacheMode
	{
		ReadWrite, // This machine will both read and write to the cache
		ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
		WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
	}

	///////////////////////////////////////////////////////////////////////

	class FASTBuild : ActionExecutor
	{
		public readonly static string DefaultExecutableBasePath = Path.Combine(BuildTool.EngineDirectory.FullName, "Extras", "ThirdPartyNotUE", "FASTBuild");

		// Used to specify the location of fbuild.exe if the distributed binary isn't being used
		[XMLConfigFile]
		public static string FBuildExecutablePath = null;

		// Controls network build distribution
		[XMLConfigFile]
		public static bool bEnableDistribution = true;

		// Used to specify the location of the brokerage. If null, FASTBuild will fall back to checking FASTBUILD_BROKERAGE_PATH
		[XMLConfigFile]
		public static string FBuildBrokeragePath = null;

		// Used to specify the FASTBuild coordinator IP or network name.
		// If null, FASTBuild will fall back to checking FASTBUILD_COORDINATOR
		[XMLConfigFile]
		public static string FBuildCoordinator = null;

        #region CACHING
        // Controls whether to use caching at all. CachePath and FASTCacheMode are only relevant if this is enabled.
        [XMLConfigFile]
		public static bool bEnableCaching = true;

		// Cache access mode - only relevant if bEnableCaching is true;
		[XMLConfigFile]
		public static FASTBuildCacheMode CacheMode = FASTBuildCacheMode.ReadOnly;

		// Used to specify the location of the cache. If null, FASTBuild will fall back to checking FASTBUILD_CACHE_PATH
		[XMLConfigFile]
		public static string FBuildCachePath = null;
        #endregion CACHING

        #region Misc Options
        // Whether to force remote
        [XMLConfigFile]
		public static bool bForceRemote = false;

		// Whether to stop on error
		[XMLConfigFile]
		public static bool bStopOnError = false;

		// Which MSVC CRT Redist version to use
		[XMLConfigFile]
		public static String MSVCCRTRedistVersion = "";
        #endregion Misc Options

        public override string OutputName 
			=> "FASTBuild";

        public static string ExecutableName 
			=> Path.GetFileName(GetExecutablePath());

        public static string GetExecutablePath()
		{
			if (string.IsNullOrEmpty(FBuildExecutablePath))
			{
				string EnvPath = Environment.GetEnvironmentVariable("FASTBUILD_EXECUTABLE_PATH");
				if (EnvPath.HasValue())
				{
					FBuildExecutablePath = EnvPath;
				}
			}

			return FBuildExecutablePath;
		}

		public static string GetCachePath()
		{
			if (string.IsNullOrEmpty(FBuildCachePath))
			{
				string EnvPath = Environment.GetEnvironmentVariable(Tag.EnvVar.FASTBuildCachePath);
				if (EnvPath.HasValue())
				{
					FBuildCachePath = EnvPath;
				}
			}

			return FBuildCachePath;
		}

		public static string GetBrokeragePath()
		{
			if (string.IsNullOrEmpty(FBuildBrokeragePath))
			{
				string EnvPath = Environment.GetEnvironmentVariable(Tag.EnvVar.FASTBuildBrokeragePath);
				if (EnvPath.HasValue())
				{
					FBuildBrokeragePath = EnvPath;
				}
			}

			return FBuildBrokeragePath;
		}

		public static string GetCoordinator()
		{
			if (string.IsNullOrEmpty(FBuildCoordinator))
			{
				string EnvPath = Environment.GetEnvironmentVariable(Tag.EnvVar.FASTBuildCoordinator);
				if (EnvPath.HasValue())
				{
					FBuildCoordinator = EnvPath;
				}
			}

			return FBuildCoordinator;
		}

		public static bool IsAvailable()
		{
			string ExecutablePath = GetExecutablePath();
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				if (string.IsNullOrEmpty(ExecutablePath))
				{
					FBuildExecutablePath = Path.Combine(DefaultExecutableBasePath, BuildHostPlatform.Current.Platform.ToString(), Tag.Directory.FastBuild);
				}
			}
			else if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				if (string.IsNullOrEmpty(ExecutablePath))
				{
					FBuildExecutablePath = Path.Combine(DefaultExecutableBasePath, BuildHostPlatform.Current.Platform.ToString(), Tag.Binary.FastBuild + Tag.Ext.Exe);
				}
			}
			else
			{
				// Linux is not supported yet. Win32 likely never.
				return false;
			}

			// UBT is faster than FASTBuild for local only builds, so only allow FASTBuild if the environment is fully set up to use FASTBuild.
			// That's when the FASTBuild coordinator or brokerage folder is available.
			// On Mac the latter needs the brokerage folder to be mounted, on Windows the brokerage env variable has to be set or the path specified in UBT's config
			string Coordinator = GetCoordinator();
			if (string.IsNullOrEmpty(Coordinator))
			{
				string BrokeragePath = GetBrokeragePath();
				if (string.IsNullOrEmpty(BrokeragePath) || !Directory.Exists(BrokeragePath))
				{
					return false;
				}
			}

			if (FBuildExecutablePath.HasValue())
			{
				if (File.Exists(FBuildExecutablePath))
                {
                    return true;
                }

                Log.TraceWarning($"FBuildExecutablePath '{FBuildExecutablePath}' doesn't exist! Attempting to find executable in PATH.");
			}

			// Get the name of the FASTBuild executable.
			string FBuildExecutableName = ExecutableName;

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable(Tag.EnvVar.PATH);
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, FBuildExecutableName);
					if (File.Exists(PotentialPath))
					{
						FBuildExecutablePath = PotentialPath;
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}

			Log.TraceError("FASTBuild disabled. Unable to find any executable to use.");
			return false;
		}

		//////////////////////////////////////////
		// Action Helpers

		private readonly ObjectIDGenerator ObjectIDGenerator = new ObjectIDGenerator();

		private long GetActionID(Action Action)
		{
#pragma warning disable IDE0059 // Unnecessary assignment of a value
            return ObjectIDGenerator.GetId(Action, out bool bFirstTime);
#pragma warning restore IDE0059 // Unnecessary assignment of a value
        }

		private string ActionToActionString(Action Action)
		{
			return ActionToActionString(GetActionID(Action));
		}

		private string ActionToActionString(long UniqueId)
		{
			return $"Action_{UniqueId}";
		}

		private string ActionToDependencyString(long UniqueId, string StatusDescription, string CommandDescription = null, ActionType? ActionType = null)
		{
			string ExtraInfoString = null;
			if ((CommandDescription != null) && string.IsNullOrEmpty(CommandDescription))
				ExtraInfoString = CommandDescription;
			else if (ActionType != null)
				ExtraInfoString = ActionType.Value.ToString();

			if ((ExtraInfoString != null) && !string.IsNullOrEmpty(ExtraInfoString))
				ExtraInfoString = $" ({ExtraInfoString})";

			return $"\t\t'{ActionToActionString(UniqueId)}', ;{StatusDescription}{ExtraInfoString}";
		}

		private string ActionToDependencyString(Action Action)
		{
			return ActionToDependencyString(GetActionID(Action), Action.StatusDescription, Action.CommandDescription, Action.Type);
		}

		private readonly HashSet<string> ForceLocalCompileModules 
			= new HashSet<string>() { "Module.ProxyLODMeshReduction" };

		private enum FBBuildType
		{
			Windows,
			Apple
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private readonly static Tuple<string, Func<Action, string>, FBBuildType>[] BuildTypeSearchParams = 
			new Tuple<string, Func<Action, string>, FBBuildType>[]
		{
			Tuple.Create<string, Func<Action, string>, FBBuildType>
			(
				"Xcode",
				Action => Action.CommandArguments,
				FBBuildType.Apple
			),
			Tuple.Create<string, Func<Action, string>, FBBuildType>
			(
				"apple",
				Action => Action.CommandArguments.ToLower(),
				FBBuildType.Apple
			),
			Tuple.Create<string, Func<Action, string>, FBBuildType>
			(
				"/bin/sh",
				Action => Action.CommandPath.FullName.ToLower(),
				FBBuildType.Apple
			),
			Tuple.Create<string, Func<Action, string>, FBBuildType>
			(
				"Windows",		// Not a great test
				Action => Action.CommandPath.FullName,
				FBBuildType.Windows
			),
			Tuple.Create<string, Func<Action, string>, FBBuildType>
			(
				"Microsoft",	// Not a great test
				Action => Action.CommandPath.FullName,
				FBBuildType.Windows
			),
		};

		private bool DetectBuildType(IEnumerable<Action> Actions)
		{
			foreach (Action Action in Actions)
			{
				foreach (Tuple<string, Func<Action, string>, FBBuildType> BuildTypeSearchParam in BuildTypeSearchParams)
				{
					if (BuildTypeSearchParam.Item2(Action).Contains(BuildTypeSearchParam.Item1))
					{
						BuildType = BuildTypeSearchParam.Item3;
						Log.TraceInformation($"Detected build type as {BuildTypeSearchParam.Item1} from '{BuildTypeSearchParam.Item2(Action)}' using search term '{BuildTypeSearchParam.Item1}'");
						return true;
					}
				}
			}

			Log.TraceError("Couldn't detect build type from actions! Unsupported platform?");
			foreach (Action Action in Actions)
			{
				PrintActionDetails(Action);
			}
			return false;
		}

        private bool IsMSVC() => BuildType == FBBuildType.Windows;
        private bool IsApple() => BuildType == FBBuildType.Apple;

        private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.Windows: return Tag.ActionDescriptor.Compiler;
				case FBBuildType.Apple: return Tag.ActionDescriptor.AppleCompiler;
			}
		}

		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			if (Actions.Count <= 0)
            {
                return true;
            }

            IEnumerable<Action> CompileActions    = Actions.Where(Action => (Action.Type == ActionType.Compile && Action.bCanExecuteRemotely == true));
			IEnumerable<Action> NonCompileActions = Actions.Where(Action => (Action.Type != ActionType.Compile || Action.bCanExecuteRemotely == false));

			///////////////////////////////////////////////////////////////
			// Pre Compile Stage

			// We want to complete any non-compile actions locally that are necessary for the distributed compile step
			List<Action> PreCompileActions =
				NonCompileActions
				.Where(NonCompileAction => CompileActions.Any(CompileAction => CompileAction.PrerequisiteActions.Contains(NonCompileAction)))
				.ToList();

			// Precompile actions may have their own prerequisites which need to be executed along with them
			List<Action> Prerequisites = new List<Action>();
			foreach (Action PreCompileAction in PreCompileActions)
			{
				Prerequisites.AddRange(PreCompileAction.PrerequisiteActions);
			}

			PreCompileActions.AddRange(Prerequisites);

			if (PreCompileActions.Any())
			{
				bool bResult = new LocalExecutor().ExecuteActions(PreCompileActions, bLogDetailedActionStats);

				if (!bResult)
                {
                    return false;
                }
            }

			///////////////////////////////////////////////////////////////
			// Compile Stage

			if (CompileActions.Any())
			{
				if (!DetectBuildType(CompileActions))
                {
                    return false;
                }

                string FASTBuildFilePath = Path.Combine(BuildTool.EngineDirectory.FullName, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.FastBuild + Tag.Ext.BFF);

                if (!CreateBffFile(CompileActions, FASTBuildFilePath))
                {
                    return false;
                }

                if (!ExecuteBffFile(FASTBuildFilePath))
                {
                    return false;
                }
            }

			///////////////////////////////////////////////////////////////
			// Post Compile Stage

			List<Action> PostCompileActions = NonCompileActions.Except(PreCompileActions).ToList();

			if (PostCompileActions.Any())
			{
				bool bResult = new LocalExecutor().ExecuteActions(PostCompileActions, bLogDetailedActionStats);

				if (!bResult)
					{
                    return false;
                }
			}

			return true;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputMemoryStream.Write(Info, 0, Info.Length);
		}

		private void AddPreBuildDependenciesText(IEnumerable<Action> PreBuildDependencies)
		{
            if (!PreBuildDependencies.Any())
            {
                return;
            }

            AddText($"\t.PreBuildDependencies = {{\n");
			AddText($"{string.Join("\n", PreBuildDependencies.Select(ActionToDependencyString))}\n");
			AddText($"\t}} \n");
		}

		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			return commandLineString
				.Replace(Tag.EnvVar.DXSDKDirMacro, Tag.EnvVar.DXSDKDir)
				.Replace(Tag.EnvVar.CommonProgramFilesMacro, Tag.EnvVar.CommonProgramFiles);
		}

		private Dictionary<string, string> ParseCommandLineOptions
		(
            string   LocalToolName,
            string   CompilerCommandLine,
            string[] SpecialOptions,
            bool     SaveResponseFile = false
		)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF(Building a Backend for Frontend) friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tools are now executed via arch so they can run natively even if BuildTool is under mono/rosetta. If so we
			// need to remove the architecture from the argument list
			if (LocalToolName.ToLower() == Tag.ActionDescriptor.Arch)
			{
				// Action would be /usr/bin/arch -<arch> -other -flags so remove the first
				// argument and any trailing spaces
				Match M = Regex.Match(CompilerCommandLine, @"^\s*-.+?\s+");

				if (M.Success)
				{
					CompilerCommandLine = CompilerCommandLine.Substring(M.Length);
				}
			}

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either BuildTool or FASTBuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";
			List<string> AllTokens = new List<string>();

			int ResponseFileTokenIndex = Array.FindIndex(RawTokens, RawToken => RawToken.StartsWith("@\""));
			if (0 < ResponseFileTokenIndex) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				string responseCommandline = RawTokens[ResponseFileTokenIndex];

				for (int i = 0; i < ResponseFileTokenIndex; ++i)
				{
					AllTokens.Add(RawTokens[i]);
				}

				// If we had spaces inside the response file path, we need to reconstruct the path.
				for (int i = ResponseFileTokenIndex + 1; i < RawTokens.Length; ++i)
				{
					if (RawTokens[i - 1].Contains(Tag.Ext.Response) || RawTokens[i - 1].Contains(Tag.Ext.Res))
                    {
                        break;
                    }

                    responseCommandline += " " + RawTokens[i];
				}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					if (!File.Exists(ResponseFilePath))
					{
						throw new BuildException($"ResponseFilePath '{ResponseFilePath}' does not exist!");
					}

					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };

					if (File.Exists(ResponseFilePath))
                    {
                        RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal
                    }
                }
				catch (Exception e)
				{
					if (e.Message.HasValue())
                    {
                        Log.TraceInformation(e.Message);
                    }

                    Log.TraceError("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			for (int i = 0; i < RawTokens.Length; ++i)
			{
				AllTokens.Add(RawTokens[i]);
			}

			// Raw tokens being split with spaces may have split up some two argument options and
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < AllTokens.Count; ++i)
			{
				string Token = AllTokens[i];

				if (string.IsNullOrEmpty(Token))
				{
					if (0 < ProcessedTokens.Count && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
                    {
                        ++j;
                    }
                    else if (Token[j] == '"')
                    {
                        ++numQuotes;
                    }
                }

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && 
					!QuotesOpened)
				{
					if (numQuotes == 0 || 
						numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						bool AddedToken = false;

						for (++i; i < AllTokens.Count; ++i)
						{
							string NextToken = AllTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && 
								      NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Log.TraceWarning("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}

					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && 
						i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			for (int i = 0; i < ProcessedTokens.Count; ++i)
			{
				string Token = ProcessedTokens[i];
				if (Token.Length == 0)
				{
					continue;
				}

				// Skip the following tokens:
				if ((Token == "/I") ||
					(Token == "/l") ||
					(Token == "/D") ||
					(Token == "-D") ||
					(Token == "-x") ||
					(Token == "-F") ||
					(Token == "-arch") ||
					(Token == "-isysroot") ||
					(Token == "-include") ||
					(Token == "-current_version") ||
					(Token == "-compatibility_version") ||
					(Token == "-rpath") ||
					(Token == "-weak_library") ||
					(Token == "-weak_framework") ||
					(Token == "-framework"))
				{
					++i;
				}
				else if (Token == "/we4668")
				{
					// Replace this to make Windows builds compile happily
					ProcessedTokens[i] = "/wd4668";
				}
				else if (Token.Contains("clang++"))
				{
					ProcessedTokens.RemoveAt(i--);
				}
				else if (Token == "--")
				{
					ProcessedTokens.RemoveAt(i);
					ParsedCompilerOptions["CLFilterChildCommand"] = ProcessedTokens[i];
					ProcessedTokens.RemoveAt(i--);
				}
				else if (!Token.StartsWith("/") && 
					     !Token.StartsWith("-") && 
				         !Token.Contains(".framework"))
				{
					ParsedCompilerOptions["InputFile"] = Token;
					ProcessedTokens.RemoveAt(i);
					break;
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
            if (OptionsDictionary.TryGetValue(Key, out string Value))
            {
                return Value.Trim(new Char[] { '\"' });
            }

            if (ProblemIfNotFound)
			{
				Log.TraceWarning("We failed to find " + Key + ", which may be a problem.");
				Log.TraceWarning("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
            object returnValue = Microsoft.Win32.Registry.GetValue(Tag.Registry.HKeyLocalMachine + Tag.Registry.Software + keyName, valueName, defaultValue);
            
			if (returnValue != null)
            {
                return returnValue.ToString();
            }

            returnValue = Microsoft.Win32.Registry.GetValue(Tag.Registry.HKeyCurrentUser + Tag.Registry.Software + keyName, valueName, defaultValue);
            if (returnValue != null)
            {
                return returnValue.ToString();
            }

            returnValue = (string)Microsoft.Win32.Registry.GetValue(Tag.Registry.HKeyLocalMachine + Tag.Registry.Software + Tag.Registry.Wow6432Node + keyName, valueName, defaultValue);
            if (returnValue != null)
            {
                return returnValue.ToString();
            }

            returnValue = Microsoft.Win32.Registry.GetValue(Tag.Registry.HKeyCurrentUser + Tag.Registry.Software + Tag.Registry.Wow6432Node + keyName, valueName, defaultValue);
            if (returnValue != null)
            {
                return returnValue.ToString();
            }

            return defaultValue.ToString();
        }

		private void WriteEnvironmentSetup()
		{
			VCEnvironment VCEnv = null;

			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				if (BuildType == FBBuildType.Windows)
				{
					VCEnv = VCEnvironment.Create
					(
                        WindowsPlatform.GetDefaultCompiler(null),
                        BuildTargetPlatform.Win64,
                        WindowsArchitecture.x64,
                        null,
                        null,
                        null
					);
				}
			}
			catch (Exception)
			{
				throw new BuildException("Failed to get Visual Studio environment.");
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				if (envVars.ContainsKey(Tag.EnvVar.RawCommonProgramFiles))
				{
					AddText("#import CommonProgramFiles\n");
				}

				if (envVars.ContainsKey(Tag.EnvVar.RawCommonProgramFiles))
				{
					AddText("#import DXSDK_DIR\n");
				}

				if (envVars.ContainsKey(Tag.EnvVar.RawDurangoXDK))
				{
					AddText("#import DurangoXDK\n");
				}
			}

			if (VCEnv != null)
			{
                AddText($".WindowsSDKBasePath = '{VCEnv.WindowsSdkDir}'\n");
                AddText($"Compiler('{Tag.ActionDescriptor.ResourceCompiler}') \n{{\n");

                string platformVersionNumber = "VSVersionUnknown";
                switch (VCEnv.Compiler)
                {
                    case WindowsCompiler.VisualStudio2017:
                        // For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
                        // to install
                        platformVersionNumber = Tag.Binary.PlatformDLLVersion;
                        AddText($"\t.Executable = '{Tag.EnvVar.WindowsSDKBasePath}/{Tag.Directory.Bin}/{VCEnv.WindowsSdkVersion}/{Tag.Directory.X64}/{Tag.Binary.RC}'\n");
                        break;

                    case WindowsCompiler.VisualStudio2019:
                        // For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
                        // to install
                        platformVersionNumber = Tag.Binary.PlatformDLLVersion;
                        AddText($"\t.Executable = '{Tag.EnvVar.WindowsSDKBasePath}/{Tag.Directory.Bin}/{VCEnv.WindowsSdkVersion}/{Tag.Directory.X64}/{Tag.Binary.RC}'\n");
                        break;

                    default:
                        string exceptionString = "Error: Unsupported Visual Studio Version.";
                        Log.TraceError(exceptionString);
                        throw new BuildException(exceptionString);
                }

                AddText($"\t.CompilerFamily  = 'custom'\n");
				AddText($"}}\n\n");

				AddText($"Compiler('{Tag.ActionDescriptor.Compiler}') \n{{\n");

				DirectoryReference CLFilterDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.Windows, Tag.Binary.ClFilter);

				AddText($"\t.Root = '{VCEnv.GetToolPath()}'\n");
				AddText($"\t.CLFilterRoot = '{CLFilterDirectory.FullName}'\n");
				AddText($"\t.Executable = '{Tag.EnvVar.CLFilterRoot}\\{Tag.Binary.ClFilter}'\n");
				AddText($"\t.ExtraFiles =\n\t{{\n");
				AddText($"\t\t'$Root$/cl.exe'\n");
				AddText($"\t\t'$Root$/c1.dll'\n");
				AddText($"\t\t'$Root$/c1xx.dll'\n");
				AddText($"\t\t'$Root$/c2.dll'\n");

				FileReference cluiDllPath = null;
				string cluiSubDirName = null;

				if (File.Exists(VCEnv.GetToolPath() + $"{Tag.Binary.CluiSubDirName}/{Tag.Binary.CLUI}")) //Check English first...
				{
					AddText($"\t\t'$CLFilterRoot$/{Tag.Binary.CluiSubDirName}/{Tag.Binary.CLUI}'\n");
					cluiDllPath = new FileReference(VCEnv.GetToolPath() + $"{Tag.Binary.CluiSubDirName}/{Tag.Binary.CLUI}");
				}
				else
				{
                    IEnumerable<string> numericDirectories = Directory.GetDirectories(VCEnv.GetToolPath().ToString()).Where(d => Path.GetFileName(d).All(char.IsDigit));
                    IEnumerable<string> cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, Tag.Binary.CLUI).Any());
					if (cluiDirectories.Any())
					{
						cluiSubDirName = Path.GetFileName(cluiDirectories.First());
						AddText(string.Format($"\t\t'{Tag.EnvVar.CLFilterRoot}/{0}/{Tag.Binary.CLUI}'\n", cluiSubDirName));
						cluiDllPath = new FileReference(cluiDirectories.First() + $"/{Tag.Binary.CLUI}");
					}
				}

				// FASTBuild only preserves the directory structure of compiler files for files in the same directory or sub-directories of the primary executable
				// Since our primary executable is cl-filter.exe and we need clui.dll in a sub-directory on the worker, we need to copy it to cl-filter's subdir
				if (cluiDllPath != null)
				{
					Directory.CreateDirectory(Path.Combine(CLFilterDirectory.FullName, cluiSubDirName));
					File.Copy(cluiDllPath.FullName, Path.Combine(CLFilterDirectory.FullName, cluiSubDirName, Tag.Binary.CLUI), true);
				}

				AddText($"\t\t'{Tag.EnvVar.Root}/{Tag.Binary.MspdbsrvExe}'\n");
				AddText($"\t\t'{Tag.EnvVar.Root}/{Tag.Binary.MspdbCoreDll}'\n");

				AddText($"\t\t'{Tag.EnvVar.Root}/{Tag.Binary.Mspft}{platformVersionNumber}{Tag.Ext.Dll}'\n");
				AddText($"\t\t'{Tag.EnvVar.Root}/{Tag.Binary.Msobj}{platformVersionNumber}{Tag.Ext.Dll}'\n");
				AddText($"\t\t'{Tag.EnvVar.Root}/{Tag.Binary.Mspdb}{platformVersionNumber}{Tag.Ext.Dll}'\n");

				List<String> PotentialMSVCRedistPaths = new List<String>(Directory.EnumerateDirectories(string.Format($"{0}/{Tag.Directory.Redist}/{Tag.Directory.MSVC}", VCEnv.GetVCInstallDirectory())));
				string PrefferedMSVCRedistPath = null;
				string FinalMSVCRedistPath     = "";

				if (0 < MSVCCRTRedistVersion.Length)
				{
					PrefferedMSVCRedistPath = PotentialMSVCRedistPaths.Find
					(
						delegate (String str)
						{
							return str.Contains(MSVCCRTRedistVersion);
						}
					);
				}

				if (PrefferedMSVCRedistPath == null)
				{
					PrefferedMSVCRedistPath = PotentialMSVCRedistPaths[PotentialMSVCRedistPaths.Count - 2];

					if (0 < MSVCCRTRedistVersion.Length)
					{
						Log.TraceInformation("Couldn't find redist path for given MsvcCRTRedistVersion {" + MSVCCRTRedistVersion.ToString()
						+ "} (in BuildConfiguration.xml). \n\t...Using this path instead: {" + PrefferedMSVCRedistPath.ToString() + "}");
					}
					else
					{
						Log.TraceInformation("Using path : {" + PrefferedMSVCRedistPath.ToString() + "} for vccorlib_.dll (MSVC redist)..." +
							"\n\t...Add an entry for MsvcCRTRedistVersion in BuildConfiguration.xml to specify a version number");
					}

				}

				PotentialMSVCRedistPaths = new List<String>(Directory.EnumerateDirectories(string.Format("{0}/{1}", PrefferedMSVCRedistPath, VCEnv.CompilerArchitecture)));

                FinalMSVCRedistPath = PotentialMSVCRedistPaths.Find
                (
                    delegate (String str)
                    {
                        return str.Contains(Tag.Ext.CRT);
                    }
                );

                if (FinalMSVCRedistPath.Length <= 0)
				{
					FinalMSVCRedistPath = PrefferedMSVCRedistPath;
				}

				if (VCEnv.Compiler == WindowsCompiler.VisualStudio2017)
				{
					// VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on
					// what version of the tool chain you  chose to install.
					AddText(string.Format("\t\t'{0}/Redist/MSVC/14.16.27012/{1}/Microsoft.VC141.CRT/msvcp{2}.dll'\n", VCEnv.GetVCInstallDirectory(), VCEnv.CompilerArchitecture, platformVersionNumber));
					AddText(string.Format("\t\t'{0}/Redist/MSVC/14.16.27012/{1}/Microsoft.VC141.CRT/vccorlib{2}.dll'\n", VCEnv.GetVCInstallDirectory(), VCEnv.CompilerArchitecture, platformVersionNumber));
				}
				else // if (VCEnv.Compiler == WindowsCompiler.VisualStudio2019)
				{
					AddText($"\t\t'$Root$/msvcp{platformVersionNumber}.dll'\n");
					AddText(string.Format("\t\t'{0}/vccorlib{1}.dll'\n", FinalMSVCRedistPath, platformVersionNumber));
					AddText($"\t\t'$Root$/tbbmalloc.dll'\n");

					//AddText(string.Format("\t\t'{0}/Redist/MSVC/{1}/x64/Microsoft.VC141.CRT/vccorlib{2}.dll'\n", VCEnv.GetVCInstallDirectory(), VCEnv.ToolChainVersion, platformVersionNumber));
				}


				AddText("\t}\n"); //End extra files

				AddText($"\t.CompilerFamily = 'msvc'\n");
				AddText("}\n\n"); //End compiler
			}

			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				AddText($".MacBaseSDKDir = '{MacToolChain.Settings.BaseSDKDir}'\n");
				AddText($".MacToolchainDir = '{MacToolChain.Settings.ToolchainDir}'\n");
				AddText($"Compiler('{Tag.ActionDescriptor.AppleCompiler}') \n{{\n");
				AddText($"\t.Executable = '$MacToolchainDir$/clang++'\n");
				AddText($"\t.ClangRewriteIncludes = false\n"); // This is to fix an issue with iOS clang builds, __has_include, and Objective-C #imports
				AddText($"}}\n\n");
			}

			AddText("Settings \n{\n");

			if (bEnableCaching)
			{
				string CachePath = GetCachePath();
				if (!string.IsNullOrEmpty(CachePath))
					AddText($"\t.CachePath = '{CachePath}'\n");
			}

			if (bEnableDistribution)
			{
				string BrokeragePath = GetBrokeragePath();
				if (!string.IsNullOrEmpty(BrokeragePath))
					AddText($"\t.BrokeragePath = '{BrokeragePath}'\n");
			}

			//Start Environment
			AddText("\t.Environment = \n\t{\n");
			if (VCEnv != null)
			{
				AddText(string.Format("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\",\n", VCEnv.GetVCInstallDirectory(), VCEnv.GetToolPath()));
			}

			if (!IsApple())
			{
				if (envVars.ContainsKey("TMP"))
					AddText($"\t\t\"TMP={envVars["TMP"]}\",\n");

				if (envVars.ContainsKey("SystemRoot"))
					AddText($"\t\t\"SystemRoot={envVars["SystemRoot"]}\",\n");

				if (envVars.ContainsKey("INCLUDE"))
					AddText($"\t\t\"INCLUDE={envVars["INCLUDE"]}\",\n");

				if (envVars.ContainsKey("LIB"))
					AddText($"\t\t\"LIB={envVars["LIB"]}\",\n");
			}

			AddText("\t}\n"); //End environment
			AddText("}\n\n"); //End Settings
		}

		private void AddCompileAction(Action Action, IEnumerable<Action> DependencyActions)
		{
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.FullName.Contains("rc.exe"))
			{
				CompilerName = Tag.ActionDescriptor.ResourceCompiler;
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o", "-dependencies=", "-compiler=" };
            Dictionary<string, string> ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandPath.GetFileName(), Action.CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				throw new BuildException("We have no OutputObjectFileName. Bailing. Our Action.CommandArguments were: " + Action.CommandArguments);
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				throw new BuildException("We have no IntermediatePath. Bailing. Our Action.CommandArguments were: " + Action.CommandArguments);
			}

			IntermediatePath = IsApple() ? IntermediatePath.Replace("\\", "/") : IntermediatePath;

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				throw new Exception("We have no InputFile. Bailing. Our Action.CommandArguments were: " + Action.CommandArguments);
			}

			AddText($"ObjectList('{ActionToActionString(Action)}')\n{{\n");
			AddText($"\t.Compiler = '{CompilerName}'\n");
			AddText($"\t.CompilerInputFiles = \"{InputFile}\"\n");
			AddText($"\t.CompilerOutputPath = \"{IntermediatePath}\"\n");

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || ForceLocalCompileModules.Contains(Path.GetFileNameWithoutExtension(InputFile)))
			{
				AddText("\t.AllowDistribution = false\n");
			}

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
            string CLFilterParams = "";
            string ShowIncludesParam = "";
			if (ParsedCompilerOptions.ContainsKey("CLFilterChildCommand"))
			{
				CLFilterParams = "-dependencies=\"%CLFilterDependenciesOutput\" -compiler=\"%5\" -stderronly -- \"%5\" ";
				ShowIncludesParam = "/showIncludes";
			}

            string CompilerOutputExtension;
            if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
            {
                string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
                string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

                AddText($"\t.CompilerOptions = '{CLFilterParams}\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" {OtherCompilerOptions} '\n");

                AddText($"\t.PCHOptions = '{CLFilterParams}\"%1\" /Fp\"%2\" /Yc\"{PCHIncludeHeader}\" {OtherCompilerOptions} /Fo\"{OutputObjectFileName}\"'\n");
                AddText($"\t.PCHInputFile = \"{InputFile}\"\n");
                AddText($"\t.PCHOutputFile = \"{PCHOutputFile}\"\n");
                CompilerOutputExtension = ".obj";
            }
            else if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
            {
                string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
                string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
                string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
                AddText($"\t.CompilerOptions = '{CLFilterParams}\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" /FI\"{PCHToForceInclude}\" {OtherCompilerOptions} {ShowIncludesParam} '\n");
                string InputFileExt = Path.GetExtension(InputFile);
                CompilerOutputExtension = InputFileExt + ".obj";
            }
            else if (Path.GetExtension(OutputObjectFileName) == ".gch") //Create PCH
            {
                AddText($"\t.CompilerOptions = '{OtherCompilerOptions} -D __BUILDING_WITH_FASTBUILD__ -fno-diagnostics-color -o \"%2\" \"%1\" '\n");
                AddText($"\t.PCHOptions = '{OtherCompilerOptions} -o \"%2\" \"%1\" '\n");
                AddText($"\t.PCHInputFile = \"{InputFile}\"\n");
                AddText($"\t.PCHOutputFile = \"{OutputObjectFileName}\"\n");
                CompilerOutputExtension = ".h.gch";
            }
            else
            {
                if (CompilerName == "UEResourceCompiler")
                {
                    AddText($"\t.CompilerOptions = '{OtherCompilerOptions} /fo\"%2\" \"%1\" '\n");
                    CompilerOutputExtension = Path.GetExtension(InputFile) + ".res";
                }
                else
                {
                    if (IsMSVC())
                    {
                        AddText($"\t.CompilerOptions = '{CLFilterParams}{OtherCompilerOptions} /Fo\"%2\" \"%1\" {ShowIncludesParam} '\n");
                        string InputFileExt = Path.GetExtension(InputFile);
                        CompilerOutputExtension = InputFileExt + ".obj";
                    }
                    else
                    {
                        AddText($"\t.CompilerOptions = '{OtherCompilerOptions} -D __BUILDING_WITH_FASTBUILD__ -fno-diagnostics-color -o \"%2\" \"%1\" '\n");
                        string InputFileExt = Path.GetExtension(InputFile);
                        CompilerOutputExtension = InputFileExt + ".o";
                    }
                }
            }

            AddText($"\t.CompilerOutputExtension = '{CompilerOutputExtension}' \n");
			AddPreBuildDependenciesText(DependencyActions);
			AddText("}\n\n");
		}

		private void AddLinkAction(Action Action, IEnumerable<Action> DependencyActions)
		{
			string[] SpecialLinkerOptions = IsApple() ? new string[] { "-o" } : new string[] { "/OUT:", "@", "-o" };

			List<string> SplitCommandArgs = new List<string>();

			string CommandArgsToParse = Action.CommandArguments;
			if (CommandArgsToParse.StartsWith("-c \'") && CommandArgsToParse.EndsWith("\'"))
			{
				CommandArgsToParse = CommandArgsToParse.Remove(0, 4).TrimEnd('\'').Trim(' ');
				SplitCommandArgs = CommandArgsToParse.Split(';').Select(SplitCommandArg => SplitCommandArg.Trim()).ToList();
				CommandArgsToParse = SplitCommandArgs[0];
			}

			Dictionary<string, string> ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandPath.GetFileName(), CommandArgsToParse, SpecialLinkerOptions, SaveResponseFile: true);

			string OutputFile;

			if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else // Apple
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Log.TraceError("Failed to find output file. Bailing.");
				return;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			IEnumerable<Action> PrebuildDependencies = null;

			if (Action.CommandPath.FullName.Contains("lib.exe"))
			{
				if (DependencyActions.Any())
				{
                    bool DoesActionProducePCH(Action ActionToCheck)
                    {
                        foreach (FileItem ProducedItem in ActionToCheck.ProducedItems)
                        {
                            if (ProducedItem.ToString().Contains(".pch") || ProducedItem.ToString().Contains(".res"))
                            {
                                return true;
                            }
                        }
                        return false;
                    }

                    // Don't specify pch or resource files, they have the wrong name and the response file will have them anyways.
                    PrebuildDependencies = DependencyActions.Where(DoesActionProducePCH);
					DependencyActions = DependencyActions.Where((ActionToCheck) => !DoesActionProducePCH(ActionToCheck));
				}

				AddText($"Library('{ActionToActionString(Action)}')\n{{\n");
				AddText($"\t.Compiler = '{GetCompilerName()}'\n");
				if (IsMSVC())
					AddText("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n");
				else
					AddText("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n");
				AddText($"\t.CompilerOutputPath = \"{Path.GetDirectoryName(OutputFile)}\"\n");
				AddText($"\t.Librarian = '{Action.CommandPath.FullName}' \n");

				if (!string.IsNullOrEmpty(ResponseFilePath))
				{
					if (IsMSVC())
						AddText($"\t.LibrarianOptions = ' /OUT:\"%2\" @\"{ResponseFilePath}\" \"%1\"' \n");
					else
						AddText($"\t.LibrarianOptions = '\"%2\" @\"%1\" {OtherCompilerOptions}' \n");
				}
				else
				{
					if (IsMSVC())
						AddText($"\t.LibrarianOptions = ' /OUT:\"%2\" {OtherCompilerOptions} \"%1\"' \n");
				}

				if (DependencyActions.Any())
				{
					List<string> DependencyNames = DependencyActions.Select(ActionToDependencyString).ToList();

					if (!string.IsNullOrEmpty(ResponseFilePath))
						AddText($"\t.LibrarianAdditionalInputs = {{\n{DependencyNames[0]}\n\t}} \n"); // Hack...Because FASTBuild needs at least one Input file
					else if (IsMSVC())
						AddText($"\t.LibrarianAdditionalInputs = {{\n{string.Join(",", DependencyNames.ToArray())}\n\t}} \n");

					PrebuildDependencies = PrebuildDependencies.Concat(DependencyActions);
				}
				else
				{
					AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true)));
				}

				AddText($"\t.LibrarianOutput = '{OutputFile}' \n");
				AddPreBuildDependenciesText(PrebuildDependencies);
				AddText($"}}\n\n");
			}
			else if (Action.CommandPath.FullName.Contains("link.exe"))
			{
				AddText($"Executable('{ActionToActionString(Action)}')\n{{ \n");
				AddText($"\t.Linker = '{Action.CommandPath.FullName}' \n");
				if (DependencyActions.Any())
				{
					AddText($"\t.Libraries = {{ '{ResponseFilePath}' }} \n");
					if (IsMSVC())
					{
						AddText($"\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{ResponseFilePath}\" ' \n"); // The TLBOUT is a huge bodge to consume the %1.
					}
					else
					{
						AddText($"\t.LinkerOptions = '-o \"%2\" @\"{ResponseFilePath}\" {OtherCompilerOptions} -MQ \"%1\"' \n"); // The MQ is a huge bodge to consume the %1.
					}
				}
				else
				{
					AddText($"\t.Libraries = '{ActionToActionString(DependencyActions.First())}' \n");

					if (IsMSVC())
					{
						AddText($"\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{ResponseFilePath}\" ' \n"); // The TLBOUT is a huge bodge to consume the %1.
					}
					else
					{
						AddText($"\t.LinkerOptions = '-o \"%2\" @\"{ResponseFilePath}\" {OtherCompilerOptions} -MQ \"%1\"' \n"); // The MQ is a huge bodge to consume the %1.
					}
				}

				AddText($"\t.LinkerOutput = '{OutputFile}' \n");
				AddPreBuildDependenciesText(DependencyActions);
				AddText($"}}\n\n");
			}
			else if (Action.CommandArguments.Contains("clang++"))
			{
				AddText($"Executable('{ActionToActionString(Action)}')\n{{ \n");
				AddText("\t.Linker = '$MacToolchainDir$/clang++' \n");

				string InputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				if (!string.IsNullOrEmpty(InputFile))
				{
					Action InputFileAction = DependencyActions
						.Where(ActionToInspect =>
							ActionToInspect.ProducedItems.Exists(Item => Item.AbsolutePath == InputFile)
						).FirstOrDefault();

					if (InputFileAction != null)
						InputFile = ActionToActionString(InputFileAction);
				}

				AddText($"\t.Libraries = {{ '{InputFile}' }} \n");
				AddText($"\t.LinkerOptions = '{OtherCompilerOptions} \"%1\" -o \"%2\"' \n");
				AddText($"\t.LinkerOutput = '{OutputFile}' \n");
				AddPreBuildDependenciesText(DependencyActions);

				IEnumerable<string> PostLinkCommands = SplitCommandArgs.Skip(1);
				if (PostLinkCommands.Any())
				{
					List<string> ChangeCommands = new List<string>();
					foreach (string PostLinkCommand in PostLinkCommands)
					{
						int ChangeIndex = PostLinkCommand.IndexOf("-change");
						if (ChangeIndex == -1)
							continue;

						string LastDylibString = ".dylib ";
						int LastDylibIndex = PostLinkCommand.LastIndexOf(LastDylibString);
						if (LastDylibIndex == -1)
							continue;

						ChangeCommands.Add(PostLinkCommand.Substring(ChangeIndex, LastDylibIndex + LastDylibString.Count() - ChangeIndex));
					}

					if (ChangeCommands.Any())
					{
						AddText($"\t.LinkerStampExe = '$MacToolchainDir$/install_name_tool' \n");
						AddText($"\t.LinkerStampExeArgs = '{string.Join(" ", ChangeCommands)} {OutputFile}' \n");
					}
				}

				AddText($"}}\n\n");
			}
			else
			{
				Log.TraceError("Failed to add link action!");
				PrintActionDetails(Action);
			}
		}

		private void PrintActionDetails(Action ActionToPrint)
		{
			Log.TraceInformation(ActionToActionString(ActionToPrint));
			Log.TraceInformation($"Action Type: {ActionToPrint.Type}");
			Log.TraceInformation($"Action CommandPath: {ActionToPrint.CommandPath.FullName}");
			Log.TraceInformation($"Action CommandArgs: {ActionToPrint.CommandArguments}");
		}

		private MemoryStream bffOutputMemoryStream = null;

		private bool CreateBffFile(IEnumerable<Action> Actions, string BffFilePath)
		{
			try
			{
				bffOutputMemoryStream = new MemoryStream();

				AddText(";*************************************************************************\n");
				AddText(";* Autogenerated bff - see FASTBuild.cs for how this file was generated. *\n");
				AddText(";*************************************************************************\n\n");

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				foreach (Action Action in Actions)
				{
					// Resolve the list of prerequisite items for this action to
					// a list of actions which produce these prerequisites
					IEnumerable<Action> DependencyActions = Action.PrerequisiteActions.Distinct();

					AddText($";** Function for Action {GetActionID(Action)} **\n");
					AddText($";** CommandPath: {Action.CommandPath.FullName}\n");
					AddText($";** CommandArguments: {Action.CommandArguments}\n");
					AddText("\n");

					switch (Action.Type)
					{
						case ActionType.Compile:
							AddCompileAction(Action, DependencyActions);
							break;
						case ActionType.Link:
							AddLinkAction(Action, DependencyActions);
							break;
						default:
							Log.TraceWarning("FASTBuild is ignoring an unsupported action!");
							PrintActionDetails(Action);
							break;
					}
				}

				string JoinedActions = Actions
					.Select(Action => ActionToDependencyString(Action))
					.DefaultIfEmpty(string.Empty)
					.Aggregate((str, obj) => str + "\n" + obj);

				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");
				AddText(JoinedActions);
				AddText("\n\t}\n");
				AddText("}\n");

				using (FileStream bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write))
				{
					bffOutputMemoryStream.Position = 0;
					bffOutputMemoryStream.CopyTo(bffOutputFileStream);
				}

				bffOutputMemoryStream.Close();
			}
			catch (Exception e)
			{
				Log.TraceError("Exception while creating bff file: " + e.ToString());
				return false;
			}

			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			string CacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case FASTBuildCacheMode.ReadOnly:
						CacheArgument = "-cacheread";
						break;
					case FASTBuildCacheMode.WriteOnly:
						CacheArgument = "-cachewrite";
						break;
					case FASTBuildCacheMode.ReadWrite:
						CacheArgument = "-cache";
						break;
				}
			}

			string DistArgument = bEnableDistribution ? "-dist" : "";
			string ForceRemoteArgument = bForceRemote ? "-forceremote" : "";
			string NoStopOnErrorArgument = bStopOnError ? "" : "-nostoponerror";
			string IDEArgument = IsApple() ? "" : "-ide";

			// Interesting flags for FASTBuild:
			// -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FASTBuild internal
			// dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			// Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = $"-monitor -summary {DistArgument} {CacheArgument} {IDEArgument} -clean -config \"{BffFilePath}\" {NoStopOnErrorArgument} {ForceRemoteArgument}";

			Log.TraceInformation($"FBuild Command Line Arguments: '{FBCommandLine}");

			string FBExecutable = GetExecutablePath();
			string WorkingDirectory = Path.GetFullPath(Path.Combine(BuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source"));

            ProcessStartInfo FBStartInfo = new ProcessStartInfo(FBExecutable, FBCommandLine)
            {
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
				WorkingDirectory       = WorkingDirectory
			};

            string Coordinator = GetCoordinator();
			if (!string.IsNullOrEmpty(Coordinator) && !FBStartInfo.EnvironmentVariables.ContainsKey("FASTBUILD_COORDINATOR"))
			{
				FBStartInfo.EnvironmentVariables.Add("FASTBUILD_COORDINATOR", Coordinator);
			}
			FBStartInfo.EnvironmentVariables.Remove("FASTBUILD_BROKERAGE_PATH"); // remove stale serialized value and defer to GetBrokeragePath
			string BrokeragePath = GetBrokeragePath();
			if (!string.IsNullOrEmpty(BrokeragePath) && !FBStartInfo.EnvironmentVariables.ContainsKey("FASTBUILD_BROKERAGE_PATH"))
			{
				FBStartInfo.EnvironmentVariables.Add("FASTBUILD_BROKERAGE_PATH", BrokeragePath);
			}
			string CachePath = GetCachePath();
			if (!string.IsNullOrEmpty(CachePath) && !FBStartInfo.EnvironmentVariables.ContainsKey("FASTBUILD_CACHE_PATH"))
			{
				FBStartInfo.EnvironmentVariables.Add("FASTBUILD_CACHE_PATH", CachePath);
			}

			try
			{
                Process FBProcess = new Process
                {
                    StartInfo           = FBStartInfo,
                    EnableRaisingEvents = true
                };

                void OutputEventHandler(object Sender, DataReceivedEventArgs Args)
                {
                    if (Args.Data != null)
                    {
                        Log.TraceInformation(Args.Data);
                    }
                }

                FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived  += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Log.TraceError("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}
}