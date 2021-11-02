using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using BuildToolUtilities;

namespace BuildTool
{
	// The type of shell supported by this platform. Used to configure command line arguments.
	public enum ShellType
	{
		Shell, // The Bourne shell
		Cmd,   // Windows command interpreter
	}

	// Host platform abstraction
	public abstract class BuildHostPlatform
	{
		private static BuildHostPlatform CurrentPlatform;
#if MAC
#pragma warning disable IDE1006 // Naming Styles
        private static bool bIsMac => File.Exists(Tag.Directory.MacOSVersion);
#pragma warning restore IDE1006 // Naming Styles
#endif
        abstract public BuildTargetPlatform Platform { get; }
		abstract public FileReference ShellPath { get; }
		abstract public ShellType ShellType { get; }
		internal abstract IEnumerable<ProjectFileFormat> GetDefaultProjectFileFormats();

		// Host platform singleton.
		static public BuildHostPlatform Current
		{
			get
			{
				if (CurrentPlatform == null)
				{
					BuildTargetPlatform RuntimePlatform = GetRuntimePlatform();
					if (RuntimePlatform == BuildTargetPlatform.Win64)
					{
						CurrentPlatform = new WindowsBuildHostPlatform();
					}
					else if (RuntimePlatform == BuildTargetPlatform.Mac)
					{
						CurrentPlatform = new MacBuildHostPlatform();
					}
					else if (RuntimePlatform == BuildTargetPlatform.Linux)
					{
						CurrentPlatform = new LinuxBuildHostPlatform();
					}
				}
				return CurrentPlatform;
			}
		}

		// Returns the name of platform UBT is running on. Internal use only. If you need access this this enum, use BuildHostPlatform.Current.Platform */
		private static BuildTargetPlatform GetRuntimePlatform()
		{
			PlatformID Platform = Environment.OSVersion.Platform;
			switch (Platform)
			{
				case PlatformID.Win32NT:
					return BuildTargetPlatform.Win64;
				case PlatformID.Unix:
#if MAC
					return bIsMac ? BuildTargetPlatform.Mac : BuildTargetPlatform.Linux;
#else
                    return BuildTargetPlatform.Linux;
#endif
				case PlatformID.MacOSX:
					return BuildTargetPlatform.Mac;
				default:
					throw new BuildException("Unhandled runtime platform " + Platform);
			}
		}

		// Class that holds information about a running process
		public sealed class ProcessInfo
		{
			public int    PID;               // Process ID
			public string ProcessName;       // Name of the process
			public string ProcessBinaryName; // Filename of the process binary

			public ProcessInfo(int InPID, string InName, string InFilename)
			{
				PID               = InPID;
				ProcessName       = InName;
				ProcessBinaryName = InFilename;
			}

			public ProcessInfo(Process Proc)
			{
				PID               = Proc.Id;
				ProcessName       = Proc.ProcessName;
				ProcessBinaryName = Path.GetFullPath(Proc.MainModule.FileName);
			}

			public override string ToString()
			{
				return String.Format("{0}, {1}", ProcessName, ProcessBinaryName);
			}
		}

		// For HotReloading
		// Gets all currently running processes.
		public virtual ProcessInfo[] GetProcesses()
		{
			Process[] AllProcesses = Process.GetProcesses();
			List<ProcessInfo> Result = new List<ProcessInfo>(AllProcesses.Length);
			foreach (Process Proc in AllProcesses)
			{
				try
				{
					if (!Proc.HasExited)
					{
						Result.Add(new ProcessInfo(Proc));
					}
				}
				catch 
				{
				}
			}
			return Result.ToArray();
		}

		public virtual ProcessInfo GetProcessByName(string Name)
		{
			ProcessInfo[] AllProcess = GetProcesses();
			foreach (ProcessInfo Info in AllProcess)
			{
				if (Info.ProcessName == Name)
				{
					return Info;
				}
			}
			return null;
		}

		public virtual ProcessInfo[] GetProcessesByName(string Name)
		{
			ProcessInfo[] AllProcess = GetProcesses();
			List<ProcessInfo> Result = new List<ProcessInfo>();
			foreach (ProcessInfo Info in AllProcess)
			{
				if (Info.ProcessName == Name)
				{
					Result.Add(Info);
				}
			}
			return Result.ToArray();
		}

		// <returns>An array of all module filenames associated with the process.
		// Can be empty of the process is no longer running.
		public virtual string[] GetProcessModules(int PID, string Filename)
		{
			List<string> Modules = new List<string>();

			try
			{
				Process Proc = Process.GetProcessById(PID);
				if (Proc != null)
				{
					foreach (ProcessModule Module in Proc.Modules.Cast<System.Diagnostics.ProcessModule>())
					{
						Modules.Add(Path.GetFullPath(Module.FileName));
					}
				}
			}
			catch
			{
			}
			return Modules.ToArray();
		}
	}

	class WindowsBuildHostPlatform : BuildHostPlatform
	{
        public override BuildTargetPlatform Platform => BuildTargetPlatform.Win64;

        public override FileReference ShellPath 
			=> new FileReference(Environment.GetEnvironmentVariable(Tag.EnvVar.COMSPEC));

