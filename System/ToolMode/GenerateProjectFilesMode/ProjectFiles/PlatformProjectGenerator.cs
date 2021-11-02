using System;
using System.Collections.Generic;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// Base class for platform-specific project generators
	abstract class PlatformProjectGenerator
	{
#pragma warning disable IDE0079 // Remove unused parameter
#pragma warning disable IDE0060 // Remove unused parameter
		protected PlatformProjectGenerator(CommandLineArguments Arguments)
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore IDE0079 // Remove unused parameter
		{
		}

		// Register the platform with the UEPlatformProjectGenerator class
		public abstract IEnumerable<BuildTargetPlatform> GetPlatforms();

		// Do nothing All Derived classes.
		/*
		public virtual void GenerateGameProjectStub
		(
			ProjectFileGenerator InGenerator,
			string InTargetName,
			string InTargetFilepath,
			TargetRules InTargetRules,
			List<BuildTargetPlatform> InPlatforms,
			List<BuildTargetConfiguration> InConfigurations
		);
		*/
		public virtual void GenerateGameProperties(TargetConfiguration Configuration, StringBuilder VCProjectFileContent, TargetType TargetType, DirectoryReference RootDirectory, FileReference TargetFilePath)
		{
			// Do nothing
		}

        public virtual bool RequiresVSUserFileGeneration() => false;

		public virtual bool HasVisualStudioSupport(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, VCProjectFileFormat ProjectFileFormat)
			=> true; // By default, we assume this is true


        // Return the VisualStudio platform name for this build platform
        public virtual string GetVisualStudioPlatformName(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration) 
			=> InPlatform.ToString(); // By default, return the platform string

        // Return project configuration settings that must be included before the default props file
        public virtual void GetVisualStudioPreDefaultString(BuildTargetPlatform Platform, TargetConfiguration Configuration, StringBuilder ProjectFileBuilder)
		{
		}

		// Return the platform toolset string to write into the project configuration
		public virtual void GetVisualStudioPlatformToolsetString(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, VCProjectFileFormat InProjectFileFormat, StringBuilder ProjectFileBuilder)
		{
		}

		// Return any custom property group lines
		public virtual void GetAdditionalVisualStudioPropertyGroups
		(
            BuildTargetPlatform InPlatform,
            VCProjectFileFormat InProjectFileFormat,
            StringBuilder ProjectFileBuilder
		)
		{
			// The custom property import lines for the project file; Empty string if it doesn't require one
		}


		// Return any custom property group lines
		public virtual string GetVisualStudioPlatformConfigurationType(BuildTargetPlatform InPlatform, VCProjectFileFormat InProjectFileFormat) 
			=> "Makefile";


        // Return any custom paths for VisualStudio this platform requires
        // This include ReferencePath, LibraryPath, LibraryWPath, IncludePath and ExecutablePath.
        public virtual void GetVisualStudioPathsEntries
		(
            BuildTargetPlatform InPlatform,
            TargetConfiguration InConfiguration,
            TargetType TargetType,
            FileReference TargetRulesPath, // .target.cs
            FileReference ProjectFilePath,
            FileReference NMakeOutputPath,
            VCProjectFileFormat InProjectFileFormat,
            StringBuilder ProjectFileBuilder
		)
		{
			// NOTE: We are intentionally overriding defaults for these paths with empty strings.  We never want Visual Studio's
			//       defaults for these fields to be propagated, since they are version-sensitive paths that may not reflect
			//       the environment that UBT is building in.  We'll set these environment variables ourselves!
			// NOTE: We don't touch 'ExecutablePath' because that would result in Visual Studio clobbering the system "Path"
			//       environment variable

			string DoubleIndent = Tag.CppProjectContents.Indent(2);

			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.IncludePathEmpty);
			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.ReferencePathEmpty);
			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.LibraryPathEmpty);
			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.LibraryWPathEmpty);
			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.SourcePathEmpty);
			ProjectFileBuilder.AppendLine(DoubleIndent + Tag.CppProjectContents.Format.ExcludePathEmpty);
		}

		// Return any custom property settings. These will be included in the ImportGroup section
		public virtual void GetVisualStudioImportGroupProperties(BuildTargetPlatform InPlatform, StringBuilder ProjectFileBuilder)
		{
			// The custom property import lines for the project file; Empty string if it doesn't require one
		}

		// Return any custom property settings. These will be included right after Global properties to make values available to all other imports.
		public virtual void GetVisualStudioGlobalProperties(BuildTargetPlatform InPlatform, StringBuilder ProjectFileBuilder)
		{
			// The custom property import lines for the project file; Empty string if it doesn't require one
		}


		// Return any custom target overrides. These will be included last in the project file so they have the opportunity to override any existing settings.
		public virtual void GetVisualStudioTargetOverrides
		(
            BuildTargetPlatform InPlatform,
            VCProjectFileFormat InProjectFileFormat,
            StringBuilder ProjectFileBuilder
		)
		{
		}

		
		// Return any custom layout directory sections
		public virtual string GetVisualStudioLayoutDirSection
		(
            BuildTargetPlatform InPlatform,
            TargetConfiguration InConfiguration,
            string InConditionString,
            TargetType TargetType,
            FileReference TargetRulesPath,
            FileReference ProjectFilePath,
            FileReference NMakeOutputPath,
            VCProjectFileFormat InProjectFileFormat
		)
		=> "";

		
		// Get the output manifest section, if required
		public virtual string GetVisualStudioOutputManifestSection
		(
            BuildTargetPlatform InPlatform,
            TargetType TargetType,
            FileReference TargetRulesPath, // .target.cs
            FileReference ProjectFilePath,
            VCProjectFileFormat InProjectFileFormat
		)
		=> "";

		// Get whether this platform deploys
		public virtual bool GetVisualStudioDeploymentEnabled(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration)
		=> false;

		
		// Get the text to insert into the user file for the given platform/configuration/target
		public virtual string GetVisualStudioUserFileStrings
		(
            BuildTargetPlatform InPlatform,
            TargetConfiguration InConfiguration,
            string InConditionString,
            TargetRules InTargetRules,
            FileReference TargetRulesPath,
            FileReference ProjectFilePath
		)
		=> "";
		
		// For Additional Project Property files that need to be written out.
		// This is currently used only on Android. 
		public virtual void WriteAdditionalPropFile()
		{
		}

		// For additional Project files (ex. *PROJECTNAME*-AndroidRun.androidproj.user) that needs to be written out.
		// This is currently used only on Android. 
		public virtual void WriteAdditionalProjUserFile(ProjectFile ProjectFile)
		{
		}

		// For additional Project files (ex. *PROJECTNAME*-AndroidRun.androidproj) that needs to be written out.
		// This is currently used only on Android.
		public virtual Tuple<ProjectFile, string> WriteAdditionalProjFile(ProjectFile ProjectFile)
		{
			return null;
		}
    }
}
