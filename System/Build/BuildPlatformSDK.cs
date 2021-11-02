using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// SDK installation status
	enum SDKStatus
	{	
		Valid,   // Desired SDK is installed and set up.
		Invalid, // Could not find the desired SDK, SDK setup failed, etc.		
	};

	// SDK for a platform
	internal abstract class BuildPlatformSDK
	{
		// AutoSDKs handling portion
		#region protected AutoSDKs Utility

		// Name of the file that holds currently install SDK version string
		protected static string CurrentlyInstalledSDKStringManifest => "CurrentlyInstalled.txt";

		// name of the file that holds the last succesfully run SDK setup script version		
		protected static string LastRunScriptVersionManifest => "CurrentlyInstalled.Version.txt";

		// Name of the file that holds environment variables of current SDK
		protected static string SDKEnvVarsFile => "OutputEnvVars.txt";
		protected static string SDKRootEnvVar => "SDKS_ROOT";
		protected static string AutoSetupEnvVar => "AutoSDKSetup";

		// Whether platform supports switching SDKs during runtime
		protected virtual bool PlatformSupportsAutoSDKs() => false;

		private static bool bCheckedAutoSDKRootEnvVar = false;
		private static bool bAutoSDKSystemEnabled = false;
		private static bool HasAutoSDKSystemEnabled()
		{
			if (!bCheckedAutoSDKRootEnvVar)
			{
				string SDKRoot = Environment.GetEnvironmentVariable(SDKRootEnvVar);
				if (SDKRoot != null)
				{
					bAutoSDKSystemEnabled = true;
				}
				bCheckedAutoSDKRootEnvVar = true;
			}
			return bAutoSDKSystemEnabled;
		}

		// Whether AutoSDK setup is safe. AutoSDKs will damage manual installs on some platforms.
		protected bool IsAutoSDKSafe() => !IsAutoSDKDestructive() || !HasAnyManualInstall();

		// Returns SDK string as required by the platform
		protected virtual string GetRequiredSDKString() => "";

		// Gets the version number of the SDK setup script itself.
		// The version in the base should ALWAYS be the master revision from the last refactor.
		// If you need to force a rebuild for a given platform, override this for the given platform.
		protected virtual String GetRequiredScriptVersionString() => "3.0";

		// Returns path to platform SDKs
		protected string GetPathToPlatformAutoSDKs()
		{
			string SDKPath = "";
			string SDKRoot = Environment.GetEnvironmentVariable(SDKRootEnvVar);

			if (SDKRoot != null)
			{
				if (SDKRoot != "")
				{
					SDKPath = Path.Combine(SDKRoot, "Host" + BuildHostPlatform.Current.Platform, GetSDKTargetPlatformName());
				}
			}

			return SDKPath;
		}

		// Returns path to platform SDKs
		public static bool TryGetHostPlatformAutoSDKDir(out DirectoryReference OutPlatformDir)
		{
			string SDKRoot = Environment.GetEnvironmentVariable(SDKRootEnvVar);
			if (String.IsNullOrEmpty(SDKRoot))
			{
				OutPlatformDir = null;
				return false;
			}
			else
			{
				OutPlatformDir = DirectoryReference.Combine(new DirectoryReference(SDKRoot), "Host" + BuildHostPlatform.Current.Platform);
				return true;
			}
		}

        // Because most ManualSDK determination depends on reading env vars, if this process is spawned by a process that ALREADY set up
        // AutoSDKs then all the SDK env vars will exist, and we will spuriously detect a Manual SDK. (children inherit the environment of the parent process).
        // Therefore we write out an env variable to set in the command file (OutputEnvVars.txt) such that child processes can determine if their manual SDK detection
        // is bogus.  Make it platform specific so that platforms can be in different states.
        protected string GetPlatformAutoSDKSetupEnvVar() => GetSDKTargetPlatformName() + AutoSetupEnvVar;

        // Gets currently installed version
        protected bool GetCurrentlyInstalledSDKString(string AbsolutePathPlatformSDKRoot, out string OutInstalledSDKVersionString)
		{
			if (Directory.Exists(AbsolutePathPlatformSDKRoot))
			{
				string VersionFilename = Path.Combine(AbsolutePathPlatformSDKRoot, CurrentlyInstalledSDKStringManifest);
				if (File.Exists(VersionFilename))
				{
					using (StreamReader Reader = new StreamReader(VersionFilename))
					{
						string Version = Reader.ReadLine();
						string Type    = Reader.ReadLine();

						// don't allow ManualSDK installs to count as an AutoSDK install version.
						if (Type != null && Type == "AutoSDK")
						{
							if (Version != null)
							{
								OutInstalledSDKVersionString = Version;
								return true;
							}
						}
					}
				}
			}

			OutInstalledSDKVersionString = "";
			return false;
		}

		// Gets the version of the last successfully run setup script.
		protected bool GetLastRunScriptVersionString(string AbsolutePathPlatformSDKRoot, out string OutLastRunScriptVersion)
		{
			if (Directory.Exists(AbsolutePathPlatformSDKRoot))
			{
				string VersionFilename = Path.Combine(AbsolutePathPlatformSDKRoot, LastRunScriptVersionManifest);
				if (File.Exists(VersionFilename))
				{
					using (StreamReader Reader = new StreamReader(VersionFilename))
					{
						string Version = Reader.ReadLine();
						if (Version != null)
						{
							OutLastRunScriptVersion = Version;
							return true;
						}
					}
				}
			}

			OutLastRunScriptVersion = "";
			return false;
		}

		// Sets currently installed version
		protected bool SetCurrentlyInstalledAutoSDKString(String InstalledSDKVersionString)
		{
			String PlatformSDKRoot = GetPathToPlatformAutoSDKs();
			if (Directory.Exists(PlatformSDKRoot))
			{
				string VersionFilename = Path.Combine(PlatformSDKRoot, CurrentlyInstalledSDKStringManifest);
				if (File.Exists(VersionFilename))
				{
					File.Delete(VersionFilename);
				}

				using (StreamWriter Writer = File.CreateText(VersionFilename))
				{
					Writer.WriteLine(InstalledSDKVersionString);
					Writer.WriteLine("AutoSDK");
					return true;
				}
			}

			return false;
		}

		protected void SetupManualSDK()
		{
			if (PlatformSupportsAutoSDKs() && HasAutoSDKSystemEnabled())
			{
				String InstalledSDKVersionString = GetRequiredSDKString();
				String PlatformSDKRoot = GetPathToPlatformAutoSDKs();
                if (!Directory.Exists(PlatformSDKRoot))
                {
                    Directory.CreateDirectory(PlatformSDKRoot);
                }

				{
					string VersionFilename = Path.Combine(PlatformSDKRoot, CurrentlyInstalledSDKStringManifest);
					if (File.Exists(VersionFilename))
					{
						File.Delete(VersionFilename);
					}

					string EnvVarFile = Path.Combine(PlatformSDKRoot, SDKEnvVarsFile);
					if (File.Exists(EnvVarFile))
					{
						File.Delete(EnvVarFile);
					}

					using (StreamWriter Writer = File.CreateText(VersionFilename))
					{
						Writer.WriteLine(InstalledSDKVersionString);
						Writer.WriteLine("ManualSDK");
					}
				}
			}
		}

		protected bool SetLastRunAutoSDKScriptVersion(string LastRunScriptVersion)
		{
			String PlatformSDKRoot = GetPathToPlatformAutoSDKs();
			if (Directory.Exists(PlatformSDKRoot))
			{
				string VersionFilename = Path.Combine(PlatformSDKRoot, LastRunScriptVersionManifest);
				if (File.Exists(VersionFilename))
				{
					File.Delete(VersionFilename);
				}

				using (StreamWriter Writer = File.CreateText(VersionFilename))
				{
					Writer.WriteLine(LastRunScriptVersion);
					return true;
				}
			}
			return false;
		}

		// Returns Hook names as needed by the platform
		// (e.g. can be overridden with custom executables or scripts)
		protected virtual string GetHookExecutableName(SDKHookType Hook)
		{
			if(BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				if (Hook == SDKHookType.Uninstall)
				{
					return Tag.Binary.Unsetup + Tag.Ext.Bat;
				}
				else
				{
					return Tag.Binary.Setup + Tag.Ext.Bat;
				}
			}
			else
			{
				if (Hook == SDKHookType.Uninstall)
				{
					return Tag.Binary.Unsetup + Tag.Ext.Shell;
				}
				else
				{
					return Tag.Binary.Setup + Tag.Ext.Shell;
				}
			}
		}

		// Runs install/uninstall hooks for SDK
		protected virtual bool RunAutoSDKHooks
		(
            string AbsolutePathPlatformSDKRoot,
            string SDKVersionString,
            SDKHookType HookType,
            bool bHookCanBeNonExistent = true
		)
		{
			if (!IsAutoSDKSafe())
			{
				Log.TraceLog(GetSDKTargetPlatformName() + " attempted to run SDK hook which could have damaged manual SDK install!");
				return false;
			}
			if (SDKVersionString != "")
			{
				string SDKDirectory = Path.Combine(AbsolutePathPlatformSDKRoot, SDKVersionString);
				string HookExe = Path.Combine(SDKDirectory, GetHookExecutableName(HookType));

				if (File.Exists(HookExe))
				{
					Log.TraceLog("Running {0} hook {1}", HookType, HookExe);

					// run it
					Process HookProcess = new Process();
					HookProcess.StartInfo.WorkingDirectory = SDKDirectory;
					HookProcess.StartInfo.FileName = HookExe;
					HookProcess.StartInfo.Arguments = "";
					HookProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                    // seems to break the build machines?
                    //HookProcess.StartInfo.UseShellExecute = false;
                    //HookProcess.StartInfo.RedirectStandardOutput = true;
                    //HookProcess.StartInfo.RedirectStandardError = true;					

                    // Installers may require administrator access to succeed. so run as an admmin.
                    HookProcess.StartInfo.Verb = "runas";
                    HookProcess.Start();
                    HookProcess.WaitForExit();

                    //LogAutoSDK(HookProcess.StandardOutput.ReadToEnd());
                    //LogAutoSDK(HookProcess.StandardError.ReadToEnd());
                    if (HookProcess.ExitCode != 0)
					{
						Log.TraceLog("{0} exited uncleanly (returned {1}), considering it failed.", HookExe, HookProcess.ExitCode);
						return false;
					}

					return true;
				}
				else
				{
					Log.TraceLog("File {0} does not exist", HookExe);
				}
			}
			else
			{
				Log.TraceLog("Version string is blank for {0}. Can't determine {1} hook.", AbsolutePathPlatformSDKRoot, HookType.ToString());
			}

			return bHookCanBeNonExistent;
		}

		// Loads environment variables from SDK
		// If any commands are added or removed the handling needs to be duplicated in TargetPlatformManagerModule.cpp
		protected bool SetupEnvironmentFromAutoSDK(string AbsolutePathPlatformSDKRoot)
		{
			string EnvVarFile = Path.Combine(AbsolutePathPlatformSDKRoot, SDKEnvVarsFile);
			if (File.Exists(EnvVarFile))
			{
				using (StreamReader Reader = new StreamReader(EnvVarFile))
				{
					List<string> PathAdds = new List<string>();
					List<string> PathRemoves = new List<string>();

					List<string> EnvVarNames = new List<string>();
					List<string> EnvVarValues = new List<string>();

					bool bNeedsToWriteAutoSetupEnvVar = true;
					String PlatformSetupEnvVar = GetPlatformAutoSDKSetupEnvVar();
					for (; ; )
					{
						string VariableString = Reader.ReadLine();
						if (VariableString == null)
						{
							break;
						}

						string[] Parts = VariableString.Split('=');
						if (Parts.Length != 2)
						{
							Log.TraceLog("Incorrect environment variable declaration:");
							Log.TraceLog(VariableString);
							return false;
						}

						if (String.Compare(Parts[0], "strippath", true) == 0)
						{
							PathRemoves.Add(Parts[1]);
						}
						else if (String.Compare(Parts[0], "addpath", true) == 0)
						{
							PathAdds.Add(Parts[1]);
						}
						else
						{
							if (String.Compare(Parts[0], PlatformSetupEnvVar) == 0)
							{
								bNeedsToWriteAutoSetupEnvVar = false;
							}
							// convenience for setup.bat writers.  Trim any accidental whitespace from variable names/values.
							EnvVarNames.Add(Parts[0].Trim());
							EnvVarValues.Add(Parts[1].Trim());
						}
					}

					// don't actually set anything until we successfully validate and read all values in.
					// we don't want to set a few vars, return a failure, and then have a platform try to
					// build against a manually installed SDK with half-set env vars.
					for (int i = 0; i < EnvVarNames.Count; ++i)
					{
						string EnvVarName  = EnvVarNames[i];
						string EnvVarValue = EnvVarValues[i];
						Log.TraceVerbose("Setting variable '{0}' to '{1}'", EnvVarName, EnvVarValue);
						Environment.SetEnvironmentVariable(EnvVarName, EnvVarValue);
					}

                    // actually perform the PATH stripping / adding.
                    String OrigPathVar = Environment.GetEnvironmentVariable("PATH");
                    String PathDelimiter = BuildPlatform.GetPathVarDelimiter();
                    String[] PathVars = { };
                    if (!String.IsNullOrEmpty(OrigPathVar))
                    {
                        PathVars = OrigPathVar.Split(PathDelimiter.ToCharArray());
                    }
                    else
                    {
                        Log.TraceVerbose("Path environment variable is null during AutoSDK");
                    }

					List<String> ModifiedPathVars = new List<string>();
					ModifiedPathVars.AddRange(PathVars);

					// perform removes first, in case they overlap with any adds.
					foreach (String PathRemove in PathRemoves)
					{
						foreach (String PathVar in PathVars)
						{
							if (PathVar.IndexOf(PathRemove, StringComparison.OrdinalIgnoreCase) >= 0)
							{
								Log.TraceVerbose("Removing Path: '{0}'", PathVar);
								ModifiedPathVars.Remove(PathVar);
							}
						}
					}

					// remove all the of ADDs so that if this function is executed multiple times, the paths will be guaranteed to be in the same order after each run.
					// If we did not do this, a 'remove' that matched some, but not all, of our 'adds' would cause the order to change.
					foreach (String PathAdd in PathAdds)
					{
						foreach (String PathVar in PathVars)
						{
							if (String.Compare(PathAdd, PathVar, true) == 0)
							{
								Log.TraceVerbose("Removing Path: '{0}'", PathVar);
								ModifiedPathVars.Remove(PathVar);
							}
						}
					}

					// perform adds, but don't add duplicates
					foreach (String PathAdd in PathAdds)
					{
						if (!ModifiedPathVars.Contains(PathAdd))
						{
							Log.TraceVerbose("Adding Path: '{0}'", PathAdd);
							ModifiedPathVars.Add(PathAdd);
						}
					}

					String ModifiedPath = String.Join(PathDelimiter, ModifiedPathVars);
					Environment.SetEnvironmentVariable("PATH", ModifiedPath);

					Reader.Close();

					// write out environment variable command so any process using this commandfile will mark itself as having had autosdks set up.
					// avoids child processes spuriously detecting manualsdks.
					if (bNeedsToWriteAutoSetupEnvVar)
					{
						using (StreamWriter Writer = File.AppendText(EnvVarFile))
						{
							Writer.WriteLine("{0}=1", PlatformSetupEnvVar);
						}
						// set the variable in the local environment in case this process spawns any others.
						Environment.SetEnvironmentVariable(PlatformSetupEnvVar, "1");
					}

					// make sure we know that we've modified the local environment, invalidating manual installs for this run.
					bLocalProcessSetupAutoSDK = true;

					return true;
				}
			}
			else
			{
				Log.TraceLog("Cannot set up environment for {1} because command file {2} does not exist.", AbsolutePathPlatformSDKRoot, EnvVarFile);
			}

			return false;
		}

		protected void InvalidateCurrentlyInstalledAutoSDK()
		{
			String PlatformSDKRoot = GetPathToPlatformAutoSDKs();
			if (Directory.Exists(PlatformSDKRoot))
			{
				string SDKFilename = Path.Combine(PlatformSDKRoot, CurrentlyInstalledSDKStringManifest);
				if (File.Exists(SDKFilename))
				{
					File.Delete(SDKFilename);
				}

				string VersionFilename = Path.Combine(PlatformSDKRoot, LastRunScriptVersionManifest);
				if (File.Exists(VersionFilename))
				{
					File.Delete(VersionFilename);
				}

				string EnvVarFile = Path.Combine(PlatformSDKRoot, SDKEnvVarsFile);
				if (File.Exists(EnvVarFile))
				{
					File.Delete(EnvVarFile);
				}
			}
		}

		// Currently installed AutoSDK is written out to a text file in a known location.
		// This function just compares the file's contents with the current requirements.
		public SDKStatus HasRequiredAutoSDKInstalled()
		{
			if (PlatformSupportsAutoSDKs() && HasAutoSDKSystemEnabled())
			{
				string AutoSDKRoot = GetPathToPlatformAutoSDKs();
				if (AutoSDKRoot != "")
				{
					// check script version so script fixes can be propagated without touching every build machine's CurrentlyInstalled file manually.
					bool bScriptVersionMatches = false;
                    if (GetLastRunScriptVersionString(AutoSDKRoot, out string CurrentScriptVersionString) && 
						CurrentScriptVersionString == GetRequiredScriptVersionString())
                    {
                        bScriptVersionMatches = true;
                    }

                    // check to make sure OutputEnvVars doesn't need regenerating
                    string EnvVarFile = Path.Combine(AutoSDKRoot, SDKEnvVarsFile);
					bool bEnvVarFileExists = File.Exists(EnvVarFile);

                    if (bEnvVarFileExists && 
						GetCurrentlyInstalledSDKString(AutoSDKRoot, out string CurrentSDKString) && 
						CurrentSDKString == GetRequiredSDKString() && bScriptVersionMatches)
                    {
                        return SDKStatus.Valid;
                    }
                    return SDKStatus.Invalid;
				}
			}
			return SDKStatus.Invalid;
		}

		// This tracks if we have already checked the sdk installation.
		private Int32 SDKCheckStatus = -1;

		// true if we've ever overridden the process's environment with AutoSDK data.  After that, manual installs cannot be considered valid ever again.
		private bool bLocalProcessSetupAutoSDK = false;

        protected bool HasSetupAutoSDK() => bLocalProcessSetupAutoSDK || HasParentProcessSetupAutoSDK();

        protected bool HasParentProcessSetupAutoSDK()
		{
			bool bParentProcessSetupAutoSDK = false;

			String AutoSDKSetupVarName = GetPlatformAutoSDKSetupEnvVar();
			String AutoSDKSetupVar     = Environment.GetEnvironmentVariable(AutoSDKSetupVarName);

			if (!String.IsNullOrEmpty(AutoSDKSetupVar))
			{
				bParentProcessSetupAutoSDK = true;
			}
			return bParentProcessSetupAutoSDK;
		}

		public SDKStatus HasRequiredManualSDK()
		{
			if (HasSetupAutoSDK())
			{
				return SDKStatus.Invalid;
			}

			// manual installs are always invalid if we have modified the process's environment for AutoSDKs
			return HasRequiredManualSDKInternal();
		}

		// for platforms with destructive AutoSDK.  Report if any manual sdk is installed that may be damaged by an autosdk.
		protected virtual bool HasAnyManualInstall() => false;

		// tells us if the user has a valid manual install.
		protected abstract SDKStatus HasRequiredManualSDKInternal();

		// some platforms will fail if there is a manual install that is the WRONG manual install.
		protected virtual bool AllowInvalidManualInstall() => true;

		// platforms can choose if they prefer a correct the the AutoSDK install over the manual install.
		protected virtual bool PreferAutoSDK() => true;

		// some platforms don't support parallel SDK installs.
		// AutoSDK on these platforms will actively damage an existing manual install by overwriting files in it.
		// AutoSDK must NOT run any setup if a manual install exists in this case.
		protected virtual bool IsAutoSDKDestructive() => false;

		// Runs batch files if necessary to set up required AutoSDK.
		// AutoSDKs are SDKs that have not been setup through a formal installer, but rather come from
		// a source control directory, or other local copy.
		private void SetupAutoSDK()
		{
			if (IsAutoSDKSafe() && PlatformSupportsAutoSDKs() && HasAutoSDKSystemEnabled())
			{
				// run installation for autosdk if necessary.
				if (HasRequiredAutoSDKInstalled() == SDKStatus.Invalid)
				{
					//reset check status so any checking sdk status after the attempted setup will do a real check again.
					SDKCheckStatus = -1;

					string AutoSDKRoot = GetPathToPlatformAutoSDKs();
                    GetCurrentlyInstalledSDKString(AutoSDKRoot, out string CurrentSDKString);

                    // switch over (note that version string can be empty)
                    if (!RunAutoSDKHooks(AutoSDKRoot, CurrentSDKString, SDKHookType.Uninstall))
					{
						Log.TraceLog("Failed to uninstall currently installed SDK {0}", CurrentSDKString);
						InvalidateCurrentlyInstalledAutoSDK();
						return;
					}
					// delete Manifest file to avoid multiple uninstalls
					InvalidateCurrentlyInstalledAutoSDK();

					if (!RunAutoSDKHooks(AutoSDKRoot, GetRequiredSDKString(), SDKHookType.Install, false))
					{
						Log.TraceLog("Failed to install required SDK {0}.  Attemping to uninstall", GetRequiredSDKString());
						RunAutoSDKHooks(AutoSDKRoot, GetRequiredSDKString(), SDKHookType.Uninstall, false);
						return;
					}

					string EnvVarFile = Path.Combine(AutoSDKRoot, SDKEnvVarsFile);
					if (!File.Exists(EnvVarFile))
					{
						Log.TraceLog("Installation of required SDK {0}.  Did not generate Environment file {1}", GetRequiredSDKString(), EnvVarFile);
						RunAutoSDKHooks(AutoSDKRoot, GetRequiredSDKString(), SDKHookType.Uninstall, false);
						return;
					}

					SetCurrentlyInstalledAutoSDKString(GetRequiredSDKString());
					SetLastRunAutoSDKScriptVersion(GetRequiredScriptVersionString());
				}

				// fixup process environment to match autosdk
				SetupEnvironmentFromAutoSDK();
			}
		}

		#endregion

		#region public AutoSDKs Utility

		// Enum describing types of hooks a platform SDK can have
		public enum SDKHookType
		{
			Install,  // setup.bat,   setup.sh 
			Uninstall // unsetup.bat, unsetup.sh
		};

		
		// Returns platform-specific name used in SDK repository
		public virtual string GetSDKTargetPlatformName() => "";

		/* Whether or not we should try to automatically switch SDKs when asked to validate the platform's SDK state. */
		public static bool bAllowAutoSDKSwitching = true;

		public SDKStatus SetupEnvironmentFromAutoSDK()
		{
			string PlatformSDKRoot = GetPathToPlatformAutoSDKs();

			// load environment variables from current SDK
			if (!SetupEnvironmentFromAutoSDK(PlatformSDKRoot))
			{
				Log.TraceLog("Failed to load environment from required SDK {0}", GetRequiredSDKString());
				InvalidateCurrentlyInstalledAutoSDK();
				return SDKStatus.Invalid;
			}
			return SDKStatus.Valid;
		}

		
		// Whether the required external SDKs are installed for this platform.
		// Could be either a manual install or an AutoSDK.
		public SDKStatus HasRequiredSDKsInstalled()
		{
			// avoid redundant potentially expensive SDK checks.
			if (SDKCheckStatus == -1)
			{
				bool bHasManualSDK = HasRequiredManualSDK() == SDKStatus.Valid;
				bool bHasAutoSDK   = HasRequiredAutoSDKInstalled() == SDKStatus.Valid;

				// Per-Platform implementations can choose how to handle non-Auto SDK detection / handling.
				SDKCheckStatus = (bHasManualSDK || bHasAutoSDK) ? 1 : 0;
			}
			return SDKCheckStatus == 1 ? SDKStatus.Valid : SDKStatus.Invalid;
		}

		// Arbitrates between manual SDKs and setting up AutoSDK based on program options and platform preferences.
		public void ManageAndValidateSDK()
		{
			// do not modify installed manifests if parent process has already set everything up.
			// this avoids problems with determining IsAutoSDKSafe and doing an incorrect invalidate.
			if (bAllowAutoSDKSwitching && !HasParentProcessSetupAutoSDK())
			{
				bool bSetSomeSDK = false;
				bool bHasRequiredManualSDK = HasRequiredManualSDK() == SDKStatus.Valid;
				if (IsAutoSDKSafe() && (PreferAutoSDK() || !bHasRequiredManualSDK))
				{
					SetupAutoSDK();
					bSetSomeSDK = true;
				}

				//Setup manual SDK if autoSDK setup was skipped or failed for whatever reason.
				if (bHasRequiredManualSDK && (HasRequiredAutoSDKInstalled() != SDKStatus.Valid))
				{
					SetupManualSDK();
					bSetSomeSDK = true;
				}

				if (!bSetSomeSDK)
				{
					InvalidateCurrentlyInstalledAutoSDK();
				}
			}

			PrintSDKInfo();
		}

		public void PrintSDKInfo()
		{
			if (HasRequiredSDKsInstalled() == SDKStatus.Valid)
			{
				bool bHasRequiredManualSDK = HasRequiredManualSDK() == SDKStatus.Valid;
				if (HasSetupAutoSDK())
				{
					string PlatformSDKRoot = GetPathToPlatformAutoSDKs();
					Log.TraceLog(GetSDKTargetPlatformName() + " using SDK from: " + Path.Combine(PlatformSDKRoot, GetRequiredSDKString()));
				}
				else if (bHasRequiredManualSDK)
				{
					Log.TraceLog(this.ToString() + " using manually installed SDK " + GetRequiredSDKString());
				}
				else
				{
					Log.TraceLog(this.ToString() + " setup error.  Inform platform team.");
				}
			}
			else
			{
				Log.TraceLog(this.ToString() + " has no valid SDK");
			}
		}

		#endregion
	}
}
