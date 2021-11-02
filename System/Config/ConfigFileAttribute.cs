using System;

namespace BuildTool
{
	// Attribute indicating a value which should be populated from a ini config file
	[AttributeUsage(AttributeTargets.Field)]
	class ConfigFileAttribute : Attribute
	{
		public ConfigHierarchyType ConfigType; // Name of the config hierarchy to read from
		public string SectionName;             // Section containing the setting
		public string KeyName;                 // Key name to search for

		public ConfigFileAttribute(ConfigHierarchyType ConfigTypeToReadFrom, string SectionName, string KeyNameForSearch = null)
		{
			this.ConfigType  = ConfigTypeToReadFrom;
			this.SectionName = SectionName;
			this.KeyName     = KeyNameForSearch;
		}
	}
}
