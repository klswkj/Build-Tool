using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Representation of a reference to a plugin from a project file
	
	[DebuggerDisplay("Name={Name}")]
	public class PluginReferenceDescriptor
	{
		public string Name;           // Name of the plugin
		public bool   bEnabled;       // Whether it should be enabled by default
		public bool   bOptional;      // Whether this plugin is optional, and the game should silently ignore it not being present
		public string Description;    // Description of the plugin for users that do not have it installed.
		public string MarketplaceURL; // URL for this plugin on the marketplace, if the user doesn't have it installed.

		// If enabled, list of platforms for which the plugin should be enabled (or all platforms if blank).
		public List<BuildTargetPlatform> WhitelistPlatforms; 
		public TargetConfiguration[] WhitelistTargetConfigurations;
		public TargetType[] WhitelistTargets;

		// If enabled, list of platforms for which the plugin should be disabled.
		public List<BuildTargetPlatform> BlacklistPlatforms; 
		public TargetConfiguration[] BlacklistTargetConfigurations;
		public TargetType[] BlacklistTargets;

		// The list of supported platforms for this plugin.
		// This field is copied from the plugin descriptor, and supplements the user's whitelisted and blacklisted platforms.
		public List<BuildTargetPlatform> SupportedTargetPlatforms;

		public PluginReferenceDescriptor(string InPluginName, string InMarketplaceURL, bool bInEnabled)
		{
			Name           = InPluginName;
			MarketplaceURL = InMarketplaceURL;
			bEnabled       = bInEnabled;
		}

		public void Write(JsonWriter Writer)
		{
			Writer.WriteObjectStart();
			Writer.WriteValue(nameof(PluginReferenceDescriptor.Name), Name);
			Writer.WriteValue(nameof(PluginReferenceDescriptor.bEnabled), bEnabled);
			if(bEnabled && bOptional)
			{
				Writer.WriteValue(nameof(PluginReferenceDescriptor.bOptional), bOptional);
			}
			if(Description.HasValue())
			{
				Writer.WriteValue(nameof(PluginReferenceDescriptor.Description), Description);
			}
			if(MarketplaceURL.HasValue())
			{
				Writer.WriteValue(nameof(PluginReferenceDescriptor.MarketplaceURL), MarketplaceURL);
			}
			if(WhitelistPlatforms != null && 0 < WhitelistPlatforms.Count)
			{
				Writer.WriteStringArrayField(nameof(PluginReferenceDescriptor.WhitelistPlatforms), WhitelistPlatforms.Select(x => x.ToString()).ToArray());
			}
			if(BlacklistPlatforms != null && 0 < BlacklistPlatforms.Count)
			{
				Writer.WriteStringArrayField(nameof(PluginReferenceDescriptor.BlacklistPlatforms), BlacklistPlatforms.Select(x => x.ToString()).ToArray());
			}
			if (WhitelistTargetConfigurations != null && 0 < WhitelistTargetConfigurations.Length)
			{
				Writer.WriteEnumArrayField(nameof(PluginReferenceDescriptor.WhitelistTargetConfigurations), WhitelistTargetConfigurations);
			}
			if (BlacklistTargetConfigurations != null && 0 < BlacklistTargetConfigurations.Length)
			{
				Writer.WriteEnumArrayField(nameof(PluginReferenceDescriptor.BlacklistTargetConfigurations), BlacklistTargetConfigurations);
			}
			if (WhitelistTargets != null && 0 < WhitelistTargets.Length)
			{
				Writer.WriteEnumArrayField(nameof(PluginReferenceDescriptor.WhitelistTargets), WhitelistTargets);
			}
			if(BlacklistTargets != null && 0 < BlacklistTargets.Length)
			{
				Writer.WriteEnumArrayField(nameof(PluginReferenceDescriptor.BlacklistTargets), BlacklistTargets);
			}
			if(SupportedTargetPlatforms != null && 0 < SupportedTargetPlatforms.Count)
			{
				Writer.WriteStringArrayField(nameof(PluginReferenceDescriptor.SupportedTargetPlatforms), SupportedTargetPlatforms.Select(x => x.ToString()).ToArray());
			}
			Writer.WriteObjectEnd();
		}

		// Write an array of module descriptors
		public static void WriteArray(JsonWriter Writer, string ArrayName, PluginReferenceDescriptor[] Plugins)
		{
			if (Plugins != null && 
				0 < Plugins.Length)
			{
				Writer.WriteArrayStart(ArrayName);
				foreach (PluginReferenceDescriptor Plugin in Plugins)
				{
					Plugin.Write(Writer);
				}
				Writer.WriteArrayEnd();
			}
		}

		// Construct a PluginReferenceDescriptor from a Json object
		public static PluginReferenceDescriptor FromJsonObject(JsonObject PluginReferenceDescriptorJSON)
		{
			JsonObject RawObject = PluginReferenceDescriptorJSON;

			PluginReferenceDescriptor Descriptor = new PluginReferenceDescriptor(RawObject.GetStringField(nameof(PluginReferenceDescriptor.Name)), null, RawObject.GetBoolField("Enabled"));
			RawObject.TryGetBoolField(nameof(PluginReferenceDescriptor.bOptional), out Descriptor.bOptional);
			RawObject.TryGetStringField(nameof(PluginReferenceDescriptor.Description), out Descriptor.Description);
			RawObject.TryGetStringField(nameof(PluginReferenceDescriptor.MarketplaceURL), out Descriptor.MarketplaceURL);
			RawObject.TryGetStringArrayField(nameof(PluginReferenceDescriptor.WhitelistPlatforms), out string[] WhitelistPlatformNames);
			RawObject.TryGetStringArrayField(nameof(PluginReferenceDescriptor.BlacklistPlatforms), out string[] BlacklistPlatformNames);
			RawObject.TryGetEnumArrayField<TargetConfiguration>(nameof(PluginReferenceDescriptor.WhitelistTargetConfigurations), out Descriptor.WhitelistTargetConfigurations);
			RawObject.TryGetEnumArrayField<TargetConfiguration>(nameof(PluginReferenceDescriptor.BlacklistTargetConfigurations), out Descriptor.BlacklistTargetConfigurations);
			RawObject.TryGetEnumArrayField<TargetType>(nameof(PluginReferenceDescriptor.WhitelistTargets), out Descriptor.WhitelistTargets);
			RawObject.TryGetEnumArrayField<TargetType>(nameof(PluginReferenceDescriptor.BlacklistTargets), out Descriptor.BlacklistTargets);
			RawObject.TryGetStringArrayField(nameof(PluginReferenceDescriptor.SupportedTargetPlatforms), out string[] SupportedTargetPlatformNames);

			try
			{
				// convert string array to BuildTargetPlatform arrays
				if (WhitelistPlatformNames != null)
				{
					Descriptor.WhitelistPlatforms = WhitelistPlatformNames.Select(x => BuildTargetPlatform.Parse(x)).ToList();
				}
				if (BlacklistPlatformNames != null)
				{
					Descriptor.BlacklistPlatforms = BlacklistPlatformNames.Select(x => BuildTargetPlatform.Parse(x)).ToList();
				}
				if (SupportedTargetPlatformNames != null)
				{
					Descriptor.SupportedTargetPlatforms = SupportedTargetPlatformNames.Select(x => BuildTargetPlatform.Parse(x)).ToList();
				}
			}
			catch (BuildException Ex)
			{
				ExceptionUtils.AddContext(Ex, "while parsing PluginReferenceDescriptor {0}", Descriptor.Name);
				throw;
			}

			return Descriptor;
		}

		public bool IsEnabledForPlatform(BuildTargetPlatform PlatformToCheck)
		{
			if (!bEnabled)
			{
				return false;
			}
			if (WhitelistPlatforms != null   && 
				0 < WhitelistPlatforms.Count && 
				!WhitelistPlatforms.Contains(PlatformToCheck))
			{
				return false;
			}
			if (BlacklistPlatforms != null && 
				BlacklistPlatforms.Contains(PlatformToCheck))
			{
				return false;
			}
			return true;
		}

		public bool IsEnabledForTargetConfiguration(TargetConfiguration TargetConfigurationToCheck)
		{
			if (!bEnabled)
			{
				return false;
			}
			if (WhitelistTargetConfigurations != null    && 
				0 < WhitelistTargetConfigurations.Length && 
				!WhitelistTargetConfigurations.Contains(TargetConfigurationToCheck))
			{
				return false;
			}
			if (BlacklistTargetConfigurations != null && 
				BlacklistTargetConfigurations.Contains(TargetConfigurationToCheck))
			{
				return false;
			}
			return true;
		}

		public bool IsEnabledForTarget(TargetType TargetToCheck)
		{
			if (!bEnabled)
			{
				return false;
			}
			if (WhitelistTargets != null    && 
				0 < WhitelistTargets.Length && 
				!WhitelistTargets.Contains(TargetToCheck))
			{
				return false;
			}
			if (BlacklistTargets != null && 
				BlacklistTargets.Contains(TargetToCheck))
			{
				return false;
			}
			return true;
		}

		public bool IsSupportedTargetPlatform(BuildTargetPlatform PlatformToCheck)
		{
			return SupportedTargetPlatforms == null    || 
				   SupportedTargetPlatforms.Count == 0 || 
				   SupportedTargetPlatforms.Contains(PlatformToCheck);
		}
	}
}
