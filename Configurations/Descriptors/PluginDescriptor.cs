using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// The version format for .uplugin files.
	// This rarely changes now; plugin descriptors should maintain backwards compatibility automatically.
	public enum PluginDescriptorVersion
	{
		Invalid                  = 0,
		Initial                  = 1,
		NameHash                 = 2, // Adding SampleNameHash
		ProjectPluginUnification = 3, // Unifying plugin/project files (since abandoned, but backwards compatibility maintained)
		LatestPlusOne,                // This needs to be the last line, so we can calculate the value of Latest below
		Latest = LatestPlusOne - 1    // The latest plugin descriptor version
	}

	// In-memory representation of a .uplugin file
	// Keep Sync with .uplugin files, In PROJECTS_API, FProjectDescriptor.h/cpp FModuleDescriptor.h/cpp, FPluginsDescriptor.h/cpp
	public class PluginDescriptor
	{
		// Descriptor version number
		public int FileVersion;

		// Version number for the plugin.
		// The version number must increase with every version of the plugin,
		// so that the system can determine whether one version of a plugin is newer than another,
		// or to enforce other requirements.
		// This version number is not displayed in front-facing UI.
		// Use the VersionName for that.
		public int Version;

		// Name of the version for this plugin.
		// This is the front-facing part of the version number.
		// It doesn't need to match the version number numerically,
		// but should be updated when the version number is increased accordingly.
		public string VersionName;
		public string FriendlyName;
		public string Description; // Description of the plugin
		public string Category;// The name of the category this plugin
		public string CreatedBy; // The company or individual who created this plugin.
		public string CreatedByURL; // Hyperlink URL string for the company or individual who created this plugin.
		public string DocsURL; // Documentation URL string.

		// This URL will be embedded into projects that enable this plugin,
		// so we can redirect to the marketplace if a user doesn't have it installed.
		public string MarketplaceURL;

		public string SupportURL; // Support URL/email for this plugin.
		public string EngineVersion; // Sets the version of the engine that this plugin is compatible with.
		public bool bIsPluginExtension; // If true, this plugin from a platform extension extending another plugin

		// List of platforms supported by this plugin.
		// This list will be copied to any plugin reference from a project file,
		// to allow filtering entire plugins from staged builds.
		public List<BuildTargetPlatform> SupportedTargetPlatforms;

		// List of programs supported by this plugin.
		public string[] SupportedPrograms;

		// List of all modules associated with this plugin
		public List<ModuleDescriptor> Modules;

		// List of all localization targets associated with this plugin
		public LocalizationTargetDescriptor[] LocalizationTargets;
		
		// Whether this plugin should be enabled by default for all projects
		public Nullable<bool> bEnabledByDefault;

		// Can this plugin contain content?
		public bool bCanContainContent;

		// Marks the plugin as beta in the UI
		public bool bIsBetaVersion;
		
		// Marks the plugin as experimental in the UI
		public bool bIsExperimentalVersion;

		// Set for plugins which are installed
		public bool bInstalled;

		// For plugins that are under a platform folder (eg. /PS4/),
		// determines whether compiling the plugin requires the build platform and/or SDK to be available
		public bool bRequiresBuildPlatform;
		
		// When true, this plugin's modules will not be loaded automatically nor will it's content be mounted automatically.
		// It will load/mount when explicitly requested and LoadingPhases will be ignored
		public bool bExplicitlyLoaded;

		// Set of pre-build steps to execute, keyed by host platform name.
		public CustomBuildSteps PreBuildSteps;
		
		// Set of post-build steps to execute, keyed by host platform name.
		public CustomBuildSteps PostBuildSteps;

		// Additional plugins that this plugin depends on
		public List<PluginReferenceDescriptor> Plugins;

		// Private constructor.
		// This object should not be created directly; read it from disk using FromFile() instead.
		private PluginDescriptor()
		{
			FileVersion = (int)PluginDescriptorVersion.Latest;
		}

		// Reads a plugin descriptor from a json object
		public PluginDescriptor(JsonObject RawObject)
		{
			// Read the version
			if (!RawObject.TryGetIntegerField(nameof(PluginDescriptor.FileVersion), out FileVersion))
			{
				throw new BuildException("Plugin descriptor does not contain a valid FileVersion entry");
			}

			// Check it's not newer than the latest version we can parse
			if ((int)PluginDescriptorVersion.Latest < FileVersion)
			{
				throw new BuildException("Plugin descriptor appears to be in a newer version ({0}) of the file format that we can load (max version: {1}).", FileVersion, (int)PluginDescriptorVersion.Latest);
			}

			// Read the other fields
			RawObject.TryGetIntegerField(nameof(PluginDescriptor.Version), out Version);
			RawObject.TryGetStringField(nameof(PluginDescriptor.VersionName), out VersionName);
			RawObject.TryGetStringField(nameof(PluginDescriptor.FriendlyName), out FriendlyName);
			RawObject.TryGetStringField(nameof(PluginDescriptor.Description), out Description);

			if (!RawObject.TryGetStringField(nameof(PluginDescriptor.Category), out Category))
			{
				// Category used to be called CategoryPath in .uplugin files
				RawObject.TryGetStringField(nameof(PluginDescriptor.Category), out Category);
			}

			// Due to a difference in command line parsing between Windows and Mac, we shipped a few Mac samples containing
			// a category name with escaped quotes. Remove them here to make sure we can list them in the right category.
			if (Category != null          && 
				0 < Category.Length       && 
				Category.StartsWith("\"") && 
				Category.EndsWith("\""))
			{
				Category = Category.Substring(1, Category.Length - 2);
			}

			RawObject.TryGetStringField(nameof(PluginDescriptor.CreatedBy), out CreatedBy);
			RawObject.TryGetStringField(nameof(PluginDescriptor.CreatedByURL), out CreatedByURL);
			RawObject.TryGetStringField(nameof(PluginDescriptor.DocsURL), out DocsURL);
			RawObject.TryGetStringField(nameof(PluginDescriptor.MarketplaceURL), out MarketplaceURL);
			RawObject.TryGetStringField(nameof(PluginDescriptor.SupportURL), out SupportURL);
			RawObject.TryGetStringField(nameof(PluginDescriptor.EngineVersion), out EngineVersion);
			RawObject.TryGetStringArrayField(nameof(PluginDescriptor.SupportedPrograms), out SupportedPrograms);
			RawObject.TryGetBoolField(nameof(PluginDescriptor.bIsPluginExtension), out bIsPluginExtension);

			try
			{
#pragma warning disable IDE0018 // Inline variable declaration
                string[] SupportedTargetPlatformNames;

                if (RawObject.TryGetStringArrayField(nameof(PluginDescriptor.SupportedTargetPlatforms), out SupportedTargetPlatformNames))
				{
					this.SupportedTargetPlatforms = Array.ConvertAll(SupportedTargetPlatformNames, x => BuildTargetPlatform.Parse(x)).ToList();
				}
			}
			catch (BuildException Ex)
			{
				ExceptionUtils.AddContext(Ex, "while parsing SupportedTargetPlatforms in plugin with FriendlyName '{0}'", FriendlyName);
				throw;
			}

			JsonObject[] Modules;

			if (RawObject.TryGetObjectArrayField(nameof(PluginDescriptor.Modules), out Modules))
			{
				this.Modules = Array.ConvertAll(Modules, x => ModuleDescriptor.FromJsonObject(x)).ToList();
			}

			JsonObject[] LocalizationTargets;

			if (RawObject.TryGetObjectArrayField(nameof(PluginDescriptor.LocalizationTargets), out LocalizationTargets))
			{
				this.LocalizationTargets = Array.ConvertAll(LocalizationTargets, x => LocalizationTargetDescriptor.FromJsonObject(x));
			}

			bool bEnabledByDefault;

			if (RawObject.TryGetBoolField(nameof(PluginDescriptor.bEnabledByDefault), out bEnabledByDefault))
			{
				this.bEnabledByDefault = bEnabledByDefault;
			}

			RawObject.TryGetBoolField(nameof(PluginDescriptor.bCanContainContent), out bCanContainContent);
			RawObject.TryGetBoolField(nameof(PluginDescriptor.bIsBetaVersion), out bIsBetaVersion);
			RawObject.TryGetBoolField(nameof(PluginDescriptor.bIsExperimentalVersion), out bIsExperimentalVersion);
			RawObject.TryGetBoolField(nameof(PluginDescriptor.bInstalled), out bInstalled);

			bool bCanBeUsedWithHeaderTool;

			if (RawObject.TryGetBoolField(nameof(bCanBeUsedWithHeaderTool), out bCanBeUsedWithHeaderTool) && bCanBeUsedWithHeaderTool)
			{
				Array.Resize(ref SupportedPrograms, (SupportedPrograms == null) ? 1 : SupportedPrograms.Length + 1);
				SupportedPrograms[SupportedPrograms.Length - 1] = "HeaderTool";
			}

			RawObject.TryGetBoolField(nameof(PluginDescriptor.bRequiresBuildPlatform), out bRequiresBuildPlatform);
			RawObject.TryGetBoolField(nameof(PluginDescriptor.bExplicitlyLoaded), out bExplicitlyLoaded);

			CustomBuildSteps.TryRead(RawObject, nameof(PluginDescriptor.PreBuildSteps), out PreBuildSteps);
			CustomBuildSteps.TryRead(RawObject, nameof(PluginDescriptor.PostBuildSteps), out PostBuildSteps);

			JsonObject[] Plugins;

			if (RawObject.TryGetObjectArrayField(nameof(PluginDescriptor.Plugins), out Plugins))
			{
				this.Plugins = Array.ConvertAll(Plugins, x => PluginReferenceDescriptor.FromJsonObject(x)).ToList();
			}
#pragma warning restore IDE0018 // Inline variable declaration
		}

		// Creates a plugin descriptor from a file on disk
		public static PluginDescriptor FromFile(FileReference FileNameToRead)
		{
			JsonObject RawObject = JsonObject.Read(FileNameToRead);
			try
			{
				PluginDescriptor Descriptor = new PluginDescriptor(RawObject);
				if (Descriptor.Modules != null)
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

		// Saves the descriptor to disk
		public void Save(string FileNameToWriteTo)
		{
			using (JsonWriter Writer = new JsonWriter(FileNameToWriteTo))
			{
				Writer.WriteObjectStart();
				Write(Writer);
				Writer.WriteObjectEnd();
			}
		}

		// Writes the plugin descriptor to an existing Json writer
		public void Write(JsonWriter Writer)
		{
			Writer.WriteValue(nameof(PluginDescriptor.FileVersion), (int)ProjectDescriptorVersion.Latest);
			Writer.WriteValue(nameof(PluginDescriptor.Version), Version);
			Writer.WriteValue(nameof(PluginDescriptor.VersionName), VersionName);
			Writer.WriteValue(nameof(PluginDescriptor.FriendlyName), FriendlyName);
			Writer.WriteValue(nameof(PluginDescriptor.Description), Description);
			Writer.WriteValue(nameof(PluginDescriptor.Category), Category);
			Writer.WriteValue(nameof(PluginDescriptor.CreatedBy), CreatedBy);
			Writer.WriteValue(nameof(PluginDescriptor.CreatedByURL), CreatedByURL);
			Writer.WriteValue(nameof(PluginDescriptor.DocsURL), DocsURL);
			Writer.WriteValue(nameof(PluginDescriptor.MarketplaceURL), MarketplaceURL);
			Writer.WriteValue(nameof(PluginDescriptor.SupportURL), SupportURL);
			if(!String.IsNullOrEmpty(EngineVersion))
			{
				Writer.WriteValue(nameof(PluginDescriptor.EngineVersion), EngineVersion);
			}
			if(bEnabledByDefault.HasValue)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bEnabledByDefault), bEnabledByDefault.Value);
			}
			Writer.WriteValue(nameof(PluginDescriptor.bCanContainContent), bCanContainContent);
			if (bIsBetaVersion)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bIsBetaVersion), bIsBetaVersion);
			}
			if (bIsExperimentalVersion)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bIsExperimentalVersion), bIsExperimentalVersion);
			}
			if (bInstalled)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bInstalled), bInstalled);
			}

			if(bRequiresBuildPlatform)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bRequiresBuildPlatform), bRequiresBuildPlatform);
			}

			if (bExplicitlyLoaded)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bExplicitlyLoaded), bExplicitlyLoaded);
			}

			if(SupportedTargetPlatforms != null && 0 < SupportedTargetPlatforms.Count)
			{
				Writer.WriteStringArrayField(nameof(PluginDescriptor.SupportedTargetPlatforms), SupportedTargetPlatforms.Select<BuildTargetPlatform, string>(x => x.ToString()).ToArray());
			}

			if (SupportedPrograms != null && 0 < SupportedPrograms.Length)
			{
				Writer.WriteStringArrayField(nameof(PluginDescriptor.SupportedPrograms), SupportedPrograms);
			}
			if (bIsPluginExtension)
			{
				Writer.WriteValue(nameof(PluginDescriptor.bIsPluginExtension), bIsPluginExtension);
			}

			if (Modules != null && 0 < Modules.Count)
			{
				ModuleDescriptor.WriteArray(Writer, nameof(PluginDescriptor.Modules), Modules.ToArray());
			}

			LocalizationTargetDescriptor.WriteArray(Writer, nameof(PluginDescriptor.LocalizationTargets), LocalizationTargets);

			if(PreBuildSteps != null)
			{
				PreBuildSteps.Write(Writer, nameof(PluginDescriptor.PreBuildSteps));
			}

			if(PostBuildSteps != null)
			{
				PostBuildSteps.Write(Writer, nameof(PluginDescriptor.PostBuildSteps));
			}

			if (Plugins != null && 0 < Plugins.Count)
			{
				PluginReferenceDescriptor.WriteArray(Writer, nameof(PluginDescriptor.Plugins), Plugins.ToArray());
			}
		}
		
		// Determines if this reference enables the plugin for a given platform
		public bool SupportsTargetPlatform(BuildTargetPlatform PlatformToCheck)
		{
			return SupportedTargetPlatforms == null    || 
				   SupportedTargetPlatforms.Count == 0 || 
				   SupportedTargetPlatforms.Contains(PlatformToCheck);
		}
	}
}