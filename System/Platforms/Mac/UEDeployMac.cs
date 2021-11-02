// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.IO;
using Ionic.Zip;
using BuildToolUtilities;

namespace BuildTool
{
	class UEDeployMac : BuildDeploy
	{
		public override bool PrepTargetForDeployment(TargetReceipt Receipt)
		{
			Log.TraceInformation("Deploying now!");
			return true;
		}

		public static bool GeneratePList(string ProjectDirectory, bool bIsDefaultGame, string GameName, string ProjectName, string InEngineDir, string ExeName)
		{
			string IntermediateDirectory = (bIsDefaultGame ? InEngineDir : ProjectDirectory) + "/Intermediate/Mac";
			string DestPListFile = IntermediateDirectory + "/" + ExeName + "-Info.plist";
			string SrcPListFile = (bIsDefaultGame ? (InEngineDir + "Source/Programs/") : (ProjectDirectory + "/Source/")) + GameName + "/Resources/Mac/Info.plist";
			if (!File.Exists(SrcPListFile))
			{
				SrcPListFile = InEngineDir + "/Source/Runtime/Launch/Resources/Mac/Info.plist";
			}

			string PListData;

			if (File.Exists(SrcPListFile))
			{
				PListData = File.ReadAllText(SrcPListFile);
			}
			else
			{
				return false;
			}

            // Bundle ID
            // plist replacements
			DirectoryReference DirRef;

			if(bIsDefaultGame)
			{
				if (BuildTool.GetRemoteIniPath().HasValue())
				{
					DirRef = new DirectoryReference(BuildTool.GetRemoteIniPath());
				}
				else
				{
					DirRef = null;
				}
			}
			else
			{
				DirRef = new DirectoryReference(ProjectDirectory);
			}

            ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, DirRef, BuildTargetPlatform.IOS);

			Ini.GetString("/Script/IOSRuntimeSettings.IOSRuntimeSettings", "BundleIdentifier", out string BundleIdentifier);

			string BundleVersion = MacToolChain.LoadEngineDisplayVersion();
			PListData = PListData.Replace("${EXECUTABLE_NAME}", ExeName).Replace("${APP_NAME}", BundleIdentifier.Replace("[PROJECT_NAME]", ProjectName).Replace("_", "")).Replace("${ICON_NAME}", GameName).Replace("${MACOSX_DEPLOYMENT_TARGET}", MacToolChain.Settings.MinMacOSVersion).Replace("${BUNDLE_VERSION}", BundleVersion);

			if (!Directory.Exists(IntermediateDirectory))
			{
				Directory.CreateDirectory(IntermediateDirectory);
			}
			File.WriteAllText(DestPListFile, PListData);

			return true;
		}
	}
}
