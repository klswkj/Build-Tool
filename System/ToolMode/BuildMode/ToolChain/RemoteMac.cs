using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BuildToolUtilities;

namespace BuildTool
{
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0060 // Remove unused parameter
#pragma warning disable IDE0051 // Remove unused parameter
	// Stores information about how a local directory maps to a remote directory
	[DebuggerDisplay("{LocalDirectory}")]
	class RemoteMapping
	{
		public DirectoryReference LocalDirectory;
		public string             RemoteDirectory;

		public RemoteMapping(DirectoryReference InLocalDirectory, string InRemoteDirectory)
		{
			LocalDirectory  = InLocalDirectory;
			RemoteDirectory = InRemoteDirectory;
		}
	}

	// Handles uploading and building on a remote Mac
	class RemoteMac
	{
		// These two variables will be loaded from the XML config file in XmlConfigLoader.Init().
		[XMLConfigFile]
		private readonly string ServerName;

		[XMLConfigFile]
		private readonly string RemoteUserName;

		[XMLConfigFile]
		private readonly FileReference SSHPrivateKey;

		// The authentication used for Rsync (for the -e rsync flag).
		[XMLConfigFile]
		private readonly string RsyncAuthentication = "./ssh -i '${CYGWIN_SSH_PRIVATE_KEY}'";

		// The authentication used for SSH (probably similar to RsyncAuthentication).
		[XMLConfigFile]
		private readonly string SSHAuthentication = "-i '${CYGWIN_SSH_PRIVATE_KEY}'";

		// Save the specified port so that RemoteServerName is the machine address only
		private readonly int SSHServerPort = 22;	// Default ssh port

		private readonly FileReference RsyncEXE;
		private readonly FileReference SSHEXE;
		private readonly FileReference ProjectFile; // The project being built. Settings will be read from config files in this project.

		private readonly UProjectDescriptor ProjectDescriptor; // The project descriptor for the project being built.
		private readonly List<DirectoryReference> AdditionalPaths; // A set of directories containing additional paths to be built.

		private readonly string RemoteBaseDir; // The base directory on the remote machine

		private readonly List<RemoteMapping> Mappings; // Mappings from local directories to remote directories

		private readonly List<string> CommonSSHArguments;   // Arguments that are used by every Ssh call
		private readonly List<string> BasicRsyncArguments;  // Arguments that are used by every Rsync call
		private readonly List<string> CommonRsyncArguments; // Arguments that are used by directory Rsync call

        private readonly string IniBundleIdentifier = "";

        public RemoteMac(FileReference ProjectFile)
		{
			RsyncEXE = FileReference.Combine(BuildTool.EngineDirectory, "Extras", "ThirdPartyNotUE", "DeltaCopy", "Binaries", "Rsync.exe");
			SSHEXE = FileReference.Combine(BuildTool.EngineDirectory, "Extras", "ThirdPartyNotUE", "DeltaCopy", "Binaries", "Ssh.exe");
			this.ProjectFile = ProjectFile;
			if (ProjectFile != null)
			{
				ProjectDescriptor = UProjectDescriptor.FromFile(ProjectFile);
				AdditionalPaths = new List<DirectoryReference>();
				ProjectDescriptor.AddAdditionalPaths(AdditionalPaths/*, ProjectFile.Directory*/);
				if (AdditionalPaths.Count == 0)
				{
					AdditionalPaths = null;
				}
			}

			// Apply settings from the XML file
			XMLConfig.ApplyTo(this);

			// Get the project config file path
			DirectoryReference EngineIniPath = ProjectFile?.Directory;
			if (EngineIniPath == null && 
				BuildTool.GetRemoteIniPath() != null)
			{
				EngineIniPath = new DirectoryReference(BuildTool.GetRemoteIniPath());
			}

			ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, EngineIniPath, BuildTargetPlatform.IOS);

			// Read the project settings if we don't have anything in the build configuration settings
			if(String.IsNullOrEmpty(ServerName))
			{
				// Read the server name
				if (Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "RemoteServerName", out string IniServerName) && !String.IsNullOrEmpty(IniServerName))
				{
					ServerName = IniServerName;
				}
				else
				{
					throw new BuildException("Remote compiling requires a server name. Use the editor (Project Settings > IOS) to set up your remote compilation settings.");
				}

