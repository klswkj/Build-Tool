using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using BuildToolUtilities;

namespace BuildTool
{
	// Utility functions
	public static class Utils
	{
		// Whether we are currently running on Mono platform.  
		// We cache this statically because it is a bit slow to check.
#if NET_CORE
		public static readonly bool IsRunningOnMono = true;
#else
		public static readonly bool IsRunningOnMono = Type.GetType("Mono.Runtime") != null;
#endif
		
		// Runs a local process and pipes the output to a file
		public static int RunLocalProcessAndPrintfOutput(ProcessStartInfo InProcessStartInfo)
		{
			string AppName = Path.GetFileNameWithoutExtension(InProcessStartInfo.FileName);
			string LogFilenameBase = string.Format("{0}_{1}", AppName, DateTime.Now.ToString("yyyy.MM.dd-HH.mm.ss"));
			string AutomationToolLogDir = Path.Combine(BuildTool.EngineDirectory.FullName, Tag.Directory.ExternalTools, Tag.Module.ExternalTool.AutomationTool, Tag.Directory.Saved, Tag.Directory.Logs);
			string LogFilename = "";
			for (int Attempt = 1; Attempt < 100; ++Attempt)
			{
				try
				{
					if (!Directory.Exists(AutomationToolLogDir))
					{
						string IniPath = BuildTool.GetRemoteIniPath();
						if(string.IsNullOrEmpty(IniPath))
						{
							break;
						}

						AutomationToolLogDir = Path.Combine(IniPath, Tag.Directory.Saved, Tag.Directory.Logs);
						if(!Directory.Exists(AutomationToolLogDir) 
							&& !Directory.CreateDirectory(AutomationToolLogDir).Exists)
						{
							break;
						}
					}

					string LogFilenameBaseToCreate = LogFilenameBase;
					if (1 < Attempt)
					{
						LogFilenameBaseToCreate += "_" + Attempt;
					}

					LogFilenameBaseToCreate += Tag.Ext.Txt;
					string LogFilenameToCreate = Path.Combine(AutomationToolLogDir, LogFilenameBaseToCreate);

					if (File.Exists(LogFilenameToCreate))
					{
						continue;
					}

					File.CreateText(LogFilenameToCreate).Close();
					LogFilename = LogFilenameToCreate;
					break;
				}
				catch (IOException)
				{
					//fatal error, let report to console
					Debugger.Break();
					break;
				}
			}

			void Output(object sender, DataReceivedEventArgs Args)
			{
				if (Args != null && Args.Data != null)
				{
					string data = Args.Data.TrimEnd();
					if (string.IsNullOrEmpty(data))
					{
						return;
					}

					if (!string.IsNullOrEmpty(LogFilename))
					{
						File.AppendAllLines(LogFilename, data.Split('\n'));
					}
					else
					{
						Log.TraceInformation(data);
					}
				}
			}

			Process LocalProcess = new Process
			{
				StartInfo = InProcessStartInfo
			};
			LocalProcess.OutputDataReceived += Output;
			LocalProcess.ErrorDataReceived  += Output;
			var ExitCode = StringUtils.RunLocalProcess(LocalProcess);
			if(ExitCode != 0 && !string.IsNullOrEmpty(LogFilename))
			{
				Log.TraceError("Process \'{0}\' failed. Details are in \'{1}\'", AppName, LogFilename);
			}

			return ExitCode;
		}