        public override ShellType ShellType
		=> ShellType.Cmd;

		internal override IEnumerable<ProjectFileFormat> GetDefaultProjectFileFormats()
		{
			yield return ProjectFileFormat.VisualStudio;
		}
	}

	class MacBuildHostPlatform : BuildHostPlatform
	{
		public override BuildTargetPlatform Platform
		=> BuildTargetPlatform.Mac;

		public override FileReference ShellPath
		=> new FileReference(Tag.Directory.ShellPath);

		public override ShellType ShellType
		=> ShellType.Shell;

		// Currently Mono returns incomplete process names in Process.GetProcesses()
		// so we need to parse 'ps' output.
		public override ProcessInfo[] GetProcesses()
		{
			List<ProcessInfo> Result = new List<ProcessInfo>();

            ProcessStartInfo StartInfo = new ProcessStartInfo
            {
                FileName               = "ps",
                Arguments              = "-eaw -o pid,comm",
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true
            };

            Process Proc = new Process
            {
                StartInfo = StartInfo
            };
            try
			{
				Proc.Start();
				Proc.WaitForExit();
				for (string Line = Proc.StandardOutput.ReadLine(); Line != null; Line = Proc.StandardOutput.ReadLine())
				{
					Line = Line.Trim();
					int PIDEnd = Line.IndexOf(' ');
					string PIDString = Line.Substring(0, PIDEnd);
					if (PIDString != "PID")
					{
						string Filename = Line.Substring(PIDEnd + 1);
						int Pid = Int32.Parse(PIDString);
						try
						{
							Process ExistingProc = Process.GetProcessById(Pid);
							if (ExistingProc != null && 
								Pid != Process.GetCurrentProcess().Id && 
								ExistingProc.HasExited == false)
							{
								ProcessInfo ProcInfo = new ProcessInfo(ExistingProc.Id, Path.GetFileName(Filename), Filename);
								Result.Add(ProcInfo);
							}
						}
						catch 
						{
						}
					}
				}

			}
			catch 
			{
			}
			return Result.ToArray();
		}

		// Currently Mono returns incomplete list of modules for Process.Modules so we need to parse vmmap output.
		public override string[] GetProcessModules(int PID, string Filename)
		{
			// Add the process file name to the module list.
			// This is to make it compatible with the results of Process.Modules on Windows.
			HashSet<string> Modules = new HashSet<string> { Filename };

            ProcessStartInfo StartInfo = new ProcessStartInfo
            {
                FileName = "vmmap",
                Arguments = String.Format("{0} -w", PID),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            Process Proc = new Process
            {
                StartInfo = StartInfo
            };

            try
			{
				Proc.Start();
				// Start processing output before vmmap exits otherwise it's going to hang
				while (!Proc.WaitForExit(1))
				{
					ProcessVMMapOutput(Proc, Modules);
				}
				ProcessVMMapOutput(Proc, Modules);
			}
			catch
			{
			}
			return Modules.ToArray();
		}
		private void ProcessVMMapOutput(Process Proc, HashSet<string> Modules)
		{
			for (string Line = Proc.StandardOutput.ReadLine(); Line != null; Line = Proc.StandardOutput.ReadLine())
			{
				Line = Line.Trim();
				if (Line.EndsWith(Tag.Ext.Dylib))
				{
					const int SharingModeLength = 6;
					int SMStart = Line.IndexOf("SM=");
					int PathStart = SMStart + SharingModeLength;
					string Module = Line.Substring(PathStart).Trim();
					if (!Modules.Contains(Module))
					{
						Modules.Add(Module);
					}
				}
			}
		}

		internal override IEnumerable<ProjectFileFormat> GetDefaultProjectFileFormats()
		{
			yield return ProjectFileFormat.XCode;
		}
	}

	class LinuxBuildHostPlatform : BuildHostPlatform
	{
		public override BuildTargetPlatform Platform
		=> BuildTargetPlatform.Linux;

		public override FileReference ShellPath
		=> new FileReference(Tag.Directory.ShellPath);

		public override ShellType ShellType
		=> ShellType.Shell;

		// Currently Mono returns incomplete process names in Process.GetProcesses() so we need to use /proc
		// (also, Mono locks up during process traversal sometimes, trying to open /dev/snd/pcm*)
		public override ProcessInfo[] GetProcesses()
		{
			// @TODO: Implement for Linux
			return new List<ProcessInfo>().ToArray();
		}

		// Currently Mono returns incomplete list of modules for Process.Modules so we need to parse /proc/PID/maps.
		// (also, Mono locks up during process traversal sometimes, trying to open /dev/snd/pcm*)
		public override string[] GetProcessModules(int PID, string Filename)
		{
			// @TODO: Implement for Linux
			return new List<string>().ToArray();
		}

		internal override IEnumerable<ProjectFileFormat> GetDefaultProjectFileFormats()
		{
			yield return ProjectFileFormat.Make;
			yield return ProjectFileFormat.VisualStudioCode;
			yield return ProjectFileFormat.KDevelop;
			yield return ProjectFileFormat.QMake;
			yield return ProjectFileFormat.CMake;
			yield return ProjectFileFormat.CodeLite;
		}
	}
}
