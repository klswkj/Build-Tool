// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	
	// Represents a folder within the master project (e.g. Visual Studio solution)
	
	class XcodeProjectFolder : MasterProjectFolder
	{
		public XcodeProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
			: base(InitOwnerProjectFileGenerator, InitFolderName)
		{
		}
	}

	// Xcode project file generator implementation
	class XcodeProjectFileGenerator : ProjectFileGenerator
	{
		// always seed the random number the same, so multiple runs of the generator will generate the same project
		private readonly static Random Rand = new Random(0);

		private readonly bool   bForDistribution = false; // Mark for distribution builds
		private readonly string BundleIdentifier = ""; // Override BundleID
		private readonly string AppName          = ""; // Override AppName

		public XcodeProjectFileGenerator(FileReference InOnlyGameProject, CommandLineArguments CommandLine)
			: base(InOnlyGameProject)
		{
			if (CommandLine.HasOption("-distribution"))
			{
				bForDistribution = true;
			}
			if (CommandLine.HasValue("-bundleID="))
			{
				BundleIdentifier = CommandLine.GetString("-bundleID=");
			}

			if (CommandLine.HasValue("-appname="))
			{
				AppName = CommandLine.GetString("-appname=");
			}
		}

		// Make a random Guid string usable by Xcode (24 characters exactly)
		public static string MakeXcodeGuid()
		{
			string Guid = "";

			byte[] Randoms = new byte[12];
			Rand.NextBytes(Randoms);
			for (int Index = 0; Index < 12; Index++)
			{
				Guid += Randoms[Index].ToString("X2");
			}

			return Guid;
		}

		public override string ProjectFileExtension => ".xcodeproj"; // File extension for project files we'll be generating (e.g. ".vcxproj")

		static public XcodeProjectFilePlatform ProjectFilePlatform = XcodeProjectFilePlatform.All; // Which platforms we should generate targets for
		static public bool bGeneratingRunIOSProject  = false; // Should we generate a special project to use for iOS signing instead of a normal one
		static public bool bGeneratingRunTVOSProject = false; // Should we generate a special project to use for tvOS signing instead of a normal one

		public override void CleanProjectFiles(DirectoryReference InMasterProjectDirectory, string InMasterProjectName, DirectoryReference InIntermediateProjectFilesPath)
		{
			DirectoryReference MasterProjDeleteFilename = DirectoryReference.Combine(InMasterProjectDirectory, InMasterProjectName + ".xcworkspace");
			if (DirectoryReference.Exists(MasterProjDeleteFilename))
			{
				DirectoryReference.Delete(MasterProjDeleteFilename, true);
			}

			// Delete the project files folder
			if (DirectoryReference.Exists(InIntermediateProjectFilesPath))
			{
				try
				{
					DirectoryReference.Delete(InIntermediateProjectFilesPath, true);
				}
				catch (Exception Ex)
				{
					Log.TraceInformation("Error while trying to clean project files path {0}. Ignored.", InIntermediateProjectFilesPath);
					Log.TraceInformation("\t" + Ex.Message);
				}
			}
		}

		// Allocates a generator-specific project file object
		protected override ProjectFile AllocateProjectFile(FileReference InitFilePath)
		{
			return new XcodeProjectFile(InitFilePath, OnlyGameProject, bForDistribution, BundleIdentifier, AppName);
		}

		// ProjectFileGenerator interface
		public override MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
		{
			return new XcodeProjectFolder(InitOwnerProjectFileGenerator, InitFolderName);
		}

		private bool WriteWorkspaceSettingsFile(string Path)
		{
			StringBuilder WorkspaceSettingsContent = new StringBuilder();
			WorkspaceSettingsContent.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("<plist version=\"1.0\">" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("<dict>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>BuildSystemType</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>Original</string>" + ProjectFileGenerator.NewLine);
            WorkspaceSettingsContent.Append("\t<key>BuildLocationStyle</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>UseTargetSettings</string>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>CustomBuildLocationType</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>RelativeToDerivedData</string>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>DerivedDataLocationStyle</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>Default</string>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>IssueFilterStyle</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>ShowAll</string>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>LiveSourceIssuesEnabled</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<true/>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>SnapshotAutomaticallyBeforeSignificantChanges</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<true/>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<key>SnapshotLocationStyle</key>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("\t<string>Default</string>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("</dict>" + ProjectFileGenerator.NewLine);
			WorkspaceSettingsContent.Append("</plist>" + ProjectFileGenerator.NewLine);
			return WriteFileIfChanged(Path, WorkspaceSettingsContent.ToString(), new UTF8Encoding());
		}

		private bool WriteXcodeWorkspace()
		{
			bool bSuccess = true;

			StringBuilder WorkspaceDataContent = new StringBuilder();

			WorkspaceDataContent.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + ProjectFileGenerator.NewLine);
			WorkspaceDataContent.Append("<Workspace" + ProjectFileGenerator.NewLine);
			WorkspaceDataContent.Append("   version = \"1.0\">" + ProjectFileGenerator.NewLine);

			void AddProjectsFunction(List<MasterProjectFolder> FolderList, string Ident)
			{
				int SchemeIndex = 0;
				foreach (XcodeProjectFolder CurFolder in FolderList)
				{
					WorkspaceDataContent.Append(Ident + "   <Group" + ProjectFileGenerator.NewLine);
					WorkspaceDataContent.Append(Ident + "      location = \"container:\"      name = \"" + CurFolder.FolderName + "\">" + ProjectFileGenerator.NewLine);

					AddProjectsFunction(CurFolder.SubFolders, Ident + "   ");

					List<ProjectFile> ChildProjects = new List<ProjectFile>(CurFolder.ChildProjects);
					ChildProjects.Sort((ProjectFile A, ProjectFile B) => { return A.ProjectFilePath.GetFileName().CompareTo(B.ProjectFilePath.GetFileName()); });

					foreach (ProjectFile CurProject in ChildProjects)
					{
						if (CurProject is XcodeProjectFile XcodeProject)
						{
							WorkspaceDataContent.Append(Ident + "      <FileRef" + ProjectFileGenerator.NewLine);
							WorkspaceDataContent.Append(Ident + "         location = \"group:" + XcodeProject.ProjectFilePath.MakeRelativeTo(ProjectFileGenerator.MasterProjectPath) + "\">" + ProjectFileGenerator.NewLine);
							WorkspaceDataContent.Append(Ident + "      </FileRef>" + ProjectFileGenerator.NewLine);

							// Also, update project's schemes index so that the schemes list order match projects order in the navigator
							FileReference SchemeManagementFile = XcodeProject.ProjectFilePath + "/xcuserdata/" + Environment.UserName + ".xcuserdatad/xcschemes/xcschememanagement.plist";
							if (FileReference.Exists(SchemeManagementFile))
							{
								string SchemeManagementContent = FileReference.ReadAllText(SchemeManagementFile);
								SchemeManagementContent = SchemeManagementContent.Replace("<key>orderHint</key>\n\t\t\t<integer>1</integer>", "<key>orderHint</key>\n\t\t\t<integer>" + SchemeIndex.ToString() + "</integer>");
								FileReference.WriteAllText(SchemeManagementFile, SchemeManagementContent);
								SchemeIndex++;
							}
						}
					}

					WorkspaceDataContent.Append(Ident + "   </Group>" + ProjectFileGenerator.NewLine);
				}
			}

			AddProjectsFunction(RootFolder.SubFolders, "");

			WorkspaceDataContent.Append("</Workspace>" + ProjectFileGenerator.NewLine);

			string ProjectName = MasterProjectName;
			if (ProjectFilePlatform != XcodeProjectFilePlatform.All)
			{
				ProjectName += ProjectFilePlatform == XcodeProjectFilePlatform.Mac ? "_Mac" : (ProjectFilePlatform == XcodeProjectFilePlatform.iOS ? "_IOS" : "_TVOS");
			}
			string WorkspaceDataFilePath = MasterProjectPath + "/" + ProjectName + ".xcworkspace/contents.xcworkspacedata";
			bSuccess = WriteFileIfChanged(WorkspaceDataFilePath, WorkspaceDataContent.ToString(), new UTF8Encoding());
			if (bSuccess)
			{
				string WorkspaceSettingsFilePath = MasterProjectPath + "/" + ProjectName + ".xcworkspace/xcuserdata/" + Environment.UserName + ".xcuserdatad/WorkspaceSettings.xcsettings";
				bSuccess = WriteWorkspaceSettingsFile(WorkspaceSettingsFilePath);
			}

			return bSuccess;
		}

		protected override bool WriteMasterProjectFile(ProjectFile UBTProject, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			return WriteXcodeWorkspace();
		}

		protected override void WriteDebugSolutionFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators, DirectoryReference IntermediateProjectFilesPath)
		{
		}

		[Flags]
		public enum XcodeProjectFilePlatform
		{
			Mac = 1 << 0,
			iOS = 1 << 1,
			tvOS = 1 << 2,
			All = Mac | iOS | tvOS
		}

		// Configures project generator based on command-line options
		
		// <param name="Arguments">Arguments passed into the program</param>
		// <param name="IncludeAllPlatforms">True if all platforms should be included</param>
		protected override void ConfigureProjectFileGeneration(string[] Arguments, ref bool IncludeAllPlatforms)
		{
			// Call parent implementation first
			base.ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);
			ProjectFilePlatform = IncludeAllPlatforms ? XcodeProjectFilePlatform.All : XcodeProjectFilePlatform.Mac;

			foreach (string CurArgument in Arguments)
			{
				if (CurArgument.StartsWith("-iOSDeployOnly", StringComparison.InvariantCultureIgnoreCase))
				{
					bGeneratingRunIOSProject = true;
					break;
				}

				if (CurArgument.StartsWith("-tvOSDeployOnly", StringComparison.InvariantCultureIgnoreCase))
				{
					bGeneratingRunTVOSProject = true;
					break;
				}
			}

			if (bGeneratingGameProjectFiles)
			{
				bIncludeEngineSourceInSolution = true;
			}
		}
	}
}