		// Given a file path and a directory, returns a file path that is relative to the specified directory
		public static string MakePathRelativeTo
		(
            string SourcePathToConvert,
            string RelativeToDirectory,                 // The directory that the source file path should be converted to be relative to.
			bool   AlwaysTreatSourceAsDirectory = false // True if we should treat the source path like a directory even if it doesn't end with a path separator
		)
		{
			if (String.IsNullOrEmpty(RelativeToDirectory))
			{
				RelativeToDirectory = "."; // Assume CWD
			}

			string AbsolutePath = SourcePathToConvert;

			if (!Path.IsPathRooted(AbsolutePath))
			{
				AbsolutePath = Path.GetFullPath(SourcePathToConvert);
			}

			bool SourcePathEndsWithDirectorySeparator = AbsolutePath.EndsWith(Path.DirectorySeparatorChar.ToString()) || AbsolutePath.EndsWith(Path.AltDirectorySeparatorChar.ToString());
			
			if (AlwaysTreatSourceAsDirectory 
				&& !SourcePathEndsWithDirectorySeparator)
			{
				AbsolutePath += Path.DirectorySeparatorChar;
			}

			Uri AbsolutePathUri = new Uri(AbsolutePath);

			string AbsoluteRelativeDirectory = RelativeToDirectory;
			if (!Path.IsPathRooted(AbsoluteRelativeDirectory))
			{
				AbsoluteRelativeDirectory = Path.GetFullPath(AbsoluteRelativeDirectory);
			}

			// Make sure the directory has a trailing directory separator so that the relative directory that
			// MakeRelativeUri creates doesn't include our directory -- only the directories beneath it!
			if (!AbsoluteRelativeDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()) 
			 && !AbsoluteRelativeDirectory.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
			{
				AbsoluteRelativeDirectory += Path.DirectorySeparatorChar;
			}

			// Convert to URI form which is where we can make the relative conversion happen
			Uri AbsoluteRelativeDirectoryUri = new Uri(AbsoluteRelativeDirectory);

			// Ask the URI system to convert to a nicely formed relative path, then convert it back to a regular path string
			Uri UriRelativePath = AbsoluteRelativeDirectoryUri.MakeRelativeUri(AbsolutePathUri);
			string RelativePath = Uri.UnescapeDataString(UriRelativePath.ToString()).Replace('/', Path.DirectorySeparatorChar);

			// If we added a directory separator character earlier on, remove it now
			if (!SourcePathEndsWithDirectorySeparator && AlwaysTreatSourceAsDirectory && RelativePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
			{
				RelativePath = RelativePath.Substring(0, RelativePath.Length - 1);
			}

			// Uri.MakeRelativeUri is broken in Mono 2.x and sometimes returns broken path
			if (IsRunningOnMono)
			{
				// Check if result is correct
				string TestPath = Path.GetFullPath(Path.Combine(AbsoluteRelativeDirectory, RelativePath));
				string AbsoluteTestPath = StringUtils.CollapseRelativeDirectories(AbsolutePath);
				if (TestPath != AbsoluteTestPath)
				{
					TestPath += "/";
					if (TestPath != AbsoluteTestPath)
					{
						// Fix the path. @todo Mac: replace this hack with something better
						RelativePath = "../" + RelativePath;
					}
				}
			}

			return RelativePath;
		}

		// Returns true if the specified Process has been created, started and remains valid (i.e. running).
		public static bool IsValidProcess(Process InProcess)
		{
			// null objects are always invalid
			if (InProcess == null)
            {
                return false;
            }
            // due to multithreading on Windows, lock the object
            lock (InProcess)
			{
				// Mono has a specific requirement if testing for an alive process
				if (IsRunningOnMono)
                {
                    return InProcess.Handle != IntPtr.Zero; // native handle to the process
                }

                // on Windows, simply test the process ID to be non-zero. 
                // note that this can fail and have a race condition in threads,
				// but the framework throws an exception when this occurs.
                try
				{
					return InProcess.Id != 0;
				}
				catch 
				{
				} // all exceptions can be safely caught and ignored, meaning the process is not started or has stopped.
			}
			return false;
		}

		// Returns the User Settings Directory path.
		// This matches FPlatformProcess::UserSettingsDir().
		// NOTE: This function may return null.
		// Some accounts (eg. the SYSTEM account on Windows) do not have a personal folder, and Jenkins runs using this account by default.
		public static DirectoryReference GetUserSettingDirectory()
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac)
			{
				return new DirectoryReference(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), Tag.Directory.Library, Tag.Directory.ApplcationSupport, Tag.Directory.Engine));
			}
			else if (Environment.OSVersion.Platform == PlatformID.Unix)
			{
				return new DirectoryReference(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Engine"));
			}
			else
			{
				// ($UserName)/AppData/Local
				// Not all user accounts have a local application data directory (eg. SYSTEM, used by Jenkins for builds).
				string DirectoryName = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				if(String.IsNullOrEmpty(DirectoryName))
				{
					return null;
				}
				else
				{
					return new DirectoryReference(DirectoryName);
				}
			}
		}

		enum LOGICAL_PROCESSOR_RELATIONSHIP
		{
			RelationProcessorCore,
			RelationNumaNode,
			RelationCache,
			RelationProcessorPackage,
			RelationGroup,
			RelationAll = 0xffff
		}

		[DllImport("kernel32.dll", SetLastError=true)]
		extern static bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType, IntPtr Buffer, ref uint ReturnedLength);

		// Gets the number of logical cores. We use this rather than Environment.ProcessorCount when possible to handle machines with > 64 cores (the single group limit available to the .NET framework).
		// <returns>The number of logical cores.</returns>
		public static int GetLogicalProcessorCount()
		{
			// This function uses Windows P/Invoke calls; if we're on Mono, just return the default.
			if(!Utils.IsRunningOnMono)
			{
				const int ERROR_INSUFFICIENT_BUFFER = 122;

				// Determine the required buffer size to store the processor information
				uint ReturnLength = 0;
				if(!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationGroup, IntPtr.Zero, ref ReturnLength) 
					&& Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
				{
					// Allocate a buffer for it
					IntPtr pInteger = Marshal.AllocHGlobal((int)ReturnLength);
					try
					{
						if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationGroup, pInteger, ref ReturnLength))
						{
							int Count = 0;
							for(int Pos = 0; Pos < ReturnLength;)
							{
								LOGICAL_PROCESSOR_RELATIONSHIP Type = (LOGICAL_PROCESSOR_RELATIONSHIP)Marshal.ReadInt16(pInteger, Pos);
								if(Type == LOGICAL_PROCESSOR_RELATIONSHIP.RelationGroup)
								{
									// Read the values from the embedded GROUP_RELATIONSHIP structure
									int GroupRelationshipPos = Pos + 8;
									int ActiveGroupCount = Marshal.ReadInt16(pInteger, GroupRelationshipPos + 2);

									// Read the processor counts from the embedded PROCESSOR_GROUP_INFO structures
									int GroupInfoPos = GroupRelationshipPos + 24;
									for(int GroupIdx = 0; GroupIdx < ActiveGroupCount; ++GroupIdx)
									{
										Count += Marshal.ReadByte(pInteger, GroupInfoPos + 1);
										GroupInfoPos += 40 + IntPtr.Size;
									}
								}
								Pos += Marshal.ReadInt32(pInteger, Pos + 4);
							}
							return Count;
						}
					}
					finally
					{
						Marshal.FreeHGlobal(pInteger);		
					}
				}
			}
			return Environment.ProcessorCount;
		}

		// Gets the number of physical cores, excluding hyper threading.
		// <returns>The number of physical cores, or -1 if it could not be obtained</returns>
		public static int GetPhysicalProcessorCount()
		{
			// This function uses Windows P/Invoke calls; if we're on Mono, just fail.
			if (Utils.IsRunningOnMono)
			{
				return -1;
			}

			const int ERROR_INSUFFICIENT_BUFFER = 122;

			// Determine the required buffer size to store the processor information
			uint ReturnLength = 0;
			if(!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref ReturnLength) 
				&& Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
			{
				// Allocate a buffer for it
				IntPtr pInteger = Marshal.AllocHGlobal((int)ReturnLength);
				try
				{
					if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, pInteger, ref ReturnLength))
					{
						// As per-MSDN, this will return one structure per physical processor.
						// Each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX structure is of a variable size,
						// so just skip through the list and count the number of entries.
						int Count = 0;
						for(int Pos = 0; Pos < ReturnLength; )
						{
							LOGICAL_PROCESSOR_RELATIONSHIP Type = (LOGICAL_PROCESSOR_RELATIONSHIP)Marshal.ReadInt16(pInteger, Pos);
							if(Type == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
							{
								++Count;
							}
							Pos += Marshal.ReadInt32(pInteger, Pos + 4);
						}
						return Count;
					}
				}
				finally
				{
					Marshal.FreeHGlobal(pInteger);		
				}
			}

			return -1;
		}

		// Executes a list of custom build step scripts
		public static void ExecuteCustomBuildSteps(FileReference[] ScriptFilesToExecute)
		{
			foreach(FileReference ScriptFile in ScriptFilesToExecute)
			{
				ProcessStartInfo StartInfo = new ProcessStartInfo
				{
					FileName = BuildHostPlatform.Current.ShellPath.FullName
				};

				if (BuildHostPlatform.Current.ShellType == ShellType.Cmd)
				{
					StartInfo.Arguments = String.Format("/C \"{0}\"", ScriptFile.FullName);
				}
				else
				{
					StartInfo.Arguments = String.Format("\"{0}\"", ScriptFile.FullName);
				}

				int ReturnCode = StringUtils.RunLocalProcessAndLogOutput(StartInfo);
				if(ReturnCode != 0)
				{
					throw new BuildException("Custom build step terminated with exit code {0}", ReturnCode);
				}
			}
		}

		// Find all the platforms in a given class
		// <param name="Class">Class of platforms to return</param>
		// <returns>Array of platforms in the given class</returns>
		public static BuildTargetPlatform[] GetPlatformsInClass(BuildPlatformClass Class)
		{
			switch (Class)
			{
				case BuildPlatformClass.All:
					return BuildTargetPlatform.GetValidPlatforms();
				case BuildPlatformClass.Desktop:
					return BuildPlatform.GetPlatformsInGroup(BuildPlatformGroup.Desktop).ToArray();
				case BuildPlatformClass.Editor:
					return new BuildTargetPlatform[] { BuildTargetPlatform.Win64, BuildTargetPlatform.Linux, BuildTargetPlatform.Mac };
				case BuildPlatformClass.Server:
					return new BuildTargetPlatform[] 
					{ BuildTargetPlatform.Win32, BuildTargetPlatform.Win64, BuildTargetPlatform.Linux, BuildTargetPlatform.LinuxAArch64, BuildTargetPlatform.Mac };
			}
			throw new ArgumentException(String.Format("'{0}' is not a valid value for BuldPlatformClass", (int)Class));
		}

		// Given a list of supported platforms, returns a list of names of platforms that should not be supported
		// <param name="SupportedPlatforms">List of supported platforms</param>
		// <param name="bIncludeUnbuildablePlatforms">If true, add platforms that are present but not available for compiling</param>
		// <returns>List of unsupported platforms in string format</returns>
		public static List<string> MakeListOfUnsupportedPlatforms(List<BuildTargetPlatform> SupportedPlatforms, bool bIncludeUnbuildablePlatforms)
		{
			List<string> OtherPlatformNameStrings = new List<string>();
			{
				List<BuildPlatformGroup> SupportedGroups = new List<BuildPlatformGroup>();

				// look at each group to see if any supported platforms are in it
				foreach (BuildPlatformGroup Group in BuildPlatformGroup.GetValidGroups())
				{
					// get the list of platforms registered to this group, if any
					List<BuildTargetPlatform> Platforms = BuildPlatform.GetPlatformsInGroup(Group);
					if (Platforms != null)
					{
						// loop over each one
						foreach (BuildTargetPlatform Platform in Platforms)
						{
							// if it's a compiled platform, then add this group to be supported
							if (SupportedPlatforms.Contains(Platform))
							{
								SupportedGroups.Add(Group);
							}
						}
					}
				}

				// loop over groups one more time, anything NOT in SupportedGroups is now unsupported, and should be added to the output list
				foreach (BuildPlatformGroup Group in BuildPlatformGroup.GetValidGroups())
				{
					if (SupportedGroups.Contains(Group) == false)
					{
						OtherPlatformNameStrings.Add(Group.ToString());
					}
				}

				foreach (BuildTargetPlatform CurPlatform in BuildTargetPlatform.GetValidPlatforms())
				{
					bool ShouldConsider = true;

					// If we have a platform and a group with the same name, don't add the platform
					// to the other list if the same-named group is supported.  This is a lot of
					// lines because we need to do the comparisons as strings.
					string CurPlatformString = CurPlatform.ToString();
					foreach (BuildPlatformGroup Group in BuildPlatformGroup.GetValidGroups())
					{
						if (Group.ToString().Equals(CurPlatformString))
						{
							ShouldConsider = false;
							break;
						}
					}

					// Don't add our current platform to the list of platform sub-directory names that
					// we'll skip source files for
					if (ShouldConsider && !SupportedPlatforms.Contains(CurPlatform))
					{
						OtherPlatformNameStrings.Add(CurPlatform.ToString());
					}
					// if a platform isn't available to build, then return it 
					else if (bIncludeUnbuildablePlatforms && !BuildPlatform.IsPlatformAvailable(CurPlatform))
					{
						OtherPlatformNameStrings.Add(CurPlatform.ToString());
					}
				}

				return OtherPlatformNameStrings;
			}
		}
	}
}
