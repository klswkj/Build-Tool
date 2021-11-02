using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	//  Base class to handle deploy of a target for a given platform
	internal sealed class HoloLensDeploy : BuildDeploy
	{
		//private FileReference MakeAppXPath;
		//private FileReference SignToolPath;

		private readonly List<WinMDRegistrationInfo> WinMDReferences = new List<WinMDRegistrationInfo>();

		// Utility function to delete a file
		private static void DeployHelper_DeleteFile(string InFileToDelete)
		{
			Log.TraceInformation("HoloLensDeploy.DeployHelper_DeleteFile({0})", InFileToDelete);
			if (File.Exists(InFileToDelete) == true)
			{
				FileAttributes attributes = File.GetAttributes(InFileToDelete);
				if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
				{
					attributes &= ~FileAttributes.ReadOnly;
					File.SetAttributes(InFileToDelete, attributes);
				}
				File.Delete(InFileToDelete);
			}
		}

		// Copy the contents of the given source directory to the given destination directory
#pragma warning disable IDE0051 // Remove unused private members
		private static bool CopySourceToDestDir
#pragma warning restore IDE0051 // Remove unused private members
		(
			string InSourceDirectory,
			string InDestinationDirectory,
			string InWildCard,
			bool bInIncludeSubDirectories,
			bool bInRemoveDestinationOrphans
		)
		{
			Log.TraceInformation("HoloLensDeploy.CopySourceToDestDir({0}, {1}, {2},...)", InSourceDirectory, InDestinationDirectory, InWildCard);
			if (Directory.Exists(InSourceDirectory) == false)
			{
				Log.TraceInformation("Warning: CopySourceToDestDir - SourceDirectory does not exist: {0}", InSourceDirectory);
				return false;
			}

			// Make sure the destination directory exists!
			Directory.CreateDirectory(InDestinationDirectory);

			SearchOption OptionToSearch = bInIncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

			List<string> SourceDirs = new List<string>(Directory.GetDirectories(InSourceDirectory, "*.*", OptionToSearch));
			foreach (string SourceDir in SourceDirs)
			{
				string SubDir = SourceDir.Replace(InSourceDirectory, "");
				string DestDir = InDestinationDirectory + SubDir;
				Directory.CreateDirectory(DestDir);
			}

			List<string> SourceFiles = new List<string>(Directory.GetFiles(InSourceDirectory, InWildCard, OptionToSearch));
			List<string> DestFiles   = new List<string>(Directory.GetFiles(InDestinationDirectory, InWildCard, OptionToSearch));

			// Keep a list of the files in the source directory... without the source path
			List<string> FilesInSource = new List<string>();

			// Copy all the source files that are newer...
			foreach (string SourceFile in SourceFiles)
			{
				string Filename = SourceFile.Replace(InSourceDirectory, "");
				FilesInSource.Add(Filename.ToUpperInvariant());
				string DestFile = InDestinationDirectory + Filename;

				System.DateTime SourceTime = File.GetLastWriteTime(SourceFile);
				System.DateTime DestTime   = File.GetLastWriteTime(DestFile);

				if (DestTime < SourceTime)
				{
					try
					{
						DeployHelper_DeleteFile(DestFile);
						const bool Overwrite = true;
						File.Copy(SourceFile, DestFile, Overwrite);
					}
					catch (Exception exceptionMessage)
					{
						Log.TraceInformation("Failed to copy {0} to deployment: {1}", SourceFile, exceptionMessage);
					}
				}
			}

			if (bInRemoveDestinationOrphans == true)
			{
				// If requested, delete any destination files that do not have a corresponding
				// file in the source directory
				foreach (string DestFile in DestFiles)
				{
					string DestFilename = DestFile.Replace(InDestinationDirectory, "");
					if (FilesInSource.Contains(DestFilename.ToUpperInvariant()) == false)
					{
						Log.TraceInformation("Destination file does not exist in Source - DELETING: {0}", DestFile);
						//FileAttributes attributes = File.GetAttributes(DestFile);
						try
						{
							DeployHelper_DeleteFile(DestFile);
						}
						catch (Exception exceptionMessage)
						{
							Log.TraceInformation("Failed to delete {0} from deployment: {1}", DestFile, exceptionMessage);
						}
					}
				}
			}

			return true;
		}

		// Helper function for copying files
		private static void CopyFile(string InSource, string InDest, bool bForce)
		{
			if (File.Exists(InSource) == true)
			{
				if (File.Exists(InDest) == true)
				{
					if (File.GetLastWriteTime(InSource).CompareTo(File.GetLastWriteTime(InDest)) == 0)
					{
						//If the source and dest have the file and they have the same write times they are assumed to be equal and we don't need to copy.
						return;
					}
					if (bForce == true)
					{
						DeployHelper_DeleteFile(InDest);
					}
				}
				Log.TraceInformation("HoloLensDeploy.CopyFile({0}, {1}, {2})", InSource, InDest, bForce);
				File.Copy(InSource, InDest, true);
				File.SetAttributes(InDest, File.GetAttributes(InDest) & ~FileAttributes.ReadOnly);
			}
			else
			{
				Log.TraceInformation("HoloLensDeploy: File didn't exist - {0}", InSource);
			}
		}

		// Helper function for copying a tree files
		private static void CopyDirectory(string InSource, string InDest, bool bForce, bool bRecurse)
		{
			if (Directory.Exists(InSource))
			{
				if (!Directory.Exists(InDest))
				{
					Directory.CreateDirectory(InDest);
				}

				// Copy all files
				string[] FilesInDir = Directory.GetFiles(InSource);
				foreach (string FileSourcePath in FilesInDir)
				{
					string FileDestPath = Path.Combine(InDest, Path.GetFileName(FileSourcePath));
					CopyFile(FileSourcePath, FileDestPath, true);
				}

				// Recurse sub directories
				string[] DirsInDir = Directory.GetDirectories(InSource);
				foreach (string DirSourcePath in DirsInDir)
				{
					string DirName = Path.GetFileName(DirSourcePath);
					string DirDestPath = Path.Combine(InDest, DirName);
					CopyDirectory(DirSourcePath, DirDestPath, bForce, bRecurse);
				}
			}
		}

		public static bool PrepareForUATPackageOrDeploy
		(
			string ProjectDirectory,
			List<string> ExecutablePaths
		)
		{
			string AbsoluteExeDirectory = Path.GetDirectoryName(ExecutablePaths[0]);

			// If using a secure networking manifest, copy it to the output directory.
			string NetworkManifest = Path.Combine(ProjectDirectory, "Config", "HoloLens", "NetworkManifest.xml");
			if (File.Exists(NetworkManifest))
			{
				CopyFile(NetworkManifest, Path.Combine(AbsoluteExeDirectory, "NetworkManifest.xml"), false);
			}

			return true;
		}

		private static void MakePackage(TargetReceipt Receipt, TargetReceipt NewReceipt, WindowsArchitecture Architecture, List<string> UpdatedFiles)
		{
			string OutputName = String.Format("{0}_{1}_{2}_{3}", Receipt.TargetName, Receipt.Platform, Receipt.Configuration, WindowsExports.GetArchitectureSubpath(Architecture));
			string IntermediateDirectory = Path.Combine(Receipt.ProjectDir != null ? Receipt.ProjectDir.FullName : BuildTool.EngineDirectory.FullName, "Intermediate", "Deploy", WindowsExports.GetArchitectureSubpath(Architecture));
			string OutputDirectory = Receipt.Launch.Directory.FullName;

			string OutputAppX  = Path.Combine(OutputDirectory, OutputName + ".appx");
			string MapFilename = Path.Combine(IntermediateDirectory, OutputName + ".pkgmap");

			DirectoryReference LocalRoot = Receipt.ProjectDir;
			Dictionary<string, string> AddedFiles = new Dictionary<string, string>();

			{
				foreach (var Product in Receipt.BuildProducts)
				{
					if (Product.Type == BuildProductType.Executable || Product.Type == BuildProductType.DynamicLibrary || Product.Type == BuildProductType.RequiredResource)
					{
						string Filename;
						if(AddedFiles.ContainsKey(Product.Path.FullName))
						{
							continue;
						}

						if (LocalRoot != null && Product.Path.IsUnderDirectory(LocalRoot))
						{
							Filename = Product.Path.MakeRelativeTo(LocalRoot.ParentDirectory);
						}
						else if(Product.Path.IsUnderDirectory(BuildTool.RootDirectory))
						{
							Filename = Product.Path.MakeRelativeTo(BuildTool.RootDirectory);
						}
						else
						{
							throw new BuildException("Failed to parse target receipt file.  See log for details.");
						}

						AddedFiles.Add(Product.Path.FullName, Filename);
					}
				}

				foreach(var Dep in Receipt.RuntimeDependencies)
				{
					if(Dep.Type == StagedFileType.RawFile)
					{
						if(AddedFiles.ContainsKey(Dep.Path.FullName))
						{
							continue;
						}

						string Filename;
						if (LocalRoot != null && Dep.Path.IsUnderDirectory(LocalRoot))
						{
							Filename = Dep.Path.MakeRelativeTo(LocalRoot.ParentDirectory);
						}
						else if (Dep.Path.IsUnderDirectory(BuildTool.RootDirectory))
						{
							Filename = Dep.Path.MakeRelativeTo(BuildTool.RootDirectory);
						}
						else
						{
							throw new BuildException("Failed to parse target receipt file.  See log for details.");
						}

						AddedFiles.Add(Dep.Path.FullName, Filename);
					}
				}
			}

			string ManifestName = String.Format("AppxManifest_{0}.xml", WindowsExports.GetArchitectureSubpath(Architecture));
			AddedFiles.Add(Path.Combine(OutputDirectory, ManifestName), "AppxManifest.xml");

			//manually add resources
			string PriFileName = String.Format("resources_{0}.pri", WindowsExports.GetArchitectureSubpath(Architecture));
			AddedFiles.Add(Path.Combine(OutputDirectory, PriFileName), "resources.pri");
			{
				DirectoryReference ResourceFolder = DirectoryReference.Combine(Receipt.Launch.Directory, WindowsExports.GetArchitectureSubpath(Architecture));
				foreach (var ResourcePath in UpdatedFiles)
				{
					var ResourceFile = new FileReference(ResourcePath);

					if (ResourceFile.IsUnderDirectory(ResourceFolder))
					{
						AddedFiles.Add(ResourceFile.FullName, ResourceFile.MakeRelativeTo(ResourceFolder));
					}
					else
					{
						Log.TraceError("Wrong path to resource \'{0}\', the resource should be in \'{1}\'", ResourceFile.FullName, ResourceFolder.FullName);
						throw new BuildException("Failed to generate AppX file.  See log for details.");
					}
				}
			}


			FileReference SourceNetworkManifestPath = new FileReference(Path.Combine(OutputDirectory, "NetworkManifest.xml"));
			if (FileReference.Exists(SourceNetworkManifestPath))
			{
				AddedFiles.Add(SourceNetworkManifestPath.FullName, "NetworkManifest.xml");
			}
			FileReference SourceXboxConfigPath = new FileReference(Path.Combine(OutputDirectory, "xboxservices.config"));
			if (FileReference.Exists(SourceXboxConfigPath))
			{
				AddedFiles.Add(SourceXboxConfigPath.FullName, "xboxservices.config");
			}

			try
			{
				DeployHelper_DeleteFile(OutputAppX);
			}
			catch (Exception exceptionMessage)
			{
				Log.TraceError("Failed to delete {0} from deployment: {1}", OutputAppX, exceptionMessage);
				System.Diagnostics.Debugger.Break();
				throw new BuildException("Failed to generate AppX file.  See log for details.");
			}

			var AppXRecipeBuiltFiles = new StringBuilder();
			AppXRecipeBuiltFiles.AppendLine(@"[Files]");
			foreach (var f in AddedFiles)
			{
				AppXRecipeBuiltFiles.AppendLine(String.Format("\"{0}\"\t\"{1}\"", f.Key, f.Value));
			}
			File.WriteAllText(MapFilename, AppXRecipeBuiltFiles.ToString(), Encoding.UTF8);

			NewReceipt.BuildProducts.Add(new BuildProduct(new FileReference(MapFilename), BuildProductType.MapFile));
		}

		private static void CopyDataAndSymbolsBetweenReceipts(TargetReceipt Receipt, TargetReceipt NewReceipt)
		{
			NewReceipt.AdditionalProperties.AddRange(Receipt.AdditionalProperties);
			NewReceipt.AdditionalProperties = NewReceipt.AdditionalProperties.GroupBy(e => e.PropertyName).Select(g => g.First()).ToList();

			NewReceipt.BuildProducts.AddRange(Receipt.BuildProducts.FindAll(x => x.Type == BuildProductType.SymbolFile));
			NewReceipt.BuildProducts = NewReceipt.BuildProducts.GroupBy(e => e.Path).Select(g => g.First()).ToList();

			NewReceipt.RuntimeDependencies.AddRange(Receipt.RuntimeDependencies.FindAll(x => x.Type != StagedFileType.RawFile));
			NewReceipt.RuntimeDependencies = new RuntimeDependencyList(NewReceipt.RuntimeDependencies.GroupBy(e => e.Path).Select(g => g.First()).ToList());
		}


		public override bool PrepTargetForDeployment(TargetReceipt Receipt)
		{
			// Use the project name if possible - InTarget.AppName changes for 'Client'/'Server' builds
			string ProjectName = Receipt.ProjectFile != null ? Receipt.ProjectFile.GetFileNameWithoutAnyExtensions() : Receipt.Launch.GetFileNameWithoutExtension();
			Log.TraceInformation("Prepping {0} for deployment to {1}", ProjectName, Receipt.Platform.ToString());
			System.DateTime PrepDeployStartTime = DateTime.UtcNow;

			// Note: TargetReceipt.Read now expands path variables internally.
			FileReference ReceiptFileName = TargetReceipt.GetDefaultPath(Receipt.ProjectDir ?? BuildTool.EngineDirectory, Receipt.TargetName, Receipt.Platform, Receipt.Configuration, "Multi");
			if (!TargetReceipt.TryRead(ReceiptFileName, BuildTool.EngineDirectory, out TargetReceipt NewReceipt))
			{
				NewReceipt = new TargetReceipt(Receipt.ProjectFile, Receipt.TargetName, Receipt.TargetType, Receipt.Platform, Receipt.Configuration, Receipt.Version, "Multi");
			}

			AddWinMDReferencesFromReceipt(Receipt, Receipt.ProjectDir ?? BuildTool.EngineDirectory, BuildTool.EngineDirectory.ParentDirectory.FullName);

			//PrepForUATPackageOrDeploy(InTarget.ProjectFile, InAppName, InTarget.ProjectDirectory.FullName, InTarget.OutputPath.FullName, TargetBuildEnvironment.RelativeEnginePath, false, "", false);
			List<TargetConfiguration> TargetConfigs = new List<TargetConfiguration> { Receipt.Configuration };

            // string RelativeEnginePath = BuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory());

            WindowsArchitecture Arch = WindowsArchitecture.ARM64;
			if (Receipt.Architecture.ToLower() == "x64")
			{
				Arch = WindowsArchitecture.x64;
			}

			string SDK = "";
			var Results = Receipt.AdditionalProperties.Where(x => x.PropertyName == "SDK");
			if (Results.Any())
			{
				SDK = Results.First().PropertyValue;
			}
			HoloLensExports.InitWindowsSdkToolPath(SDK);

            List<string> ExePaths = new List<string> { Receipt.Launch.FullName };
            string AbsoluteExeDirectory = Path.GetDirectoryName(ExePaths[0]);
            BuildTargetPlatform Platform = BuildTargetPlatform.HoloLens;
			string IntermediateDirectory = Path.Combine(Receipt.ProjectDir != null ? Receipt.ProjectDir.FullName : BuildTool.EngineDirectory.FullName, "Intermediate", "Deploy", WindowsExports.GetArchitectureSubpath(Arch));
			List<string> UpdatedFiles = new HoloLensManifestGenerator().CreateManifest
			(
                Platform, 
				Arch, 
				AbsoluteExeDirectory, 
				IntermediateDirectory, 
				Receipt.ProjectFile,
                Receipt.ProjectDir != null ? Receipt.ProjectDir.FullName : BuildTool.EngineDirectory.FullName, 
				TargetConfigs,
                ExePaths, 
				WinMDReferences
			);

			PrepareForUATPackageOrDeploy(Receipt.ProjectDir != null ? Receipt.ProjectDir.FullName : BuildTool.EngineDirectory.FullName, ExePaths);
			MakePackage(Receipt, NewReceipt, Arch, UpdatedFiles);
			CopyDataAndSymbolsBetweenReceipts(Receipt, NewReceipt);

			NewReceipt.Write(ReceiptFileName, BuildTool.EngineDirectory);

			// Log out the time taken to deploy...
			double PrepDeployDuration = (DateTime.UtcNow - PrepDeployStartTime).TotalSeconds;
			Log.TraceInformation("HoloLens deployment preparation took {0:0.00} seconds", PrepDeployDuration);

			return true;
		}

		public void AddWinMDReferencesFromReceipt(TargetReceipt Receipt, DirectoryReference SourceProjectDir, string DestRelativeTo)
		{
			// Dependency paths in receipt are already expanded at this point
			foreach (var Dep in Receipt.RuntimeDependencies)
			{
				if (Dep.Path.GetExtension() == ".dll")
				{
					string SourcePath = Dep.Path.FullName;
					string WinMDFile = Path.ChangeExtension(SourcePath, "winmd");
					if (File.Exists(WinMDFile))
					{
						string DestPath = Dep.Path.FullName.Replace(BuildTool.EngineDirectory.FullName, Path.Combine(DestRelativeTo, "Engine"));
						DestPath = DestPath.Replace(SourceProjectDir.FullName, Path.Combine(DestRelativeTo, SourceProjectDir.GetDirectoryName()));
						DestPath = Utils.MakePathRelativeTo(DestPath, DestRelativeTo);
						WinMDReferences.Add(new WinMDRegistrationInfo(new FileReference(WinMDFile), DestPath));
					}
				}
			}
		}
	}
}
