using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// The version format for .uproject files.
	// This rarely changes now; project descriptors should maintain backwards compatibility automatically.	
	enum ProjectDescriptorVersion
	{
		Invalid                  = 0,
		Initial                  = 1,
		NameHash                 = 2, // Adding SampleNameHash
		ProjectPluginUnification = 3, // Unifying plugin/project files (since abandoned, but backwards compatibility maintained)
		LatestPlusOne,                // This needs to be the last line, so we can calculate the value of Latest below
		Latest = LatestPlusOne - 1    // The latest plugin descriptor version
	}

	// In-memory representation of a .uproject file
	// Keep Sync with .uproject files, In PROJECTS_API, FProjectDescriptor.h/cpp FModuleDescriptor.h/cpp, FPluginsDescriptor.h/cpp, FCustomBuildSteps.h/cpp
	public class UProjectDescriptor
	{
		public int      FileVersion;       // Descriptor version number.
		public string   EngineAssociation; // The engine to open this project with.
		public string   Category;          // Category to show under the project browser
		public string   Description;       // Description to show in the project browser
		public string[] TargetPlatforms;   // Platforms that this project is targeting

		public ModuleDescriptor[]          Modules; // List of all modules associated with this project
		public PluginReferenceDescriptor[] Plugins; // List of plugins for this project (may be enabled/disabled)
		public List<DirectoryReference> AdditionalRootDirectories = new List<DirectoryReference>(); 
		public List<DirectoryReference> AdditionalPluginDirectories = new List<DirectoryReference>();

		public CustomBuildSteps PreBuildSteps; // Steps to execute before building targets in this project
		public CustomBuildSteps PostBuildSteps; // Steps to execute before building targets in this project

		public uint SampleNameHash; // A hash that is used to determine if the project was forked from a sample
		public bool IsEnterpriseProject; // Indicates if this project is an Enterprise project
		public bool DisableEnginePluginsByDefault; // Indicates that enabled by default engine plugins should not be enabled unless explicitly enabled by the project or target files.

		public UProjectDescriptor()
		{
			FileVersion = (int)ProjectDescriptorVersion.Latest;
			IsEnterpriseProject = false;
			DisableEnginePluginsByDefault = false;
		}

		public UProjectDescriptor(JsonObject RawObject, DirectoryReference BaseDir)
		{
			// Read the version
			if (!RawObject.TryGetIntegerField(nameof(UProjectDescriptor.FileVersion), out FileVersion))
			{
				throw new BuildException("Project does not contain a valid FileVersion entry");
			}

			// Check it's not newer than the latest version we can parse
			if ((int)PluginDescriptorVersion.Latest < FileVersion)
			{
				throw new BuildException("Project descriptor appears to be in a newer version ({0}) of the file format that we can load (max version: {1}).", FileVersion, (int)ProjectDescriptorVersion.Latest);
			}

			// Read simple fields
			RawObject.TryGetStringField(nameof(UProjectDescriptor.EngineAssociation), out EngineAssociation);
			RawObject.TryGetStringField(nameof(UProjectDescriptor.Category), out Category);
			RawObject.TryGetStringField(nameof(UProjectDescriptor.Description), out Description);
			RawObject.TryGetBoolField(nameof(UProjectDescriptor.IsEnterpriseProject), out IsEnterpriseProject);
			RawObject.TryGetBoolField(nameof(UProjectDescriptor.DisableEnginePluginsByDefault), out DisableEnginePluginsByDefault);

			// Read the modules
			if (RawObject.TryGetObjectArrayField(nameof(UProjectDescriptor.Modules), out JsonObject[] ModulesArray))
			{
				Modules = Array.ConvertAll(ModulesArray, x => ModuleDescriptor.FromJsonObject(x));
			}

			// Read the plugins
			if (RawObject.TryGetObjectArrayField(nameof(UProjectDescriptor.Plugins), out JsonObject[] PluginsArray))
			{
				Plugins = Array.ConvertAll(PluginsArray, x => PluginReferenceDescriptor.FromJsonObject(x));
			}

			// Read the additional root directories
			if (RawObject.TryGetStringArrayField(nameof(UProjectDescriptor.AdditionalRootDirectories), out string[] RootDirectoryStrings))
			{
				AdditionalRootDirectories.AddRange(RootDirectoryStrings.Select(x => DirectoryReference.Combine(BaseDir, x)));
			}

			// Read the additional plugin directories
			if (RawObject.TryGetStringArrayField(nameof(UProjectDescriptor.AdditionalPluginDirectories), out string[] PluginDirectoryStrings))
			{
				AdditionalPluginDirectories.AddRange(PluginDirectoryStrings.Select(x => DirectoryReference.Combine(BaseDir, x)));
			}

			// Read the target platforms
			RawObject.TryGetStringArrayField(nameof(UProjectDescriptor.TargetPlatforms), out TargetPlatforms);

			// Get the sample name hash
			RawObject.TryGetUnsignedIntegerField(nameof(UProjectDescriptor.SampleNameHash), out SampleNameHash);

			// Read the pre and post-build steps
			CustomBuildSteps.TryRead(RawObject, nameof(UProjectDescriptor.PreBuildSteps),  out PreBuildSteps);
			CustomBuildSteps.TryRead(RawObject, nameof(UProjectDescriptor.PostBuildSteps), out PostBuildSteps);
		}

		// Creates a plugin descriptor from a file on disk
		public static UProjectDescriptor FromFile(FileReference FileNameToRead)
		{
			JsonObject RawObject = JsonObject.Read(FileNameToRead);
			try
			{
				UProjectDescriptor Descriptor = new UProjectDescriptor(RawObject, FileNameToRead.Directory);
				if(Descriptor.Modules != null)
				{
					foreach (ModuleDescriptor Module in Descriptor.Modules)
					{
						Module.Validate(FileNameToRead);
					}
				}
				return Descriptor;
			}
			catch (JsonParseException ParseException)
			{
				throw new JsonParseException("{0} (in {1})", ParseException.Message, FileNameToRead);
			}
		}

		// If the descriptor has either additional plugin directories or
		// additional root directories then it is considered to have additional paths.
		// The additional paths will be relative to the provided directory.
		public void AddAdditionalPaths(List<DirectoryReference> RootDirectoriesToBeAdd/*, DirectoryReference ProjectDir*/)
		{
			RootDirectoriesToBeAdd.AddRange(AdditionalRootDirectories);
			RootDirectoriesToBeAdd.AddRange(AdditionalPluginDirectories);
		}

		// Saves the descriptor to disk
		public void Save(FileReference FileNameToWriteTo)
		{
			using (JsonWriter Writer = new JsonWriter(FileNameToWriteTo))
			{
				Writer.WriteObjectStart();
				Write(Writer, FileNameToWriteTo.Directory);
				Writer.WriteObjectEnd();
			}
		}

		// Writes the plugin descriptor to an existing Json writer
		public void Write(JsonWriter Writer, DirectoryReference BaseDirToSave)
		{
			Writer.WriteValue(nameof(UProjectDescriptor.FileVersion), (int)ProjectDescriptorVersion.Latest);
			Writer.WriteValue(nameof(UProjectDescriptor.EngineAssociation), EngineAssociation);
			Writer.WriteValue(nameof(UProjectDescriptor.Category), Category);
			Writer.WriteValue(nameof(UProjectDescriptor.Description), Description);

			// Write the enterprise flag
			if (IsEnterpriseProject)
			{
				Writer.WriteValue(nameof(UProjectDescriptor.IsEnterpriseProject), IsEnterpriseProject);
			}

			// Write the module list
			ModuleDescriptor.WriteArray(Writer, nameof(UProjectDescriptor.Modules), Modules);

			// Write the plugin list
			PluginReferenceDescriptor.WriteArray(Writer, nameof(UProjectDescriptor.Plugins), Plugins);

			// Write the custom module roots
			if(0 < AdditionalRootDirectories.Count)
			{
				Writer.WriteStringArrayField(nameof(UProjectDescriptor.AdditionalRootDirectories), AdditionalRootDirectories.Select(x => x.MakeRelativeTo(BaseDirToSave).Replace(Path.DirectorySeparatorChar, '/')));
			}

			// Write out the additional plugin directories to scan
			if(0 < AdditionalPluginDirectories.Count)
			{
				Writer.WriteStringArrayField(nameof(UProjectDescriptor.AdditionalPluginDirectories), AdditionalPluginDirectories.Select(x => x.MakeRelativeTo(BaseDirToSave).Replace(Path.DirectorySeparatorChar, '/')));
			}

			// Write the target platforms
			if(TargetPlatforms != null && 0 < TargetPlatforms.Length)
			{
				Writer.WriteStringArrayField(nameof(UProjectDescriptor.TargetPlatforms), TargetPlatforms);
			}

			// If it's a signed sample, write the name hash
			if(SampleNameHash != 0)
			{
				Writer.WriteValue(nameof(UProjectDescriptor.SampleNameHash), (uint)SampleNameHash);
			}

			// Write the custom build steps
			if(PreBuildSteps != null)
			{
				PreBuildSteps.Write(Writer, nameof(UProjectDescriptor.PreBuildSteps));
			}
			if(PostBuildSteps != null)
			{
				PostBuildSteps.Write(Writer, nameof(UProjectDescriptor.PostBuildSteps));
			}
		}
	}
}
