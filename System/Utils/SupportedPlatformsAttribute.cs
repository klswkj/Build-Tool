using System;
using BuildToolUtilities;

namespace BuildTool
{
	// Attribute which can be applied to a TargetRules-dervied class to indicate which platforms it supports
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
	public class SupportedPlatformsAttribute : Attribute
	{
		public readonly BuildTargetPlatform[] Platforms;

		// Initialize the attribute with a list of platforms
		public SupportedPlatformsAttribute(params string[] Platforms)
		{
			try
			{
				this.Platforms = Array.ConvertAll(Platforms, x => BuildTargetPlatform.Parse(x));
			}
			catch (BuildException Ex)
			{
				ExceptionUtils.AddContext(Ex, "while parsing a SupportedPlatforms attribute");
				throw;
			}

		}

		// Initialize the attribute with all the platforms in a given category
		public SupportedPlatformsAttribute(BuildPlatformClass Category)
		{
			this.Platforms = Utils.GetPlatformsInClass(Category);
		}
	}
}
