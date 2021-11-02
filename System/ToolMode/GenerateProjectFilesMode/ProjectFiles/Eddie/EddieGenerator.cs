using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class EddieProjectFolder : MasterProjectFolder
	{
		public EddieProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName)
			: base(InitOwnerProjectFileGenerator, InitFolderName)
		{
		}
	}

	class EddieProjectFileGenerator : ProjectFileGenerator
	{
		public EddieProjectFileGenerator(FileReference InOnlyGameProject)
			: base(InOnlyGameProject)
		{
		}

		override public string ProjectFileExtension => ".wkst";

		public override void CleanProjectFiles(DirectoryReference InMasterProjectDirectory, string InMasterProjectName, DirectoryReference InIntermediateProjectFilesPath)
		{
			FileReference MasterProjDeleteFilename = FileReference.Combine(InMasterProjectDirectory, InMasterProjectName + ".wkst");
			if (FileReference.Exists(MasterProjDeleteFilename))
			{
				File.Delete(MasterProjDeleteFilename.FullName);
			}

			// Delete the project files folder
			if (DirectoryReference.Exists(InIntermediateProjectFilesPath))
			{
				try
				{
					Directory.Delete(InIntermediateProjectFilesPath.FullName, true);
				}
				catch (Exception Ex)
				{
					Log.TraceInformation("Error while trying to clean project files path {0}. Ignored.", InIntermediateProjectFilesPath);
					Log.TraceInformation("\t" + Ex.Message);
				}
			}
		}

		protected override ProjectFile AllocateProjectFile(FileReference InitFilePath) 
			=> new EddieProjectFile(InitFilePath, OnlyGameProject);

		public override MasterProjectFolder AllocateMasterProjectFolder(ProjectFileGenerator InitOwnerProjectFileGenerator, string InitFolderName) 
			=> new EddieProjectFolder(InitOwnerProjectFileGenerator, InitFolderName);

		private bool WriteEddieWorkset()
		{
			bool bSuccess = false;
			
			StringBuilder WorksetDataContent = new StringBuilder();
			WorksetDataContent.Append("# @Eddie Workset@" + ProjectFileGenerator.NewLine);
			WorksetDataContent.Append("AddWorkset \"" + MasterProjectName + ".wkst\" \"" + MasterProjectPath + "\"" + ProjectFileGenerator.NewLine);

			void AddProjectsFunction(string Path, List<MasterProjectFolder> FolderList)
			{
				foreach (EddieProjectFolder CurFolder in FolderList)
				{
					String NewPath = Path + "/" + CurFolder.FolderName;
					WorksetDataContent.Append("AddFileGroup \"" + NewPath + "\" \"" + CurFolder.FolderName + "\"" + ProjectFileGenerator.NewLine);

					AddProjectsFunction(NewPath, CurFolder.SubFolders);

					foreach (ProjectFile CurProject in CurFolder.ChildProjects)
					{
						if (CurProject is EddieProjectFile EddieProject)
						{
							WorksetDataContent.Append("AddFile \"" + EddieProject.ToString() + "\" \"" + EddieProject.ProjectFilePath + "\"" + ProjectFileGenerator.NewLine);
						}
					}

					WorksetDataContent.Append("EndFileGroup \"" + NewPath + "\"" + ProjectFileGenerator.NewLine);
				}
			}

			AddProjectsFunction(MasterProjectName, RootFolder.SubFolders);
			
			string ProjectName = MasterProjectName;
			string FilePath = MasterProjectPath + "/" + ProjectName + ".wkst";
			
			bSuccess = WriteFileIfChanged(FilePath, WorksetDataContent.ToString(), new UTF8Encoding());
			
			return bSuccess;
		}
		
		protected override bool WriteMasterProjectFile(ProjectFile UBTProject, PlatformProjectGeneratorCollection PlatformProjectGenerators)
		{
			return WriteEddieWorkset();
		}
		
		protected override void ConfigureProjectFileGeneration(string[] Arguments, ref bool IncludeAllPlatforms)
		{
			// Call parent implementation first
			base.ConfigureProjectFileGeneration(Arguments, ref IncludeAllPlatforms);

			if (bGeneratingGameProjectFiles)
			{
				bIncludeEngineSourceInSolution = true;
			}
		}
		protected override void WriteDebugSolutionFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators, DirectoryReference IntermediateProjectFilesPath)
		{
		}
	}
}