using System.Collections.Generic;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores all the registered platform project generators
	class PlatformProjectGeneratorCollection
	{
		private readonly Dictionary<BuildTargetPlatform, PlatformProjectGenerator> ProjectGeneratorDictionary = new Dictionary<BuildTargetPlatform, PlatformProjectGenerator>();

		// Register the given platforms UEPlatformProjectGenerator instance
		public void RegisterPlatformProjectGenerator(BuildTargetPlatform InPlatformToRegister, PlatformProjectGenerator InPlatformProjectGenerator)
		{
			// Make sure the build platform is legal
			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(InPlatformToRegister, true);
			if (BuildPlatform != null)
			{
				if (ProjectGeneratorDictionary.ContainsKey(InPlatformToRegister) == true)
				{
					Log.TraceInformation("RegisterPlatformProjectGenerator Warning: Registering project generator {0} for {1} when it is already set to {2}",
						InPlatformProjectGenerator.ToString(), InPlatformToRegister.ToString(), ProjectGeneratorDictionary[InPlatformToRegister].ToString());
					ProjectGeneratorDictionary[InPlatformToRegister] = InPlatformProjectGenerator;
				}
				else
				{
					ProjectGeneratorDictionary.Add(InPlatformToRegister, InPlatformProjectGenerator);
				}
			}
			else
			{
				Log.TraceVerbose("Skipping project file generator registration for {0} due to no valid BuildPlatform.", InPlatformToRegister.ToString());
			}
		}

		// Retrieve the UEPlatformProjectGenerator instance for the given TargetPlatform
		public PlatformProjectGenerator GetPlatformProjectGenerator(BuildTargetPlatform InPlatform, bool bInAllowFailure = false)
		{
			if (ProjectGeneratorDictionary.ContainsKey(InPlatform) == true)
			{
				return ProjectGeneratorDictionary[InPlatform];
			}
			if (bInAllowFailure == true)
			{
				return null;
			}
			throw new BuildException("GetPlatformProjectGenerator: No PlatformProjectGenerator found for {0}", InPlatform.ToString());
		}

		/*
		// Allow various platform project generators to generate stub projects if required
		public bool GenerateGameProjectStubs(ProjectFileGenerator InGenerator, string InTargetName, string InTargetFilepath, TargetRules InTargetRules,
			List<BuildTargetPlatform> InPlatforms, List<BuildTargetConfiguration> InConfigurations)
		{
			foreach (KeyValuePair<BuildTargetPlatform, PlatformProjectGenerator> Entry in ProjectGeneratorDictionary)
			{
				PlatformProjectGenerator ProjGen = Entry.Value;
				ProjGen.GenerateGameProjectStub(InGenerator, InTargetName, InTargetFilepath, InTargetRules, InPlatforms, InConfigurations);
			}
			return true;
		}
		*/
		// Allow various platform project generators to generate any special project properties if required
		public bool GenerateGamePlatformSpecificProperties(BuildTargetPlatform InPlatform, TargetConfiguration Configuration, TargetType TargetType, StringBuilder VCProjectFileContent, DirectoryReference RootDirectory, FileReference TargetFilePath)
		{
			if (ProjectGeneratorDictionary.ContainsKey(InPlatform) == true)
			{
				ProjectGeneratorDictionary[InPlatform].GenerateGameProperties(Configuration, VCProjectFileContent, TargetType, RootDirectory, TargetFilePath); ;
			}
			return true;
		}

#pragma warning disable IDE0060 // Remove unused parameter
		public bool PlatformRequiresVSUserFileGeneration(List<BuildTargetPlatform> InPlatforms, List<TargetConfiguration> InConfigurations)
		{
			bool bRequiresVSUserFileGeneration = false;
			foreach (KeyValuePair<BuildTargetPlatform, PlatformProjectGenerator> Entry in ProjectGeneratorDictionary)
			{
				if (InPlatforms.Contains(Entry.Key))
				{
					PlatformProjectGenerator ProjGen = Entry.Value;
					bRequiresVSUserFileGeneration |= ProjGen.RequiresVSUserFileGeneration();
				}
			}
			return bRequiresVSUserFileGeneration;
		}
	}
}