				// Parse the username
				if (Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "RSyncUsername", out string IniUserName) && !String.IsNullOrEmpty(IniUserName))
				{
					RemoteUserName = IniUserName;
				}
			}

			// Split port out from the server name
			int PortIdx = ServerName.LastIndexOf(':');
			if(PortIdx != -1)
			{
				string Port = ServerName.Substring(PortIdx + 1);
				if(!int.TryParse(Port, out SSHServerPort))
				{
					throw new BuildException("Unable to parse port number from '{0}'", ServerName);
				}
				ServerName = ServerName.Substring(0, PortIdx);
			}

			// If a user name is not set, use the current user
			if (String.IsNullOrEmpty(RemoteUserName))
			{
				RemoteUserName = Environment.UserName;
			}

			// Print out the server info
			Log.TraceInformation("[Remote] Using remote server '{0}' on port {1} (user '{2}')", ServerName, SSHServerPort, RemoteUserName);

			// Get the path to the SSH private key
			if (Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "SSHPrivateKeyOverridePath", out string OverrideSshPrivateKeyPath) && !String.IsNullOrEmpty(OverrideSshPrivateKeyPath))
			{
				SSHPrivateKey = new FileReference(OverrideSshPrivateKeyPath);
				if (!FileReference.Exists(SSHPrivateKey))
				{
					throw new BuildException("SSH private key specified in config file ({0}) does not exist.", SSHPrivateKey);
				}
			}

			Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "BundleIdentifier", out IniBundleIdentifier);

			// If it's not set, look in the standard locations. If that fails, spawn the batch file to generate one.
			if (SSHPrivateKey == null && !TryGetSshPrivateKey(out SSHPrivateKey))
			{
				Log.TraceWarning("No SSH private key found for {0}@{1}. Launching SSH to generate one.", RemoteUserName, ServerName);

				StringBuilder CommandLine = new StringBuilder();
				CommandLine.AppendFormat("/C \"\"{0}\"", FileReference.Combine(BuildTool.EngineDirectory, "Build", "BatchFiles", "MakeAndInstallSSHKey.bat"));
				CommandLine.AppendFormat(" \"{0}\"", SSHEXE);
				CommandLine.AppendFormat(" \"{0}\"", SSHServerPort);
				CommandLine.AppendFormat(" \"{0}\"", RsyncEXE);
				CommandLine.AppendFormat(" \"{0}\"", RemoteUserName);
				CommandLine.AppendFormat(" \"{0}\"", ServerName);
				CommandLine.AppendFormat(" \"{0}\"", DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.MyDocuments));
				CommandLine.AppendFormat(" \"{0}\"", GetLocalCygwinPath(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.MyDocuments)));
				CommandLine.AppendFormat(" \"{0}\"", BuildTool.EngineDirectory);
				CommandLine.Append("\"");

				using(Process ChildProcess = Process.Start(BuildHostPlatform.Current.ShellPath.FullName, CommandLine.ToString()))
				{
					ChildProcess.WaitForExit();
				}

				if(!TryGetSshPrivateKey(out SSHPrivateKey))
				{
					throw new BuildException("Failed to generate SSH private key for {0}@{1}.", RemoteUserName, ServerName);
				}
			}

			// Print the path to the private key
			Log.TraceInformation("[Remote] Using private key at {0}", SSHPrivateKey);

			// resolve the rest of the strings
			RsyncAuthentication = ExpandVariables(RsyncAuthentication);
			SSHAuthentication = ExpandVariables(SSHAuthentication);

			// Build a list of arguments for SSH
			CommonSSHArguments = new List<string>
			{
				"-o BatchMode=yes",
				SSHAuthentication,
				String.Format("-p {0}", SSHServerPort),
				String.Format("\"{0}@{1}\"", RemoteUserName, ServerName)
			};

			// Build a list of arguments for Rsync
			BasicRsyncArguments = new List<string>
			{
				"--compress",
				"--verbose",
				String.Format("--rsh=\"{0} -p {1}\"", RsyncAuthentication, SSHServerPort),
				"--chmod=ugo=rwx"
			};

			// Build a list of arguments for Rsync filters
			CommonRsyncArguments = new List<string>(BasicRsyncArguments)
			{
				"--recursive",
				"--delete",          // Delete anything not in the source directory
				"--delete-excluded", // Delete anything not in the source directory
				"--times",           // Preserve modification times
				"--omit-dir-times",  // Ignore modification times for directories
				"--prune-empty-dirs" // Remove empty directories from the file list
			};

			// Get the remote base directory
			if (ExecuteAndCaptureOutput("'echo ~'", out StringBuilder Output) != 0)
			{
				throw new BuildException("Unable to determine home directory for remote user. SSH output:\n{0}", StringUtils.Indent(Output.ToString(), "  "));
			}
			RemoteBaseDir = String.Format("{0}/MyEngine/Builds/{1}", Output.ToString().Trim().TrimEnd('/'), Environment.MachineName);
			Log.TraceInformation("[Remote] Using base directory '{0}'", RemoteBaseDir);

			// Build the list of directory mappings between the local and remote machines
			Mappings = new List<RemoteMapping>
			{
				new RemoteMapping(BuildTool.EngineDirectory, GetRemotePath(BuildTool.EngineDirectory))
			};

			if (ProjectFile != null && 
				!ProjectFile.IsUnderDirectory(BuildTool.EngineDirectory))
			{
				Mappings.Add(new RemoteMapping(ProjectFile.Directory, GetRemotePath(ProjectFile.Directory)));
			}
			if (AdditionalPaths != null && ProjectFile != null)
			{
				foreach (DirectoryReference AdditionalPath in AdditionalPaths)
				{
					if (!AdditionalPath.IsUnderDirectory(BuildTool.EngineDirectory) &&
						!AdditionalPath.IsUnderDirectory(ProjectFile.Directory))
					{
						Mappings.Add(new RemoteMapping(AdditionalPath, GetRemotePath(AdditionalPath)));
					}
				}
			}
		}

		// Attempts to get the SSH private key from the standard locations
		private bool TryGetSshPrivateKey(out FileReference OutPrivateKey)
		{
			// Build a list of all the places to look for a private key
			List<DirectoryReference> Locations = new List<DirectoryReference>
			{
				DirectoryReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ApplicationData), "ngine", "BuildTool"),
				DirectoryReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.Personal), "Engine", "BuildTool")
			};

			if (ProjectFile != null)
			{
				Locations.Add(DirectoryReference.Combine(ProjectFile.Directory, "Build", "NotForLicensees"));
				Locations.Add(DirectoryReference.Combine(ProjectFile.Directory, "Build", "NoRedist"));
				Locations.Add(DirectoryReference.Combine(ProjectFile.Directory, "Build"));
			}

			Locations.Add(DirectoryReference.Combine(BuildTool.EngineDirectory, "Build", "NotForLicensees"));
			Locations.Add(DirectoryReference.Combine(BuildTool.EngineDirectory, "Build", "NoRedist"));
			Locations.Add(DirectoryReference.Combine(BuildTool.EngineDirectory, "Build"));

			// Find the first that exists
			foreach (DirectoryReference Location in Locations)
			{
				FileReference KeyFile = FileReference.Combine(Location, "SSHKeys", ServerName, RemoteUserName, "RemoteToolChainPrivate.key");
				if (FileReference.Exists(KeyFile))
				{
					// MacOS Mojave includes a new version of SSH that generates keys that are incompatible with our version of SSH. Make sure the detected keys have the right signature.
					string Text = FileReference.ReadAllText(KeyFile);
					if(Text.Contains("---BEGIN RSA PRIVATE KEY---"))
					{
						OutPrivateKey = KeyFile;
						return true;
					}
				}
			}

			// Nothing found
			OutPrivateKey = null;
			return false;
		}

		// Expand all the variables in the given string
		private string ExpandVariables(string Input)
		{
			string Result = Input;
			Result = Result.Replace("${SSH_PRIVATE_KEY}", SSHPrivateKey.FullName);
			Result = Result.Replace("${CYGWIN_SSH_PRIVATE_KEY}", GetLocalCygwinPath(SSHPrivateKey));
			return Result;
		}

		// Flush the remote machine, removing all existing files
		public void FlushRemote()
		{
			Log.TraceInformation("[Remote] Deleting all files under {0}...", RemoteBaseDir);
			Execute("/", String.Format("rm -rf \"{0}\"", RemoteBaseDir));
		}

		// HandlesTargetPlatform -> CanRemoteExecutorSupports
		// Returns true if the remote executor supports this target platform
		public static bool CanRemoteExecutorSupportsPlatform(BuildTargetPlatform Platform)
		{
			return BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 &&
				(Platform == BuildTargetPlatform.Mac || Platform == BuildTargetPlatform.IOS || Platform == BuildTargetPlatform.TVOS);
		}

		// Clean a target remotely
		public bool Clean(BuildTargetDescriptor TargetDesc)
		{
			// Translate all the arguments for the remote
			List<string> RemoteArguments = GetRemoteArgumentsForTarget(TargetDesc, null);
			RemoteArguments.Add("-Clean");
		
			// Upload the workspace
			DirectoryReference TempDir = CreateTempDirectory(TargetDesc);
			UploadWorkspace(TempDir);

			// Execute the compile
			Log.TraceInformation("[Remote] Executing clean...");

			StringBuilder BuildCommandLine = new StringBuilder("Engine/Build/BatchFiles/Mac/Build.sh");
			foreach(string RemoteArgument in RemoteArguments)
			{
				BuildCommandLine.AppendFormat(" {0}", EscapeShellArgument(RemoteArgument));
			}

			int ExecuteResult = Execute(GetRemotePath(BuildTool.RootDirectory), BuildCommandLine.ToString());
			return ExecuteResult == 0;
		}

        // Build a target remotely
        public bool Build(BuildTargetDescriptor InTargetDescriptor, FileReference RemoteLogFile)
        {
			/*
			// Get the directory for working files
			DirectoryReference TempDir = CreateTempDirectory(InTargetDescriptor);

			// Map the path containing the remote log file
			bool bLogIsMapped = false;
			foreach (RemoteMapping Mapping in Mappings)
			{
				if (RemoteLogFile.Directory.FullName.Equals(Mapping.LocalDirectory.FullName, StringComparison.InvariantCultureIgnoreCase))
				{
					bLogIsMapped = true;
					break;
				}
			}
			if (!bLogIsMapped)
			{
				Mappings.Add(new RemoteMapping(RemoteLogFile.Directory, GetRemotePath(RemoteLogFile.Directory)));
			}

			// Compile the rules assembly
			RulesAssembly RulesAssembly = RulesCompiler.CreateTargetRulesAssembly(InTargetDescriptor.ProjectFile, InTargetDescriptor.Name, false, false, InTargetDescriptor.ForeignPlugin);

			// Create the target rules
			TargetRules Rules = RulesAssembly.CreateTargetRules(InTargetDescriptor.Name, InTargetDescriptor.Platform, InTargetDescriptor.Configuration, InTargetDescriptor.Architecture, InTargetDescriptor.ProjectFile, InTargetDescriptor.AdditionalArguments);

			// Check if we need to enable a nativized plugin, and compile the assembly for that if we do
			FileReference NativizedPluginFile = Rules.GetNativizedPlugin();
			if(NativizedPluginFile != null)
			{
				RulesAssembly = RulesCompiler.CreatePluginRulesAssembly(NativizedPluginFile, RulesAssembly, false, false);
			}

			// Path to the local manifest file. This has to be translated from the remote format after the build is complete.
			List<FileReference> LocalManifestFiles = new List<FileReference>();

			// Path to the remote manifest file
			FileReference RemoteManifestFile = FileReference.Combine(TempDir, "Manifest.xml");

			// Prepare the arguments we will pass to the remote build
			List<string> RemoteArguments = GetRemoteArgumentsForTarget(InTargetDescriptor, LocalManifestFiles);
			RemoteArguments.Add(String.Format("-Log={0}", GetRemotePath(RemoteLogFile)));
			RemoteArguments.Add(String.Format("-Manifest={0}", GetRemotePath(RemoteManifestFile)));

			// Handle any per-platform setup that is required
			if(InTargetDescriptor.Platform == BuildTargetPlatform.IOS || InTargetDescriptor.Platform == BuildTargetPlatform.TVOS)
			{
				// Always generate a .stub
				RemoteArguments.Add("-CreateStub");

				// Cannot use makefiles, since we need PostBuildSync() to generate the IPA (and that requires a TargetRules instance)
				RemoteArguments.Add("-NoUBTMakefiles");

				
				// Get the provisioning data for this project
				IOSProvisioningData ProvisioningData = ((IOSPlatform)BuildPlatform.GetBuildPlatform(InTargetDescriptor.Platform)).ReadProvisioningData(InTargetDescriptor.ProjectFile, InTargetDescriptor.AdditionalArguments.HasOption("-distribution"), IniBundleIdentifier);
				if(ProvisioningData == null || ProvisioningData.MobileProvisionFile == null)
				{
					throw new BuildException("Unable to find mobile provision for {0}. See log for more information.", InTargetDescriptor.Name);
				}
				
				// Create a local copy of the provision
				FileReference MobileProvisionFile = FileReference.Combine(TempDir, ProvisioningData.MobileProvisionFile.GetFileName());
				if(FileReference.Exists(MobileProvisionFile))
				{
					FileReference.SetAttributes(MobileProvisionFile, FileAttributes.Normal);
				}
				FileReference.Copy(ProvisioningData.MobileProvisionFile, MobileProvisionFile, true);
				Log.TraceInformation("[Remote] Uploading {0}", MobileProvisionFile);
				UploadFile(MobileProvisionFile);

				// Extract the certificate for the project. Try to avoid calling IPP if we already have it.
				FileReference CertificateFile = FileReference.Combine(TempDir, "Certificate.p12");

				FileReference CertificateInfoFile = FileReference.Combine(TempDir, "Certificate.txt");
				string CertificateInfoContents = String.Format("{0}\n{1}", ProvisioningData.MobileProvisionFile, FileReference.GetLastWriteTimeUtc(ProvisioningData.MobileProvisionFile).Ticks);

				if(!FileReference.Exists(CertificateFile)     || 
				   !FileReference.Exists(CertificateInfoFile) || 
					FileReference.ReadAllText(CertificateInfoFile) != CertificateInfoContents)
				{
					Log.TraceInformation("[Remote] Exporting certificate for {0}...", ProvisioningData.MobileProvisionFile);

					StringBuilder Arguments = new StringBuilder("ExportCertificate");
					if(InTargetDescriptor.ProjectFile == null)
					{
						Arguments.AppendFormat(" \"{0}\"", BuildTool.EngineSourceDirectory);
					}
					else
					{
						Arguments.AppendFormat(" \"{0}\"", InTargetDescriptor.ProjectFile.Directory);
					}
					Arguments.AppendFormat(" -provisionfile \"{0}\"", ProvisioningData.MobileProvisionFile);
					Arguments.AppendFormat(" -outputcertificate \"{0}\"", CertificateFile);
					if(InTargetDescriptor.Platform == BuildTargetPlatform.TVOS)
					{
						Arguments.Append(" -tvos");
					}

					ProcessStartInfo StartInfo = new ProcessStartInfo
					{
						FileName = FileReference.Combine(BuildTool.EngineDirectory, "Binaries", "DotNET", "IOS", "IPhonePackager.exe").FullName,
						Arguments = Arguments.ToString()
					};

					if (StringUtils.RunLocalProcessAndLogOutput(StartInfo) != 0)
					{
						throw new BuildException("IphonePackager failed.");
					}

					FileReference.WriteAllText(CertificateInfoFile, CertificateInfoContents);
				}

				// Upload the certificate to the remote
				Log.TraceInformation("[Remote] Uploading {0}", CertificateFile);
				UploadFile(CertificateFile);

				// Tell the remote UBT instance to use them
				RemoteArguments.Add(String.Format("-ImportProvision={0}", GetRemotePath(MobileProvisionFile)));
				RemoteArguments.Add(String.Format("-ImportCertificate={0}", GetRemotePath(CertificateFile)));
				RemoteArguments.Add(String.Format("-ImportCertificatePassword=A"));
			}

			// Upload the workspace files
			UploadWorkspace(TempDir);

			// Execute the compile
			Log.TraceInformation("[Remote] Executing build");

			StringBuilder BuildCommandLine = new StringBuilder("Engine/Build/BatchFiles/Mac/Build.sh");
			foreach(string RemoteArgument in RemoteArguments)
			{
				BuildCommandLine.AppendFormat(" {0}", EscapeShellArgument(RemoteArgument));
			}

			int Result = Execute(GetRemotePath(BuildTool.RootDirectory), BuildCommandLine.ToString());
			if(Result != 0)
			{
				if(RemoteLogFile != null)
				{
					Log.TraceInformation("[Remote] Downloading {0}", RemoteLogFile);
					DownloadFile(RemoteLogFile);
				}
				return false;
			}

			// Download the manifest
			Log.TraceInformation("[Remote] Downloading {0}", RemoteManifestFile);
			DownloadFile(RemoteManifestFile);

			// Convert the manifest to local form
			BuildManifest Manifest = StringUtils.ReadClass<BuildManifest>(RemoteManifestFile.FullName);
			for(int Idx = 0; Idx < Manifest.BuildProducts.Count; Idx++)
			{
				Manifest.BuildProducts[Idx] = GetLocalPath(Manifest.BuildProducts[Idx]).FullName;
			}

			// Download the files from the remote
			Log.TraceInformation("[Remote] Downloading build products");

			List<FileReference> FilesToDownload = new List<FileReference> { RemoteLogFile };
			FilesToDownload.AddRange(Manifest.BuildProducts.Select(x => new FileReference(x)));
			DownloadFiles(FilesToDownload);

			// Copy remote FrameworkAssets directory as it could contain resource bundles that must be packaged locally.
			DirectoryReference BaseDir = DirectoryReference.FromFile(InTargetDescriptor.ProjectFile) ?? BuildTool.EngineDirectory;
			DirectoryReference FrameworkAssetsDir = DirectoryReference.Combine(BaseDir, "Intermediate", InTargetDescriptor.Platform == BuildTargetPlatform.IOS ? "IOS" : "TVOS", "FrameworkAssets");
			if(RemoteDirectoryExists(FrameworkAssetsDir))
			{
				Log.TraceInformation("[Remote] Downloading {0}", FrameworkAssetsDir);
				DownloadDirectory(FrameworkAssetsDir);
			}

			// Write out all the local manifests
			foreach(FileReference LocalManifestFile in LocalManifestFiles)
			{
				Log.TraceInformation("[Remote] Writing {0}", LocalManifestFile);
				StringUtils.WriteClass<BuildManifest>(Manifest, LocalManifestFile.FullName, "");
			}
			*/
			return true;
		}

		// Creates a temporary directory for the given target
		static DirectoryReference CreateTempDirectory(BuildTargetDescriptor InTargetDescriptor)
		{
			DirectoryReference BaseDir = DirectoryReference.FromFile(InTargetDescriptor.ProjectFile) ?? BuildTool.EngineDirectory;
			DirectoryReference TempDir = DirectoryReference.Combine(BaseDir, "Intermediate", "Remote", InTargetDescriptor.Name, InTargetDescriptor.Platform.ToString(), InTargetDescriptor.Configuration.ToString());
			DirectoryReference.CreateDirectory(TempDir);
			return TempDir;
		}

		// Translate the arguments for a target descriptor for the remote machine
		List<string> GetRemoteArgumentsForTarget(BuildTargetDescriptor InTargetDescriptor, List<FileReference> LocalManifestFiles)
		{
			List<string> RemoteArguments = new List<string>
			{
				InTargetDescriptor.Name,
				InTargetDescriptor.Platform.ToString(),
				InTargetDescriptor.Configuration.ToString(),
				"-SkipRulesCompile", // Use the rules assembly built locally
				// Use the XML config cache built locally, since the remote won't have it
				String.Format("-XmlConfigCache={0}", GetRemotePath(XMLConfig.CacheFile)) 
			};

			string RemoteIniPath = BuildTool.GetRemoteIniPath();
			if(!String.IsNullOrEmpty(RemoteIniPath))
			{
				RemoteArguments.Add(String.Format("-remoteini={0}", GetRemotePath(RemoteIniPath)));
			}

			if (InTargetDescriptor.ProjectFile != null)
			{
				RemoteArguments.Add(String.Format("-Project={0}", GetRemotePath(InTargetDescriptor.ProjectFile)));
			}

			foreach (string LocalArgument in InTargetDescriptor.AdditionalArguments)
			{
				int EqualsIdx = LocalArgument.IndexOf('=');
				if(EqualsIdx == -1)
				{
					RemoteArguments.Add(LocalArgument);
					continue;
				}

				string Key = LocalArgument.Substring(0, EqualsIdx);
				string Value = LocalArgument.Substring(EqualsIdx + 1);

				if(Key.Equals("-Log", StringComparison.InvariantCultureIgnoreCase))
				{
					// We are already writing to the local log file. The remote will produce a different log (RemoteLogFile)
					continue;
				}
				if(Key.Equals("-Manifest", StringComparison.InvariantCultureIgnoreCase) && LocalManifestFiles != null)
				{
					LocalManifestFiles.Add(new FileReference(Value));
					continue;
				}

				string RemoteArgument = LocalArgument;
				foreach(RemoteMapping Mapping in Mappings)
				{
					if(Value.StartsWith(Mapping.LocalDirectory.FullName, StringComparison.InvariantCultureIgnoreCase))
					{
						RemoteArgument = String.Format("{0}={1}", Key, GetRemotePath(Value));
						break;
					}
				}
				RemoteArguments.Add(RemoteArgument);
			}
			return RemoteArguments;
		}

		private static string GetAppBundleName(FileReference Executable)
		{
			// Get the app bundle name
			string AppBundleName = Executable.GetFileNameWithoutExtension();

			// Strip off any platform suffix
			int SuffixIdx = AppBundleName.IndexOf('-');
			if (SuffixIdx != -1)
			{
				AppBundleName = AppBundleName.Substring(0, SuffixIdx);
			}

			// Append the .app suffix
			return AppBundleName + ".app";
		}

		public static FileReference GetAssetCatalogFile(BuildTargetPlatform Platform, FileReference Executable)
		{
			// Get the output file
			if (Platform == BuildTargetPlatform.IOS)
			{
				return FileReference.Combine(Executable.Directory, "Payload", GetAppBundleName(Executable), "Assets.car");
			}
			else
			{
				return FileReference.Combine(Executable.Directory, "AssetCatalog", "Assets.car");
			}
		}

		public static string GetAssetCatalogArgs(BuildTargetPlatform Platform, string InputDir, string OutputDir)
		{
			StringBuilder Arguments = new StringBuilder("actool");
			Arguments.Append(" --output-format human-readable-text");
			Arguments.Append(" --notices");
			Arguments.Append(" --warnings");
			Arguments.AppendFormat(" --output-partial-info-plist '{0}/assetcatalog_generated_info.plist'", InputDir);
			if (Platform == BuildTargetPlatform.TVOS)
			{
				Arguments.Append(" --app-icon 'App Icon & Top Shelf Image'");
				Arguments.Append(" --launch-image 'Launch Image'");
				Arguments.Append(" --filter-for-device-model AppleTV5,3");
				//Arguments.Append(" --filter-for-device-os-version 10.0");
				Arguments.Append(" --target-device tv");
				Arguments.Append(" --minimum-deployment-target 10.0");
				Arguments.Append(" --platform appletvos");
			}
			else
			{
				Arguments.Append(" --app-icon AppIcon");
				Arguments.Append(" --product-type com.apple.product-type.application");
				Arguments.Append(" --target-device iphone");
				Arguments.Append(" --target-device ipad");
				Arguments.Append(" --minimum-deployment-target 10.0");
				Arguments.Append(" --platform iphoneos");
			}
			Arguments.Append(" --enable-on-demand-resources YES");
			Arguments.AppendFormat(" --compile '{0}'", OutputDir);
			Arguments.AppendFormat(" '{0}/Assets.xcassets'", InputDir);
			return Arguments.ToString();
		}

		// Runs the actool utility on a directory to create an Assets.car file

		// <param name="Platform">The target platform</param>
		// <param name="InputDir">Input directory containing assets</param>
		// <param name="OutputFile">Path to the Assets.car file to produce</param>
		public void RunAssetCatalogTool(BuildTargetPlatform Platform, DirectoryReference InputDir, FileReference OutputFile)
		{
			Log.TraceInformation("Running asset catalog tool for {0}: {1} -> {2}", Platform, InputDir, OutputFile);

			string RemoteInputDir = GetRemotePath(InputDir);
			UploadDirectory(InputDir);

			string RemoteOutputFile = GetRemotePath(OutputFile);
			Execute(RemoteBaseDir, String.Format("rm -f {0}", EscapeShellArgument(RemoteOutputFile)));

			string RemoteOutputDir = Path.GetDirectoryName(RemoteOutputFile).Replace(Path.DirectorySeparatorChar, '/');
			Execute(RemoteBaseDir, String.Format("mkdir -p {0}", EscapeShellArgument(RemoteOutputDir)));

			string RemoteArguments = GetAssetCatalogArgs(Platform, RemoteInputDir, RemoteOutputDir); 
			if(Execute(RemoteBaseDir, String.Format("/usr/bin/xcrun {0}", RemoteArguments)) != 0)
			{
				throw new BuildException("Failed to run actool.");
			}
			DownloadFile(OutputFile);
		}

        // Convers a remote path into local form
        private FileReference GetLocalPath(string RemotePath)
        {
			foreach(RemoteMapping Mapping in Mappings)
			{
				if(RemotePath.StartsWith(Mapping.RemoteDirectory, StringComparison.InvariantCultureIgnoreCase) && RemotePath.Length > Mapping.RemoteDirectory.Length && RemotePath[Mapping.RemoteDirectory.Length] == '/')
				{
					return FileReference.Combine(Mapping.LocalDirectory, RemotePath.Substring(Mapping.RemoteDirectory.Length + 1));
				}
			}
			throw new BuildException("Unable to map remote path '{0}' to local path", RemotePath);
		}

		// Converts a local path into a remote one
		private string GetRemotePath(FileSystemReference LocalPathToConvert)
		{
			return GetRemotePath(LocalPathToConvert.FullName);
		}

		// Converts a local path into a remote one
		private string GetRemotePath(string LocalPathToConvert)
		{
			return String.Format("{0}/{1}", RemoteBaseDir, LocalPathToConvert.Replace(":", "").Replace("\\", "/").Replace(" ", "_"));
		}

		// Gets the local path in Cygwin format (eg. /cygdrive/C/...)
		private static string GetLocalCygwinPath(FileSystemReference InPath)
		{
			if(InPath.FullName.Length < 2 || 
			   InPath.FullName[1] != ':')
			{
				throw new BuildException("Invalid local path for converting to cygwin format ({0}).", InPath);
			}
			return String.Format("/cygdrive/{0}{1}", InPath.FullName.Substring(0, 1), InPath.FullName.Substring(2).Replace('\\', '/'));
		}

		// Escapes spaces in a shell command argument
		private static string EscapeShellArgument(string Argument)
		{
			return Argument.Replace(" ", "\\ ");
		}

		// Upload a single file to the remote
		void UploadFile(FileReference LocalFile)
		{
			string RemoteFile = GetRemotePath(LocalFile);
			string RemoteDirectory = GetRemotePath(LocalFile.Directory);

			List<string> Arguments = new List<string>(CommonRsyncArguments)
			{
				String.Format("--rsync-path=\"mkdir -p {0} && rsync\"", RemoteDirectory),
				String.Format("\"{0}\"", GetLocalCygwinPath(LocalFile)),
				String.Format("\"{0}@{1}\":'{2}'", RemoteUserName, ServerName, RemoteFile),
				"-q" // quiet suppress message
			};

			int Result = Rsync(String.Join(" ", Arguments));
			if(Result != 0)
			{
				throw new BuildException("Error while running Rsync (exit code {0})", Result);
			}
		}

		// Upload a single file to the remote
		void UploadFiles(DirectoryReference LocalDirectory, string RemoteDirectory, FileReference LocalFileList)
		{
			List<string> Arguments = new List<string>(BasicRsyncArguments)
			{
				String.Format("--rsync-path=\"mkdir -p {0} && rsync\"", RemoteDirectory),
				String.Format("--files-from=\"{0}\"", GetLocalCygwinPath(LocalFileList)),
				String.Format("\"{0}/\"", GetLocalCygwinPath(LocalDirectory)),
				String.Format("\"{0}@{1}\":'{2}/'", RemoteUserName, ServerName, RemoteDirectory),
				"-q" // quiet suppress message
			};

			int Result = Rsync(String.Join(" ", Arguments));
			if(Result != 0)
			{
				throw new BuildException("Error while running Rsync (exit code {0})", Result);
			}
		}
		
		// Upload a single directory to the remote
		void UploadDirectory(DirectoryReference LocalDirectory)
		{
			string RemoteDirectory = GetRemotePath(LocalDirectory);

			List<string> Arguments = new List<string>(CommonRsyncArguments)
			{
				String.Format("--rsync-path=\"mkdir -p {0} && rsync\"", RemoteDirectory),
				String.Format("\"{0}/\"", GetLocalCygwinPath(LocalDirectory)),
				String.Format("\"{0}@{1}\":'{2}/'", RemoteUserName, ServerName, RemoteDirectory),
				"-q" // quiet suppress message
			};

			int Result = Rsync(String.Join(" ", Arguments));
			if(Result != 0)
			{
				throw new BuildException("Error while running Rsync (exit code {0})", Result);
			}
		}

		// Uploads a directory to the remote using a specific filter list
		void UploadDirectory(DirectoryReference LocalDirectory, string RemoteDirectory, List<FileReference> FilterLocations)
		{
			List<string> Arguments = new List<string>(CommonRsyncArguments)
			{
				String.Format("--rsync-path=\"mkdir -p {0} && rsync\"", RemoteDirectory)
			};

			foreach (FileReference FilterLocation in FilterLocations)
			{
				Arguments.Add(String.Format("--filter=\"merge {0}\"", GetLocalCygwinPath(FilterLocation)));
			}

			Arguments.Add("--exclude='*'");
			Arguments.Add(String.Format("\"{0}/\"", GetLocalCygwinPath(LocalDirectory)));
			Arguments.Add(String.Format("\"{0}@{1}\":'{2}/'", RemoteUserName, ServerName, RemoteDirectory));

			int RsyncResult = Rsync(String.Join(" ", Arguments));
			if(RsyncResult != 0)
			{
				throw new BuildException("Error while running Rsync (exit code {0})", RsyncResult);
			}
		}

		// Upload all the files in the workspace for the current project
		void UploadWorkspace(DirectoryReference TempDir)
		{
			// Path to the scripts to be uploaded
			FileReference ScriptPathsFileName = FileReference.Combine(BuildTool.EngineDirectory, "Build", "Rsync", "RsyncEngineScripts.txt");

			// Read the list of scripts to be uploaded
			List<string> ScriptPaths = new List<string>();
			foreach(string Line in FileReference.ReadAllLines(ScriptPathsFileName))
			{
				string FileToUpload = Line.Trim();
				if(FileToUpload.Length > 0 && FileToUpload[0] != '#')
				{
					ScriptPaths.Add(FileToUpload);
				}
			}

			// Fixup the line endings
			List<FileReference> TargetFiles = new List<FileReference>();
			foreach(string ScriptPath in ScriptPaths)
			{
				FileReference SourceFile = FileReference.Combine(BuildTool.EngineDirectory, ScriptPath.TrimStart('/'));
				if(!FileReference.Exists(SourceFile))
				{
					throw new BuildException("Missing script required for remote upload: {0}", SourceFile);
				}

				FileReference TargetFile = FileReference.Combine(TempDir, SourceFile.MakeRelativeTo(BuildTool.EngineDirectory));
				if(!FileReference.Exists(TargetFile) || FileReference.GetLastWriteTimeUtc(TargetFile) < FileReference.GetLastWriteTimeUtc(SourceFile))
				{
					DirectoryReference.CreateDirectory(TargetFile.Directory);
					string ScriptText = FileReference.ReadAllText(SourceFile);
					FileReference.WriteAllText(TargetFile, ScriptText.Replace("\r\n", "\n"));
				}
				TargetFiles.Add(TargetFile);
			}

			// Write a file that protects all the scripts from being overridden by the standard engine filters
			FileReference ScriptProtectList = FileReference.Combine(TempDir, "RsyncEngineScripts-Protect.txt");
			using(StreamWriter Writer = new StreamWriter(ScriptProtectList.FullName))
			{
				foreach(string ScriptPath in ScriptPaths)
				{
					Writer.WriteLine("protect {0}", ScriptPath);
				}
			}

			// Upload these files to the remote
			Log.TraceInformation("[Remote] Uploading scripts...");
			UploadFiles(TempDir, GetRemotePath(BuildTool.EngineDirectory), ScriptPathsFileName);

			// Upload the config files
			Log.TraceInformation("[Remote] Uploading config files...");
			UploadFile(XMLConfig.CacheFile);

			// Upload the engine files
			List<FileReference> EngineFilters = new List<FileReference> { ScriptProtectList };

			if (BuildTool.IsEngineInstalled())
			{
				EngineFilters.Add(FileReference.Combine(BuildTool.EngineDirectory, "Build", "Rsync", "RsyncEngineInstalled.txt"));
			}
			EngineFilters.Add(FileReference.Combine(BuildTool.EngineDirectory, "Build", "Rsync", "RsyncEngine.txt"));

			Log.TraceInformation("[Remote] Uploading engine files...");
			UploadDirectory(BuildTool.EngineDirectory, GetRemotePath(BuildTool.EngineDirectory), EngineFilters);

			// Upload the project files
			DirectoryReference ProjectDir = null;
			if (ProjectFile != null && !ProjectFile.IsUnderDirectory(BuildTool.EngineDirectory))
			{
				ProjectDir = ProjectFile.Directory;
			}
			else if (!string.IsNullOrEmpty(BuildTool.GetRemoteIniPath()))
			{
				ProjectDir = new DirectoryReference(BuildTool.GetRemoteIniPath());
				if (ProjectDir.IsUnderDirectory(BuildTool.EngineDirectory))
				{
					ProjectDir = null;
				}
			}
			if (ProjectDir != null)
			{
				List<FileReference> ProjectFilters = new List<FileReference>();

				FileReference CustomFilter = FileReference.Combine(ProjectDir, "Build", "Rsync", "RsyncProject.txt");
				if (FileReference.Exists(CustomFilter))
				{
					ProjectFilters.Add(CustomFilter);
				}
				ProjectFilters.Add(FileReference.Combine(BuildTool.EngineDirectory, "Build", "Rsync", "RsyncProject.txt"));

				Log.TraceInformation("[Remote] Uploading project files...");
				UploadDirectory(ProjectDir, GetRemotePath(ProjectDir), ProjectFilters);
			}

			if (AdditionalPaths != null)
			{
				foreach (DirectoryReference AdditionalPath in AdditionalPaths)
				{
					List<FileReference> CustomFilters = new List<FileReference>();

					FileReference CustomFilter = FileReference.Combine(AdditionalPath, "Build", "Rsync", "RsyncProject.txt");
					if (FileReference.Exists(CustomFilter))
					{
						CustomFilters.Add(CustomFilter);
					}
					CustomFilters.Add(FileReference.Combine(BuildTool.EngineDirectory, "Build", "Rsync", "RsyncProject.txt"));

					Log.TraceInformation(string.Format("[Remote] Uploading additional path files [{0}]...", AdditionalPath.FullName));
					UploadDirectory(AdditionalPath, GetRemotePath(AdditionalPath), CustomFilters);
				}
			}

			Execute("/", String.Format("rm -rf {0}/Intermediate/IOS/*.plist", GetRemotePath(BuildTool.EngineDirectory)));
			Execute("/", String.Format("rm -rf {0}/Intermediate/TVOS/*.plist", GetRemotePath(BuildTool.EngineDirectory)));

			if (ProjectFile != null)
			{
				Execute("/", String.Format("rm -rf {0}/Intermediate/IOS/*.plist", GetRemotePath(ProjectFile.Directory)));
				Execute("/", String.Format("rm -rf {0}/Intermediate/TVOS/*.plist", GetRemotePath(ProjectFile.Directory)));
			}

			// Fixup permissions on any shell scripts
			Execute(RemoteBaseDir, String.Format("chmod +x {0}/Build/BatchFiles/Mac/*.sh", EscapeShellArgument(GetRemotePath(BuildTool.EngineDirectory))));
		}

		// Downloads a single file from the remote
		void DownloadFile(FileReference LocalFile)
		{
			RemoteMapping Mapping = Mappings.FirstOrDefault(x => LocalFile.IsUnderDirectory(x.LocalDirectory));
			if(Mapping == null)
			{
				throw new BuildException("File for download '{0}' is not under any mapped directory.", LocalFile);
			}

			List<string> Arguments = new List<string>(CommonRsyncArguments)
			{
				String.Format("\"{0}@{1}\":'{2}/{3}'", RemoteUserName, ServerName, Mapping.RemoteDirectory, LocalFile.MakeRelativeTo(Mapping.LocalDirectory).Replace('\\', '/')),
				String.Format("\"{0}/\"", GetLocalCygwinPath(LocalFile.Directory)),
				"-q"
			};

			int Result = Rsync(String.Join(" ", Arguments));
			if(Result != 0)
			{
				throw new BuildException("Unable to download '{0}' from the remote Mac (exit code {1}).", LocalFile, Result);
			}
		}

		// Download multiple files from the remote Mac
		void DownloadFiles(IEnumerable<FileReference> Files)
		{
			List<FileReference>[] FileGroups = new List<FileReference>[Mappings.Count];
			for(int Idx = 0; Idx < Mappings.Count; ++Idx)
			{
				FileGroups[Idx] = new List<FileReference>();
			}
			foreach(FileReference File in Files)
			{
				int MappingIdx = Mappings.FindIndex(x => File.IsUnderDirectory(x.LocalDirectory));
				if(MappingIdx == -1)
				{
					throw new BuildException("File for download '{0}' is not under the engine or project directory.", File);
				}
				FileGroups[MappingIdx].Add(File);
			}
			for(int Idx = 0; Idx < Mappings.Count; Idx++)
			{
				if(FileGroups[Idx].Count > 0)
				{
					FileReference DownloadListLocation = FileReference.Combine(BuildTool.EngineDirectory, "Intermediate", "Rsync", "Download.txt");
					DirectoryReference.CreateDirectory(DownloadListLocation.Directory);
					FileReference.WriteAllLines(DownloadListLocation, FileGroups[Idx].Select(x => x.MakeRelativeTo(Mappings[Idx].LocalDirectory).Replace('\\', '/')));

					List<string> Arguments = new List<string>(CommonRsyncArguments)
					{
						String.Format("--files-from=\"{0}\"", GetLocalCygwinPath(DownloadListLocation)),
						String.Format("\"{0}@{1}\":'{2}/'", RemoteUserName, ServerName, Mappings[Idx].RemoteDirectory),
						String.Format("\"{0}/\"", GetLocalCygwinPath(Mappings[Idx].LocalDirectory))
					};

					int Result = Rsync(String.Join(" ", Arguments));
					if(Result != 0)
					{
						throw new BuildException("Unable to download files from remote Mac (exit code {0})", Result);
					}
				}
			}
		}

		// Checks whether a directory exists on the remote machine
		private bool RemoteDirectoryExists(DirectoryReference LocalDirectory)
		{
			string RemoteDirectory = GetRemotePath(LocalDirectory);
			int ExitCode = Execute(BuildTool.RootDirectory, String.Format("[ -d {0} ]", EscapeShellArgument(RemoteDirectory)));
			bool bSuccess = ExitCode == 0;
			return bSuccess;
		}

		// Download a directory from the remote Mac
		// <param name="LocalDirectory">Directory to download</param>
		private void DownloadDirectory(DirectoryReference LocalDirectory)
		{
			DirectoryReference.CreateDirectory(LocalDirectory);

			string RemoteDirectory = GetRemotePath(LocalDirectory);

			List<string> Arguments = new List<string>(CommonRsyncArguments)
			{
				String.Format("\"{0}@{1}\":'{2}/'", RemoteUserName, ServerName, RemoteDirectory),
				String.Format("\"{0}/\"", GetLocalCygwinPath(LocalDirectory))
			};

			int Result = Rsync(String.Join(" ", Arguments));
			if (Result != 0)
			{
				throw new BuildException("Unable to download '{0}' from the remote Mac (exit code {1}).", LocalDirectory, Result);
			}
		}

		// Execute Rsync
		private int Rsync(string Arguments)
		{
			using(Process RsyncProcess = new Process())
			{
				RsyncProcess.StartInfo.FileName         = RsyncEXE.FullName;
				RsyncProcess.StartInfo.WorkingDirectory = SSHEXE.Directory.FullName;
				RsyncProcess.StartInfo.Arguments        = Arguments;
				RsyncProcess.OutputDataReceived        += RsyncOutput;
				RsyncProcess.ErrorDataReceived         += RsyncOutput;

				Log.TraceLog("[Rsync] {0} {1}", StringUtils.MakePathSafeToUseWithCommandLine(RsyncProcess.StartInfo.FileName), RsyncProcess.StartInfo.Arguments);
				int ExitCode = StringUtils.RunLocalProcess(RsyncProcess);
				return ExitCode;
			}
		}

		// Handles data output by rsync
		private void RsyncOutput(object Sender, DataReceivedEventArgs Args)
		{
			if(Args.Data != null)
			{
				Log.TraceInformation("  {0}", Args.Data);
			}
		}

		// Execute a command on the remote in the remote equivalent of a local directory
		public int Execute(DirectoryReference WorkingDir, string Command)
		{
			return Execute(GetRemotePath(WorkingDir), Command);
		}

		// Execute a remote command, capturing the output text
		protected int Execute(string WorkingDirectory, string Command)
		{
			string FullCommand = String.Format("cd {0} && {1}", EscapeShellArgument(WorkingDirectory), Command);
			using(Process SSHProcess = new Process())
			{
				SSHProcess.StartInfo.FileName         = SSHEXE.FullName;
				SSHProcess.StartInfo.WorkingDirectory = SSHEXE.Directory.FullName;
				SSHProcess.StartInfo.Arguments        = String.Format("{0} {1}", String.Join(" ", CommonSSHArguments), FullCommand.Replace("\"", "\\\""));
				SSHProcess.OutputDataReceived        += (DataReceivedEventHandler)((E, Args) => SshOutput(Args));
				SSHProcess.ErrorDataReceived         += (DataReceivedEventHandler)((E, Args) => SshOutput(Args));

				Log.TraceLog("[SSH] {0} {1}", StringUtils.MakePathSafeToUseWithCommandLine(SSHProcess.StartInfo.FileName), SSHProcess.StartInfo.Arguments);
				return StringUtils.RunLocalProcess(SSHProcess);
			}
		}

		// Handler for output from running remote SSH commands
		private void SshOutput(DataReceivedEventArgs Args)
		{
			if(Args.Data != null)
			{
				string FormattedOutput = ConvertRemotePathsToLocal(Args.Data);
				Log.TraceInformation("  {0}", FormattedOutput);
			}
		}

		// Execute a remote command, capturing the output text
		protected int ExecuteAndCaptureOutput(string Command, out StringBuilder Output)
		{
			StringBuilder FullCommand = new StringBuilder();
			foreach(string CommonSshArgument in CommonSSHArguments)
			{
				FullCommand.AppendFormat("{0} ", CommonSshArgument);
			}
			FullCommand.Append(Command.Replace("\"", "\\\""));

			using(Process SSHProcess = new Process())
			{
				Output = new StringBuilder();

				StringBuilder OutputLocal = Output;

				SSHProcess.StartInfo.FileName         = SSHEXE.FullName;
				SSHProcess.StartInfo.WorkingDirectory = SSHEXE.Directory.FullName;
				SSHProcess.StartInfo.Arguments        = FullCommand.ToString();
				SSHProcess.OutputDataReceived        += (E, Args) => { if (Args.Data != null) { OutputLocal.Append(Args.Data); } };
				SSHProcess.ErrorDataReceived         += (E, Args) => { if (Args.Data != null) { OutputLocal.Append(Args.Data); } };

				Log.TraceLog("[SSH] {0} {1}", StringUtils.MakePathSafeToUseWithCommandLine(SSHProcess.StartInfo.FileName), SSHProcess.StartInfo.Arguments);
				return StringUtils.RunLocalProcess(SSHProcess);
			}
		}

		// Converts any remote paths within the given string to local format
		private string ConvertRemotePathsToLocal(string Text)
		{
			// Try to match any source file with the remote base directory in front of it
			string Pattern = String.Format("(?<![a-zA-Z=]){0}[^:]*\\.(?:cpp|inl|h|hpp|hh|txt)(?![a-zA-Z])", Regex.Escape(RemoteBaseDir));

			// Find the matches, and early out if there are none
			MatchCollection Matches = Regex.Matches(Text, Pattern, RegexOptions.IgnoreCase);
			if(Matches.Count == 0)
			{
				return Text;
			}

			// Replace any remote paths with local ones
			StringBuilder Result = new StringBuilder();
			int StartIdx = 0;
			foreach(Match Match in Matches)
			{
				// Append the text leading up to this path
				Result.Append(Text, StartIdx, Match.Index - StartIdx);

				// Try to convert the path
				string Path = Match.Value;
				foreach(RemoteMapping Mapping in Mappings)
				{
					if(Path.StartsWith(Mapping.RemoteDirectory))
					{
						Path = Mapping.LocalDirectory + Path.Substring(Mapping.RemoteDirectory.Length).Replace('/', '\\');
						break;
					}
				}

				// Append the path to the output string
				Result.Append(Path);

				// Move past this match
				StartIdx = Match.Index + Match.Length;
			}
			Result.Append(Text, StartIdx, Text.Length - StartIdx);
			return Result.ToString();
		}
	}
#pragma warning restore IDE0051 // Remove unused parameter
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore IDE0052 // Remove unread private members
}
